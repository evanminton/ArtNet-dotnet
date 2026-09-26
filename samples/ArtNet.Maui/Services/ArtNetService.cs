using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using ArtNet.Networking;

namespace ArtNet.Maui.Services;

/// <summary>One line of the packet log.</summary>
public sealed class PacketLogEntry
{
    public PacketLogEntry(ArtNetPacket packet, IPEndPoint from)
    {
        Packet = packet;
        Received = DateTime.Now;
        Source = from.Address.ToString();
        Summary = packet.Summary;
        TypeText = packet.Title;
    }

    public ArtNetPacket Packet { get; }
    public DateTime Received { get; }
    public string TimeText => Received.ToString("HH:mm:ss.fff");
    public string Source { get; }
    public string Summary { get; }
    public string TypeText { get; }
    public string Details => ArtNetFormatter.Format(Packet);
}

/// <summary>A show-control / diagnostics event shown on the Show tab.</summary>
public sealed record ShowEvent(string Time, string Source, string Text);

/// <summary>
/// Owns the ArtNetNode for the app. Network events arrive on background threads; this service marshals them to the
/// UI thread and batches the packet log and DMX monitor into ~20 UI updates per second.
/// </summary>
public sealed class ArtNetService : ObservableObject
{
    private const int MaxLogEntries = 400;
    private const int MaxShowEvents = 200;
    private static readonly TimeSpan UiTick = TimeSpan.FromMilliseconds(50);

    private readonly ConcurrentQueue<PacketLogEntry> _pendingLog = new();
    private readonly ConcurrentQueue<ShowEvent> _pendingShow = new();
    private readonly MulticastLockHolder _multicastLock = new();
    private ArtNetNode? _node;
    private IDispatcher? _dispatcher;
    private IDispatcherTimer? _timer;
    private int _packetCount;
    private DateTime _rateWindowStart = DateTime.UtcNow;
    private double _lastRate;
    private volatile ArtNetUniverseEventArgs? _monitorFrame;

    public ArtNetService(AppSettings settings) => Settings = settings;

    public AppSettings Settings { get; }
    public ArtNetNode? Node => _node;

