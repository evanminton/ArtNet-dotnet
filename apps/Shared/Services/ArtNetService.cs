using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using ArtNet.Networking;

namespace ArtNet.Shared.Services;

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
    private ArtNetNode? _node;
    private IDispatcher? _dispatcher;
    private IDispatcherTimer? _timer;
    private int _packetCount;
    private DateTime _rateWindowStart = DateTime.UtcNow;
    private double _lastRate;
    private volatile ArtNetUniverseEventArgs? _monitorFrame;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly MulticastLockHolder _multicastLock = new();

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

    /// <summary>Universe announced as the "Monitor" output port (DMX tab "Subscribe"); kept across restarts.</summary>
    public PortAddress? MonitorPort { get; set; }

    /// <summary>Raised on the UI thread (throttled) with the latest merged frame of <see cref="MonitorAddress"/>.</summary>
    public event EventHandler<ArtNetUniverseEventArgs>? MonitorFrame;

    /// <summary>Raised on the UI thread when the node starts or stops.</summary>
    public event EventHandler? RunningChanged;

    /// <summary>Raised on the UI thread after a controller reprogrammed this node and <see cref="Settings"/> was saved.</summary>
    public event EventHandler? SettingsChanged;

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Node settings built from <see cref="Settings"/>.</summary>
    public ArtNetNodeSettings BuildNodeSettings() => BuildNodeSettings(out _);

    /// <summary>
    /// Node settings built from <see cref="Settings"/>. <paramref name="warning"/> explains a saved setting that could
    /// not be applied (an interface that no longer exists, an invalid broadcast address); null when all applied.
    /// </summary>
    public ArtNetNodeSettings BuildNodeSettings(out string? warning)
    {
        var s = Settings;
        var warnings = new List<string>();
        var ns = new ArtNetNodeSettings
        {
            ShortName = string.IsNullOrWhiteSpace(s.ShortName) ? AppBrand.Name : s.ShortName.Trim(),
            LongName = s.LongName,
            Style = s.Style,
            SendPolls = s.SendPolls,
            PollInterval = TimeSpan.FromMilliseconds(Math.Clamp(s.PollIntervalMs, AppSettings.MinPollIntervalMs, AppSettings.MaxPollIntervalMs)),
            DefaultMergeMode = s.MergeMode,
            BroadcastDmxWithoutSubscribers = s.BroadcastDmxWithoutSubscribers,
            EnableSync = s.EnableSync,
            AcceptRemoteProgramming = s.AcceptRemoteProgramming,
            DmxKeepAlive = TimeSpan.FromMilliseconds(Math.Max(0, s.KeepAliveMs)),
            FirmwareVersion = 0x0100,
        };
        foreach (var a in SafeUniverses(s.OutputUniverses)) ns.Ports.Add(new ArtNetPortConfig(ArtNetPortKind.Output, a) { MergeMode = s.MergeMode });
        foreach (var a in SafeUniverses(s.InputUniverses)) ns.Ports.Add(ArtNetPortConfig.Input(a));
        if (MonitorPort is { } monitor) ns.Ports.Add(ArtNetPortConfig.Output(monitor, MonitorPortName));
        if (!string.IsNullOrWhiteSpace(s.ProductUrl)) ns.DataReplies[ArtNetDataRequestCode.UrlProduct] = s.ProductUrl.Trim();
        if (!string.IsNullOrWhiteSpace(s.InterfaceAddress))
        {
            // A saved interface can disappear (DHCP change, adapter unplugged): fall back to automatic rather than
            // advertising a dead IP and broadcasting to the wrong network.
            if (AppSettings.TryParseIPv4(s.InterfaceAddress, out var local) && ArtNetNetworkInterface.Find(local) is { } nic)
            {
                ns.LocalAddress = local;
                ns.BroadcastAddress = nic.Broadcast;
            }
            else warnings.Add($"The saved interface {s.InterfaceAddress} is not available; using the default interface.");
        }
        if (!string.IsNullOrWhiteSpace(s.BroadcastAddress))
        {
            if (AppSettings.TryParseIPv4(s.BroadcastAddress, out var b)) ns.BroadcastAddress = b;
            else warnings.Add($"The broadcast address override '{s.BroadcastAddress}' is not a valid IPv4 address and was ignored.");
        }
        warning = warnings.Count == 0 ? null : string.Join(" ", warnings);
        return ns;
    }

    /// <summary>Name of the port the DMX tab announces for <see cref="MonitorPort"/>; always the last port.</summary>
    public const string MonitorPortName = "Monitor";

    private static IReadOnlyList<PortAddress> SafeUniverses(string text)
    {
        try { return AppSettings.ParseUniverses(text); }
        catch (FormatException) { return []; }
    }

    /// <summary>Starts the node. Call from the UI thread.</summary>
    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StartCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Stops the node. Call from the UI thread.</summary>
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Stops and starts the node as one step, so no other start or stop can run in between.</summary>
    public async Task RestartAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            await StopCoreAsync();
            await StartCoreAsync();
        }
        finally { _lifecycle.Release(); }
    }

    public Task ToggleAsync() => IsRunning ? StopAsync() : StartAsync();

    /// <summary>
    /// Stops the node synchronously, for app shutdown: the window is gone before an awaited stop would finish. The
    /// library's stop does not need the UI thread, so blocking here cannot deadlock.
    /// </summary>
    public void Shutdown()
    {
        if (!_lifecycle.Wait(TimeSpan.FromSeconds(2))) return;
        try
        {
            var node = _node;
            _node = null;
            _timer?.Stop();
            _timer = null;
            if (node is null) return;
            try { node.Dispose(); } catch { /* shutting down */ }
            _multicastLock.Release();
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StartCoreAsync()
    {
        if (_node is not null) return;
        _dispatcher = Dispatcher.GetForCurrentThread() ?? Application.Current?.Dispatcher;

        var node = new ArtNetNode(BuildNodeSettings(out var warning));
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
        node.ConfigurationChanged += (_, _) => SaveRemoteProgramming(node);
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
            var message = ex is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse or System.Net.Sockets.SocketError.AccessDenied }
                ? $"{ex.Message} – another application holds UDP 6454 exclusively, or the firewall blocks it ({AppBrand.FirewallHint})."
                : ex.Message;
            LastError = message;
            StatusText = $"Could not start: {message}";
            return;
        }

        _node = node;
        IsRunning = true;
        LastError = warning ?? string.Empty;
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

    private async Task StopCoreAsync()
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

    /// <summary>
    /// Writes names and port addresses a controller programmed (ArtAddress / ArtInput) back to <see cref="Settings"/>,
    /// so they survive a restart. Raised on a network thread.
    /// </summary>
    private void SaveRemoteProgramming(ArtNetNode node)
    {
        ArtNetPortConfig[] ports = [];
        node.UpdatePorts(current => ports = [.. current]); // atomic read of the ports the network thread modifies
        string shortName = node.Settings.ShortName, longName = node.Settings.LongName;
        Ui(() =>
        {
            if (!ReferenceEquals(node, _node)) return; // stopped or restarted meanwhile
            // The DMX tab's Monitor port is always last and is not part of the saved universes.
            var own = MonitorPort is null || ports.Length == 0 ? ports : ports[..^1];
            var s = Settings;
            s.ShortName = shortName;
            s.LongName = longName;
            s.OutputUniverses = FormatUniverses(own, ArtNetPortKind.Output);
            s.InputUniverses = FormatUniverses(own, ArtNetPortKind.Input);
            s.Save();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private static string FormatUniverses(IEnumerable<ArtNetPortConfig> ports, ArtNetPortKind kind) =>
        string.Join(", ", ports.Where(p => p.Kind == kind).Select(p => $"{p.Address.Net}:{p.Address.SubNet}:{p.Address.Universe}"));

    /// <summary>The running node, or throws with a readable message.</summary>
    public ArtNetNode RequireNode() => _node ?? throw new InvalidOperationException($"Start the node first ({AppBrand.StartHint}).");

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