    public ObservableCollection<ArtNetRemoteNode> Nodes { get; } = [];
    public ObservableCollection<PacketLogEntry> PacketLog { get; } = [];
    public ObservableCollection<ShowEvent> ShowEvents { get; } = [];

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (Set(ref _isRunning, value)) OnPropertyChanged(nameof(StartStopText)); }
    }

    public string StartStopText => IsRunning ? "Stop" : "Start";

    private string _statusText = "Stopped";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string _lastError = string.Empty;
    public string LastError { get => _lastError; set => Set(ref _lastError, value); }

    private string _lastTimeCode = "--:--:--:--";
    public string LastTimeCode { get => _lastTimeCode; private set => Set(ref _lastTimeCode, value); }

    public bool LogPaused { get; set; }
    public bool LogDmx { get; set; }
    public bool LogPolls { get; set; } = true;
    public ArtNetOpCode? LogFilter { get; set; }

    /// <summary>Universe shown on the DMX tab.</summary>
    public PortAddress MonitorAddress { get; set; } = new(1);

    /// <summary>Raised on the UI thread (throttled) with the latest merged frame of <see cref="MonitorAddress"/>.</summary>
    public event EventHandler<ArtNetUniverseEventArgs>? MonitorFrame;

    /// <summary>Raised on the UI thread when the node starts or stops.</summary>
    public event EventHandler? RunningChanged;

    // ------------------------------------------------------------------ lifecycle

    public ArtNetNodeSettings BuildNodeSettings()
    {
        var s = Settings;
        var ns = new ArtNetNodeSettings
        {
            ShortName = string.IsNullOrWhiteSpace(s.ShortName) ? "Art-Net Monitor" : s.ShortName.Trim(),
            LongName = s.LongName,
            Style = s.Style,
            SendPolls = s.SendPolls,
            PollInterval = TimeSpan.FromMilliseconds(Math.Clamp(s.PollIntervalMs, 1000, 10000)),
            DefaultMergeMode = s.MergeMode,
            BroadcastDmxWithoutSubscribers = s.BroadcastDmxWithoutSubscribers,
            EnableSync = s.EnableSync,
            AcceptRemoteProgramming = s.AcceptRemoteProgramming,
            DmxKeepAlive = TimeSpan.FromMilliseconds(Math.Max(0, s.KeepAliveMs)),
            FirmwareVersion = 0x0100,
        };
        foreach (var a in SafeUniverses(s.OutputUniverses)) ns.Ports.Add(new ArtNetPortConfig(ArtNetPortKind.Output, a) { MergeMode = s.MergeMode });
        foreach (var a in SafeUniverses(s.InputUniverses)) ns.Ports.Add(ArtNetPortConfig.Input(a));
        if (!string.IsNullOrWhiteSpace(s.ProductUrl)) ns.DataReplies[ArtNetDataRequestCode.UrlProduct] = s.ProductUrl.Trim();
        if (IPAddress.TryParse(s.InterfaceAddress, out var local))
        {
            ns.LocalAddress = local;
            ns.BroadcastAddress = ArtNetNetworkInterface.Find(local)?.Broadcast;
        }
        if (IPAddress.TryParse(s.BroadcastAddress, out var b)) ns.BroadcastAddress = b;
        return ns;
    }

    private static IReadOnlyList<PortAddress> SafeUniverses(string text)
    {
        try { return AppSettings.ParseUniverses(text); }
        catch (FormatException) { return []; }
    }

    /// <summary>Starts the node. Call from the UI thread.</summary>
    public async Task StartAsync()
    {
        if (_node is not null) return;
        _dispatcher = Dispatcher.GetForCurrentThread() ?? Application.Current?.Dispatcher;

        var node = new ArtNetNode(BuildNodeSettings());
        node.NodeDiscovered += (_, e) => Ui(() => { if (!Nodes.Contains(e.Node)) Nodes.Add(e.Node); UpdateStatus(); });
        node.NodeLost += (_, e) => Ui(() => { Nodes.Remove(e.Node); UpdateStatus(); });
        node.PacketReceived += OnPacket;
        node.UniverseChanged += (_, e) => { if (e.Address == MonitorAddress) _monitorFrame = e; };
        node.TimeCodeReceived += (_, e) => { var t = e.Packet.TimeText + " " + e.Packet.Type.ToDisplayName(); Ui(() => LastTimeCode = t); };
        node.TriggerReceived += (_, e) => Show(e.RemoteEndPoint, e.Packet.Summary);
        node.CommandReceived += (_, e) => Show(e.RemoteEndPoint, e.Packet.Summary);
        node.DiagDataReceived += (_, e) => Show(e.RemoteEndPoint, e.Packet.Summary);
        node.AddressReceived += (_, e) => Show(e.RemoteEndPoint, "Programmed us: " + e.Packet.Summary);
        node.InputReceived += (_, e) => Show(e.RemoteEndPoint, "Programmed us: " + e.Packet.Summary);
        node.VlcReceived += (_, e) => Show(e.RemoteEndPoint, e.Packet.Summary);
        node.Error += (_, e) => Ui(() => LastError = e.Exception.Message);

        try
        {
            _multicastLock.Acquire();
            await node.StartAsync();
        }
        catch (Exception ex)
        {
            _multicastLock.Release();
            await node.DisposeAsync();
            LastError = ex.Message;
            StatusText = $"Could not start: {ex.Message}";
            return;
        }

        _node = node;
        IsRunning = true;
        LastError = string.Empty;
        UpdateStatus();
        RunningChanged?.Invoke(this, EventArgs.Empty);

        if (_dispatcher is not null)
        {
            _timer = _dispatcher.CreateTimer();
            _timer.Interval = UiTick;
            _timer.Tick += (_, _) => OnUiTick();
            _timer.Start();
        }
    }

    public async Task StopAsync()
    {
        var node = _node;
        if (node is null) return;
        _node = null;
        _timer?.Stop();
        _timer = null;
        try { await node.DisposeAsync(); } catch { /* shutting down */ }
        _multicastLock.Release();
        Ui(() =>
        {
            Nodes.Clear();
            IsRunning = false;
            StatusText = "Stopped";
            RunningChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    public Task ToggleAsync() => IsRunning ? StopAsync() : StartAsync();

    /// <summary>The running node, or throws with a readable message.</summary>
    public ArtNetNode RequireNode() => _node ?? throw new InvalidOperationException("Start the node first (Nodes tab).");

    public void ClearLog()
    {
        while (_pendingLog.TryDequeue(out _)) { }
        PacketLog.Clear();
    }

    public void ClearShowEvents()
    {
        while (_pendingShow.TryDequeue(out _)) { }
        ShowEvents.Clear();
    }

    // ------------------------------------------------------------------ network events (background threads)

    private void OnPacket(object? sender, ArtNetPacketEventArgs<ArtNetPacket> e)
    {
        Interlocked.Increment(ref _packetCount);
        if (LogPaused) return;
        var p = e.Packet;
        if (LogFilter is { } f) { if (p.OpCode != f) return; }
        else
        {
            if (!LogDmx && p.OpCode is ArtNetOpCode.Dmx or ArtNetOpCode.Sync) return;
            if (!LogPolls && p.OpCode is ArtNetOpCode.Poll or ArtNetOpCode.PollReply) return;
        }
        _pendingLog.Enqueue(new PacketLogEntry(p, e.RemoteEndPoint));
        while (_pendingLog.Count > MaxLogEntries && _pendingLog.TryDequeue(out _)) { }
    }

    private void Show(IPEndPoint from, string text) =>
        _pendingShow.Enqueue(new ShowEvent(DateTime.Now.ToString("HH:mm:ss.fff"), from.Address.ToString(), text));

    // ------------------------------------------------------------------ UI tick

    private void OnUiTick()
    {
        if (!LogPaused)
        {
            int added = 0;
            while (added++ < 100 && _pendingLog.TryDequeue(out var entry)) PacketLog.Insert(0, entry);
            while (PacketLog.Count > MaxLogEntries) PacketLog.RemoveAt(PacketLog.Count - 1);
        }
        while (_pendingShow.TryDequeue(out var s)) ShowEvents.Insert(0, s);
        while (ShowEvents.Count > MaxShowEvents) ShowEvents.RemoveAt(ShowEvents.Count - 1);

        var frame = _monitorFrame;
        if (frame is not null)
        {
            _monitorFrame = null;
            MonitorFrame?.Invoke(this, frame);
        }

        var now = DateTime.UtcNow;
        if (now - _rateWindowStart >= TimeSpan.FromSeconds(1))
        {
            UpdateStatus(Interlocked.Exchange(ref _packetCount, 0) / (now - _rateWindowStart).TotalSeconds);
            _rateWindowStart = now;
        }
    }

    private void UpdateStatus(double? packetsPerSecond = null)
    {
        if (packetsPerSecond is { } r) _lastRate = r;
        var node = _node;
        StatusText = node is null
            ? "Stopped"
            : $"{node.Settings.ShortName} · broadcast {node.BroadcastAddress} · {Nodes.Count} node(s) · {_lastRate:0} pkt/s" +
              (node.IsSynchronous ? " · sync" : "");
    }

    public void Ui(Action action)
    {
        var d = _dispatcher;
        if (d is null || !d.IsDispatchRequired) action();
        else d.Dispatch(action);
    }
}
