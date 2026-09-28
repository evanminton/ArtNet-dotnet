using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ArtNet.Networking;

/// <summary>
/// An Art-Net 4 device on UDP 6454. Acts as a controller (polls every 2.5 s, tracks nodes, unicasts ArtDmx to
/// subscribers, keeps universes alive, sends ArtSync) and as a node (answers ArtPoll with one ArtPollReply per port,
/// receives and merges ArtDmx, honours ArtSync, ArtAddress, ArtInput and ArtDataRequest). Every packet type can be
/// sent and is raised as a typed event.
/// </summary>
/// <example>
/// <code>
/// await using var node = new ArtNetNode(new ArtNetNodeSettings { ShortName = "My App" });
/// node.NodeDiscovered += (_, e) => Console.WriteLine(e.Node);
/// await node.StartAsync();
/// await node.SendDmxAsync(new PortAddress(1), levels);
/// </code>
/// </example>
public sealed class ArtNetNode : IAsyncDisposable, IDisposable
{
    private sealed class OutputUniverse
    {
        public byte[] Data = new byte[ArtNetConstants.DmxChannels];
        public byte Sequence;
        public byte Physical;
        public DateTime LastSent;
        public int LastTargets;
        /// <summary>Serialises build + send so keep-alive frames never overtake newer frames on the wire.</summary>
        public readonly SemaphoreSlim SendGate = new(1, 1);
    }

    private sealed class Waiter(Func<ArtNetPacket, IPEndPoint, bool> match)
    {
        public Func<ArtNetPacket, IPEndPoint, bool> Match { get; } = match;
        public TaskCompletionSource<ArtNetPacket> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly record struct DiagSubscriber(bool Unicast, ArtNetDiagnosticPriority Priority, DateTime Since);

    private readonly ConcurrentDictionary<(IPAddress Address, byte Bind), ArtNetRemoteNode> _nodes = new();
    private readonly ConcurrentDictionary<PortAddress, ArtNetUniverse> _universes = new();
    private readonly ConcurrentDictionary<PortAddress, OutputUniverse> _outputs = new();
    private readonly ConcurrentDictionary<PortAddress, ArtNetUniverseEventArgs> _pendingSync = new();
    private readonly ConcurrentDictionary<IPAddress, DiagSubscriber> _diagSubscribers = new();
    private readonly ConcurrentDictionary<IPAddress, DateTime> _changeSubscribers = new();
    private readonly List<Waiter> _waiters = [];
    private readonly List<Task> _loops = [];
    private readonly object _stateLock = new();
    private readonly object _syncLock = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _pollReplyLock = new();

    /// <summary>
    /// The node whose event handler is running synchronously on this thread (no <see cref="ArtNetNodeSettings.EventContext"/>).
    /// Such a thread may be one of the node's loops, so <see cref="StopAsync"/> must not wait for them there.
    /// Deliberately thread-static rather than async-local: work a handler hands to another thread may wait.
    /// </summary>
    [ThreadStatic] private static ArtNetNode? t_handlerNode;
    private HashSet<IPAddress> _localAddresses = [];
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private IPAddress? _lastDmxSource;
    private DateTime _lastSync = DateTime.MinValue;
    private volatile bool _syncActive;
    private int _reportCounter;
    private ArtNetNodeReportCode _reportCode = ArtNetNodeReportCode.PowerOk;
    private string _reportText = "Power on tests successful";
    private ArtNetIndicatorState _indicator = ArtNetIndicatorState.Normal;
    private ArtNetFailsafeState _failsafe = ArtNetFailsafeState.Hold;
    private ArtNetPortAddressAuthority _authority = ArtNetPortAddressAuthority.Unknown;
    private byte _acnPriority = 100;

    public ArtNetNode(ArtNetNodeSettings? settings = null) => Settings = settings ?? new ArtNetNodeSettings();

    /// <summary>
    /// Settings. Names and ports may be changed while running; call <see cref="NotifyChangedAsync"/> afterwards.
    /// While the node is running, change <see cref="ArtNetNodeSettings.Ports"/> only through <see cref="UpdatePorts"/>:
    /// ArtAddress / ArtInput from the network modify the list on the receive thread.
    /// </summary>
    public ArtNetNodeSettings Settings { get; }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    /// <summary>Directed broadcast address in use (resolved at start).</summary>
    public IPAddress BroadcastAddress { get; private set; } = ArtNetConstants.PrimaryBroadcast;

    /// <summary>The local interface used for reporting (resolved at start, may be null).</summary>
    public ArtNetNetworkInterface? Interface { get; private set; }

    /// <summary>Snapshot of discovered devices ordered by address and bind index.</summary>
    public IReadOnlyList<ArtNetRemoteNode> Nodes =>
        _nodes.Values.OrderBy(n => n.Address.GetAddressBytes(), ByteArrayComparer.Instance).ThenBy(n => n.BindIndex).ToArray();

    /// <summary>Universes that have received ArtDmx.</summary>
    public IReadOnlyList<ArtNetUniverse> Universes => _universes.Values.OrderBy(u => u.Address).ToArray();

    /// <summary>Universes this node transmits.</summary>
    public IReadOnlyList<PortAddress> OutputUniverses => _outputs.Keys.OrderBy(a => a).ToArray();

    /// <summary>True while ArtSync is being honoured (an ArtSync arrived within the last 4 s).</summary>
    public bool IsSynchronous => _syncActive && DateTime.UtcNow - _lastSync < ArtNetConstants.SyncTimeout;

    public ArtNetIndicatorState IndicatorState => _indicator;
    public ArtNetFailsafeState FailsafeState => _failsafe;

    /// <summary>Controllers that asked for diagnostics (ArtPoll flag bit 2).</summary>
    public IReadOnlyCollection<IPAddress> DiagnosticsSubscribers => _diagSubscribers.Keys.ToArray();

    // ---------------------------------------------------------------- events

    /// <summary>Every decoded packet (raised before the typed events).</summary>
    public event EventHandler<ArtNetPacketEventArgs<ArtNetPacket>>? PacketReceived;
    public event EventHandler<ArtNetNodeEventArgs>? NodeDiscovered;
    /// <summary>A known node sent an ArtPollReply with different contents.</summary>
    public event EventHandler<ArtNetNodeEventArgs>? NodeUpdated;
    public event EventHandler<ArtNetNodeEventArgs>? NodeLost;
    public event EventHandler<ArtNetPacketEventArgs<ArtPollPacket>>? PollReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtPollReplyPacket>>? PollReplyReceived;
    /// <summary>Every accepted or rejected ArtDmx as received.</summary>
    public event EventHandler<ArtNetPacketEventArgs<ArtDmxPacket>>? DmxReceived;
    /// <summary>Merged output of a universe changed (immediately, or on ArtSync in synchronous mode).</summary>
    public event EventHandler<ArtNetUniverseEventArgs>? UniverseChanged;
    public event EventHandler<ArtNetPacketEventArgs<ArtSyncPacket>>? SyncReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtNzsPacket>>? NzsReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtVlcPacket>>? VlcReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtAddressPacket>>? AddressReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtInputPacket>>? InputReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtIpProgPacket>>? IpProgReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtIpProgReplyPacket>>? IpProgReplyReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtDataRequestPacket>>? DataRequestReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtDataReplyPacket>>? DataReplyReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtDiagDataPacket>>? DiagDataReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtTimeCodePacket>>? TimeCodeReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtCommandPacket>>? CommandReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtTriggerPacket>>? TriggerReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtTodRequestPacket>>? TodRequestReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtTodDataPacket>>? TodDataReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtTodControlPacket>>? TodControlReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtRdmPacket>>? RdmReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtRdmSubPacket>>? RdmSubReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtFirmwareMasterPacket>>? FirmwareMasterReceived;
    public event EventHandler<ArtNetPacketEventArgs<ArtFirmwareReplyPacket>>? FirmwareReplyReceived;
    /// <summary>OpCodes without a dedicated class (media, video, file, directory, time sync).</summary>
    public event EventHandler<ArtNetPacketEventArgs<ArtUnknownPacket>>? UnknownReceived;
    /// <summary>This node's names, ports or state were changed remotely (ArtAddress / ArtInput).</summary>
    public event EventHandler? ConfigurationChanged;
    /// <summary>Non-fatal errors from network threads.</summary>
    public event EventHandler<ArtNetErrorEventArgs>? Error;

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Opens the socket on UDP 6454, starts receiving, polling and keep-alive.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Settings.PollInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Settings.PollInterval must be greater than zero.");
        if (InHandler)
        {
            // Never block a network thread behind a StopAsync that is waiting for that thread.
            if (!_lifecycle.Wait(0))
            {
                if (_cts is not null) return Task.CompletedTask; // running, or a concurrent start is finishing
                throw new InvalidOperationException("The node is stopping.");
            }
        }
        else _lifecycle.Wait(cancellationToken);
        try
        {
            if (_cts is null) Start();
        }
        finally { _lifecycle.Release(); }
        return Task.CompletedTask;
    }

    private bool InHandler => ReferenceEquals(t_handlerNode, this);

    private void Start()
    {
        _localAddresses = GetLocalAddresses();
        Interface = Settings.LocalAddress.Equals(IPAddress.Any)
            ? ArtNetNetworkInterface.GetDefault()
            : ArtNetNetworkInterface.Find(Settings.LocalAddress);
        BroadcastAddress = Settings.BroadcastAddress ?? Interface?.Broadcast ?? ArtNetConstants.PrimaryBroadcast;

        _socket = CreateSocket();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var socket = _socket;
        _loops.Add(Task.Run(() => ReceiveLoopAsync(socket, token), CancellationToken.None));
        _loops.Add(Task.Run(() => PollLoopAsync(token), CancellationToken.None));
        _loops.Add(Task.Run(() => KeepAliveLoopAsync(token), CancellationToken.None));
    }

    /// <summary>
    /// Stops the loops, closes the socket and clears the node list. Safe to call from an event handler raised on a
    /// network thread: the loops are then cancelled but not awaited.
    /// </summary>
    public async Task StopAsync()
    {
        bool inHandler = InHandler;
        if (inHandler)
        {
            if (!_lifecycle.Wait(0)) return; // a stop is already in progress (possibly waiting for this thread)
        }
        else await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var cts = _cts;
            if (cts is null) return;
            cts.Cancel();
            _socket?.Dispose();
            _socket = null;
            Task[] loops = [.. _loops];
            _loops.Clear();
            _cts = null;
            if (!inHandler)
            {
                try { await Task.WhenAll(loops).ConfigureAwait(false); } catch { /* loops end on dispose */ }
                cts.Dispose(); // only once no loop can still observe the token
            }

            lock (_waiters)
            {
                foreach (var w in _waiters) w.Tcs.TrySetCanceled();
                _waiters.Clear();
            }
            foreach (var n in _nodes.Values) Raise(NodeLost, new ArtNetNodeEventArgs(n));
            _nodes.Clear();
            lock (_syncLock)
            {
                _pendingSync.Clear();
                _syncActive = false;
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    // ---------------------------------------------------------------- sending (generic)

    /// <summary>Sends any packet to an endpoint.</summary>
    public async ValueTask SendAsync(ArtNetPacket packet, IPEndPoint target, CancellationToken cancellationToken = default)
    {
        var socket = _socket ?? throw new InvalidOperationException("The node is not running. Call StartAsync first.");
        byte[] bytes = packet.ToArray();
        await socket.SendToAsync(bytes, SocketFlags.None, target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Unicasts a packet to a device's IP on port 6454.</summary>
    public ValueTask SendAsync(ArtNetPacket packet, IPAddress target, CancellationToken cancellationToken = default) =>
        SendAsync(packet, new IPEndPoint(target, Settings.Port), cancellationToken);

    /// <summary>Unicasts a packet to a discovered node.</summary>
    public ValueTask SendAsync(ArtNetPacket packet, ArtNetRemoteNode node, CancellationToken cancellationToken = default) =>
        SendAsync(packet, node.Address, cancellationToken);

    /// <summary>Sends a packet to the directed broadcast address.</summary>
    public ValueTask BroadcastAsync(ArtNetPacket packet, CancellationToken cancellationToken = default) =>
        SendAsync(packet, new IPEndPoint(BroadcastAddress, Settings.Port), cancellationToken);

    /// <summary>Sends raw bytes (e.g. a hand-crafted or replayed datagram).</summary>
    public async ValueTask SendRawAsync(ReadOnlyMemory<byte> datagram, IPEndPoint target, CancellationToken cancellationToken = default)
    {
        var socket = _socket ?? throw new InvalidOperationException("The node is not running. Call StartAsync first.");
        await socket.SendToAsync(datagram, SocketFlags.None, target, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- discovery

    /// <summary>The ArtPoll this node broadcasts.</summary>
    public ArtPollPacket CreatePoll() => new()
    {
        Flags = Settings.PollFlags,
        DiagPriority = Settings.PollDiagnosticPriority,
        EstaManufacturer = Settings.EstaManufacturer,
        Oem = Settings.Oem,
    };

    /// <summary>Broadcasts an ArtPoll now (the poll loop does this every <see cref="ArtNetNodeSettings.PollInterval"/>).</summary>
    public ValueTask PollAsync(ArtPollPacket? poll = null, CancellationToken cancellationToken = default) =>
        BroadcastAsync(poll ?? CreatePoll(), cancellationToken);

    /// <summary>Polls and returns the nodes that replied within <paramref name="wait"/> (default 3 s).</summary>
    public async Task<IReadOnlyList<ArtNetRemoteNode>> DiscoverAsync(TimeSpan? wait = null, ArtPollPacket? poll = null, CancellationToken cancellationToken = default)
    {
        var start = DateTime.UtcNow;
        await PollAsync(poll, cancellationToken).ConfigureAwait(false);
        await Task.Delay(wait ?? ArtNetConstants.PollReplyTimeout, cancellationToken).ConfigureAwait(false);
        return Nodes.Where(n => n.LastSeen >= start).ToArray();
    }

    /// <summary>Nodes subscribed to a universe (they receive unicast ArtDmx for it).</summary>
    public IReadOnlyList<ArtNetRemoteNode> SubscribersOf(PortAddress address) =>
        _nodes.Values.Where(n => n.IsSubscribedTo(address)).ToArray();

    /// <summary>This node's ArtPollReply packets (one per configured port, or one without ports).</summary>
    public IReadOnlyList<ArtPollReplyPacket> CreatePollReplies(IPAddress? requester = null)
    {
        var local = ReplyAddressFor(requester);
        var mac = Settings.MacAddress ?? Interface?.Mac ?? new byte[6];
        var ports = PortsSnapshot();
        var list = new List<ArtPollReplyPacket>();
        int count = Math.Max(1, ports.Length);
        for (int i = 0; i < count; i++)
        {
            var r = new ArtPollReplyPacket
            {
                IpAddress = local,
                BindIp = local,
                BindIndex = (byte)(i + 1),
                FirmwareVersion = Settings.FirmwareVersion,
                Oem = Settings.Oem,
                EstaManufacturer = Settings.EstaManufacturer,
                ShortName = Settings.ShortName,
                LongName = Settings.LongName,
                NodeReport = ArtPollReplyPacket.FormatNodeReport(_reportCode, Interlocked.Increment(ref _reportCounter) % 10000, _reportText),
                Style = Settings.Style,
                Status2 = Settings.Status2 | ArtNetStatus2.PortAddress15Bit,
                User = Settings.UserData,
                RefreshRate = Settings.RefreshRate,
                AcnPriority = _acnPriority,
                IndicatorState = _indicator,
                PortAddressAuthority = _authority,
                FailsafeState = _failsafe,
            };
            if (mac.Length >= 6) mac.AsSpan(0, 6).CopyTo(r.Mac);
            if (i < ports.Length)
            {
                var port = ports[i];
                if (!string.IsNullOrEmpty(port.Name)) r.ShortName = port.Name;
                r.NetSwitch = port.Address.Net;
                r.SubSwitch = port.Address.SubNet;
                bool output = port.Kind == ArtNetPortKind.Output;
                r.SetPort(0, output ? ArtNetPortDirection.Output : ArtNetPortDirection.Input, port.Protocol,
                    output ? (byte)0 : port.Address.Universe, output ? port.Address.Universe : (byte)0);
                if (output)
                {
                    var goodA = ArtNetGoodOutputA.None;
                    if (_universes.TryGetValue(port.Address, out var u))
                    {
                        if (u.IsActive) goodA |= ArtNetGoodOutputA.DataTransmitted;
                        if (u.IsMerging) goodA |= ArtNetGoodOutputA.Merging;
                    }
                    if (port.MergeMode == ArtNetMergeMode.Ltp) goodA |= ArtNetGoodOutputA.MergeLtp;
                    if (port.Sacn) goodA |= ArtNetGoodOutputA.ConvertFromSacn;
                    r.GoodOutputA[0] = (byte)goodA;
                    var goodB = ArtNetGoodOutputB.DiscoveryNotRunning;
                    if (port.RdmDisabled) goodB |= ArtNetGoodOutputB.RdmDisabled;
                    if (port.ContinuousOutput) goodB |= ArtNetGoodOutputB.ContinuousOutput;
                    r.GoodOutputB[0] = (byte)goodB;
                }
                else
                {
                    var good = ArtNetGoodInput.None;
                    if (_outputs.TryGetValue(port.Address, out var o) && DateTime.UtcNow - o.LastSent < ArtNetConstants.StatusTimeout)
                        good |= ArtNetGoodInput.DataReceived;
                    if (port.InputDisabled) good |= ArtNetGoodInput.Disabled;
                    if (port.Sacn) good |= ArtNetGoodInput.ConvertToSacn;
                    r.GoodInput[0] = (byte)good;
                }
            }
            list.Add(r);
        }
        return list;
    }

    /// <summary>Sends this node's ArtPollReply packets to a controller.</summary>
    public async Task SendPollRepliesAsync(IPAddress target, ArtPollPacket? poll = null, CancellationToken cancellationToken = default)
    {
        foreach (var reply in CreatePollReplies(target))
        {
            if (poll is { TargetedMode: true } && !reply.SubscribedAddresses.Any(poll.Targets)) continue;
            await SendAsync(reply, new IPEndPoint(target, Settings.Port), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends ArtPollReply to every controller that set "reply on change" in its ArtPoll. Call after changing
    /// <see cref="Settings"/> names or ports.
    /// </summary>
    public async Task NotifyChangedAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning) return;
        foreach (var ip in _changeSubscribers.Keys)
        {
            try { await SendPollRepliesAsync(ip, null, cancellationToken).ConfigureAwait(false); }
            catch (SocketException ex) { RaiseError(ex); }
        }
    }

    /// <summary>Fire-and-forget variant for network threads: never faults (the node may be stopping).</summary>
    private async Task NotifyChangedSafeAsync()
    {
        try { await NotifyChangedAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
        catch (Exception ex) { RaiseError(ex); }
    }

    /// <summary>
    /// Replaces <see cref="ArtNetNodeSettings.Ports"/> with the result of <paramref name="update"/>, atomically with
    /// respect to remote programming (ArtAddress / ArtInput). <paramref name="update"/> receives the current ports and
    /// must not call back into the node. Call <see cref="NotifyChangedAsync"/> afterwards to tell controllers.
    /// </summary>
    public void UpdatePorts(Func<IReadOnlyList<ArtNetPortConfig>, IEnumerable<ArtNetPortConfig>> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_stateLock) Settings.Ports = [.. update([.. Settings.Ports])];
    }

    /// <summary>Copy of <see cref="ArtNetNodeSettings.Ports"/> for reading on network threads.</summary>
    private ArtNetPortConfig[] PortsSnapshot()
    {
        lock (_stateLock) return [.. Settings.Ports];
    }

    /// <summary>Sets the NodeReport code and text reported in ArtPollReply.</summary>
    public void SetNodeReport(ArtNetNodeReportCode code, string? text = null)
    {
        _reportCode = code;
        _reportText = text ?? code.ToDescription();
    }

    // ---------------------------------------------------------------- DMX output

    /// <summary>
    /// Sends ArtDmx for a universe to every subscribed node (unicast) plus <see cref="ArtNetNodeSettings.StaticDmxTargets"/>.
    /// The frame is remembered and re-sent by the keep-alive loop. Returns the number of destinations.
    /// </summary>
    public async Task<int> SendDmxAsync(PortAddress address, ReadOnlyMemory<byte> data, byte physical = 0, CancellationToken cancellationToken = default)
    {
        var o = _outputs.GetOrAdd(address, _ => new OutputUniverse());
        await o.SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArtDmxPacket packet;
            lock (o)
            {
                int n = Math.Min(data.Length, ArtNetConstants.DmxChannels);
                data.Span[..n].CopyTo(o.Data);
                o.Data.AsSpan(n).Clear();
                o.Physical = physical;
                packet = NextPacket(address, o, n);
            }
            return await SendDmxPacketAsync(packet, o, cancellationToken).ConfigureAwait(false);
        }
        finally { o.SendGate.Release(); }
    }

    /// <summary>Changes channels of the remembered output frame and sends it. <paramref name="startChannel"/> is 1-based.</summary>
    public async Task<int> SetChannelsAsync(PortAddress address, int startChannel, ReadOnlyMemory<byte> levels, CancellationToken cancellationToken = default)
    {
        if (startChannel < 1 || startChannel > ArtNetConstants.DmxChannels) throw new ArgumentOutOfRangeException(nameof(startChannel));
        var o = _outputs.GetOrAdd(address, _ => new OutputUniverse());
        await o.SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArtDmxPacket packet;
            lock (o)
            {
                int n = Math.Min(levels.Length, ArtNetConstants.DmxChannels - startChannel + 1);
                levels.Span[..n].CopyTo(o.Data.AsSpan(startChannel - 1));
                packet = NextPacket(address, o, ArtNetConstants.DmxChannels);
            }
            return await SendDmxPacketAsync(packet, o, cancellationToken).ConfigureAwait(false);
        }
        finally { o.SendGate.Release(); }
    }

    /// <summary>Copy of the frame this node transmits for a universe (512 zeros if none).</summary>
    public byte[] GetOutput(PortAddress address) =>
        _outputs.TryGetValue(address, out var o) ? (byte[])o.Data.Clone() : new byte[ArtNetConstants.DmxChannels];

    /// <summary>Stops transmitting (and keeping alive) a universe.</summary>
    public bool StopOutput(PortAddress address) => _outputs.TryRemove(address, out _);

    /// <summary>Destinations of the last ArtDmx sent for a universe.</summary>
    public int LastTargetCount(PortAddress address) => _outputs.TryGetValue(address, out var o) ? o.LastTargets : 0;

    private static ArtDmxPacket NextPacket(PortAddress address, OutputUniverse o, int length)
    {
        o.Sequence = (byte)(o.Sequence == 255 ? 1 : o.Sequence + 1);
        return new ArtDmxPacket
        {
            PortAddress = address,
            Sequence = o.Sequence,
            Physical = o.Physical,
            Data = o.Data.AsSpan(0, Math.Max(2, length)).ToArray(),
        };
    }

    /// <summary>
    /// Sends to every target even when one fails (e.g. an unreachable static target); the first failure is rethrown
    /// afterwards. Caller holds <see cref="OutputUniverse.SendGate"/>.
    /// </summary>
    private async Task<int> SendDmxPacketAsync(ArtDmxPacket packet, OutputUniverse o, CancellationToken ct)
    {
        var targets = DmxTargets(packet.PortAddress);
        Exception? first = null;
        foreach (var t in targets)
        {
            ct.ThrowIfCancellationRequested(); // a stopped loop must not use the socket of a restarted node
            try { await SendAsync(packet, t, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { first ??= ex; }
        }
        o.LastSent = DateTime.UtcNow;
        o.LastTargets = targets.Count;
        if (first is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
        return targets.Count;
    }

    private List<IPEndPoint> DmxTargets(PortAddress address)
    {
        var set = new HashSet<IPEndPoint>();
        foreach (var n in _nodes.Values)
            if (n.IsSubscribedTo(address)) set.Add(new IPEndPoint(n.Address, Settings.Port));
        foreach (var t in Settings.StaticDmxTargets) set.Add(t);
        if (set.Count == 0 && Settings.BroadcastDmxWithoutSubscribers) set.Add(new IPEndPoint(BroadcastAddress, Settings.Port));
        return set.ToList();
    }

    /// <summary>Broadcasts ArtSync so nodes output the ArtDmx sent since the last sync simultaneously.</summary>
    public ValueTask SendSyncAsync(CancellationToken cancellationToken = default) => BroadcastAsync(new ArtSyncPacket(), cancellationToken);

    /// <summary>Sends ArtNzs (non-zero start code) to the subscribers of its universe.</summary>
    public async Task<int> SendNzsAsync(ArtNzsPacket packet, CancellationToken cancellationToken = default)
    {
        var targets = DmxTargets(packet.PortAddress);
        foreach (var t in targets) await SendAsync(packet, t, cancellationToken).ConfigureAwait(false);
        return targets.Count;
    }

    // ---------------------------------------------------------------- configuration of remote nodes

    /// <summary>Sends ArtAddress and waits for the node's ArtPollReply (null on timeout).</summary>
    public async Task<ArtPollReplyPacket?> SendAddressAsync(IPAddress node, ArtAddressPacket packet, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        byte bind = Math.Max((byte)1, packet.BindIndex);
        var reply = await RequestAsync(packet, new IPEndPoint(node, Settings.Port),
            (p, from) => p is ArtPollReplyPacket r && from.Address.Equals(node) && Math.Max((byte)1, r.BindIndex) == bind,
            timeout, cancellationToken).ConfigureAwait(false);
        return reply as ArtPollReplyPacket;
    }

    /// <summary>Sends an ArtAddress command (e.g. LED locate) to a node.</summary>
    public Task<ArtPollReplyPacket?> SendAddressCommandAsync(ArtNetRemoteNode node, ArtNetAddressCommand command, CancellationToken cancellationToken = default) =>
        SendAddressAsync(node.Address, ArtAddressPacket.ForCommand(command, node.BindIndex), null, cancellationToken);

    /// <summary>Sends ArtInput and waits for the node's ArtPollReply.</summary>
    public async Task<ArtPollReplyPacket?> SendInputAsync(IPAddress node, ArtInputPacket packet, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        byte bind = Math.Max((byte)1, packet.BindIndex);
        return await RequestAsync(packet, new IPEndPoint(node, Settings.Port),
            (p, from) => p is ArtPollReplyPacket r && from.Address.Equals(node) && Math.Max((byte)1, r.BindIndex) == bind,
            timeout, cancellationToken).ConfigureAwait(false) as ArtPollReplyPacket;
    }

    /// <summary>
    /// Sends ArtIpProg and waits for ArtIpProgReply (null when the node does not support it). The reply is accepted
    /// from the old address or, when an IP is being programmed, from the new one.
    /// </summary>
    public async Task<ArtIpProgReplyPacket?> SendIpProgAsync(IPAddress node, ArtIpProgPacket packet, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var newIp = packet.Command.HasFlag(ArtIpProgCommand.EnableProgramming) && packet.Command.HasFlag(ArtIpProgCommand.ProgramIp)
            ? packet.ProgIp : null;
        return await RequestAsync(packet, new IPEndPoint(node, Settings.Port),
            (p, from) => p is ArtIpProgReplyPacket && (from.Address.Equals(node) || (newIp is not null && from.Address.Equals(newIp))),
            timeout, cancellationToken).ConfigureAwait(false) as ArtIpProgReplyPacket;
    }

    /// <summary>Sends ArtDataRequest and waits for ArtDataReply.</summary>
    public async Task<ArtDataReplyPacket?> RequestDataAsync(IPAddress node, ushort request, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var packet = new ArtDataRequestPacket { EstaManufacturer = Settings.EstaManufacturer, Oem = Settings.Oem, Request = request };
        return await RequestAsync(packet, new IPEndPoint(node, Settings.Port),
            (p, from) => p is ArtDataReplyPacket r && from.Address.Equals(node) && (r.Request == request || request == 0),
            timeout, cancellationToken).ConfigureAwait(false) as ArtDataReplyPacket;
    }

    public Task<ArtDataReplyPacket?> RequestDataAsync(IPAddress node, ArtNetDataRequestCode request, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        RequestDataAsync(node, (ushort)request, timeout, cancellationToken);

    // ---------------------------------------------------------------- show control

    /// <summary>Sends ArtTimeCode (broadcast when <paramref name="target"/> is null).</summary>
    public ValueTask SendTimeCodeAsync(ArtTimeCodePacket packet, IPAddress? target = null, CancellationToken cancellationToken = default) =>
        target is null ? BroadcastAsync(packet, cancellationToken) : SendAsync(packet, target, cancellationToken);

    /// <summary>Sends ArtTrigger (broadcast when <paramref name="target"/> is null).</summary>
    public ValueTask SendTriggerAsync(ArtTriggerPacket packet, IPAddress? target = null, CancellationToken cancellationToken = default) =>
        target is null ? BroadcastAsync(packet, cancellationToken) : SendAsync(packet, target, cancellationToken);

    /// <summary>Sends ArtCommand (broadcast when <paramref name="target"/> is null).</summary>
    public ValueTask SendCommandAsync(ArtCommandPacket packet, IPAddress? target = null, CancellationToken cancellationToken = default) =>
        target is null ? BroadcastAsync(packet, cancellationToken) : SendAsync(packet, target, cancellationToken);

    /// <summary>
    /// Sends ArtDiagData to the controllers that requested diagnostics: unicast when a single controller asked for
    /// unicast, otherwise broadcast. Messages below the lowest requested priority are not sent. Returns false when
    /// nobody wants it.
    /// </summary>
    public async Task<bool> SendDiagnosticAsync(string text, ArtNetDiagnosticPriority priority = ArtNetDiagnosticPriority.Low, byte logicalPort = 0, CancellationToken cancellationToken = default)
    {
        var subs = _diagSubscribers.ToArray();
        if (subs.Length == 0) return false;
        var min = subs.Min(s => (byte)s.Value.Priority);
        if ((byte)priority < min) return false;
        var packet = new ArtDiagDataPacket { Text = text, Priority = priority, LogicalPort = logicalPort };
        if (subs.Length == 1 && subs[0].Value.Unicast)
            await SendAsync(packet, subs[0].Key, cancellationToken).ConfigureAwait(false);
        else
            await BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ---------------------------------------------------------------- RDM

    /// <summary>
    /// Directed-broadcasts ArtTodRequest for the given universes and collects ArtTodData for <paramref name="wait"/>.
    /// </summary>
    public async Task<IReadOnlyList<ArtTodDataPacket>> RequestTodAsync(IEnumerable<PortAddress> addresses, TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        var wanted = addresses.Distinct().ToArray();
        var results = new ConcurrentQueue<ArtTodDataPacket>();
        void Handler(object? s, ArtNetPacketEventArgs<ArtTodDataPacket> e)
        {
            if (wanted.Contains(e.Packet.PortAddress)) results.Enqueue(e.Packet);
        }
        TodDataReceivedDirect += Handler;
        try
        {
            foreach (var group in wanted.GroupBy(a => a.Net))
                foreach (var chunk in group.Chunk(ArtNetConstants.MaxTodRequestAddresses))
                    await BroadcastAsync(ArtTodRequestPacket.For(chunk), cancellationToken).ConfigureAwait(false);
            await Task.Delay(wait ?? Settings.RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally { TodDataReceivedDirect -= Handler; }
        return results.ToArray();
    }

    /// <summary>Merges ArtTodData blocks into complete tables per (source, Port-Address).</summary>
    public static IReadOnlyDictionary<PortAddress, IReadOnlyList<RdmUid>> MergeTod(IEnumerable<ArtTodDataPacket> packets) =>
        packets.Where(p => p.CommandResponse == ArtNetTodDataCommand.TodFull)
               .GroupBy(p => p.PortAddress)
               .ToDictionary(g => g.Key, g => (IReadOnlyList<RdmUid>)g.OrderBy(p => p.BlockCount).SelectMany(p => p.Uids).Distinct().ToArray());

    /// <summary>Sends ArtTodControl and waits for the resulting ArtTodData.</summary>
    public async Task<ArtTodDataPacket?> SendTodControlAsync(IPAddress node, PortAddress address, ArtNetTodControlCommand command, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        await RequestAsync(new ArtTodControlPacket { PortAddress = address, Command = command }, new IPEndPoint(node, Settings.Port),
            (p, from) => p is ArtTodDataPacket t && from.Address.Equals(node) && t.PortAddress == address, timeout, cancellationToken).ConfigureAwait(false) as ArtTodDataPacket;

    /// <summary>Unicasts an RDM message (without start code) in ArtRdm and waits for the response ArtRdm (null on timeout).</summary>
    public async Task<ArtRdmPacket?> SendRdmAsync(IPAddress node, PortAddress address, byte[] rdmMessage, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var packet = new ArtRdmPacket { PortAddress = address, RdmPacket = rdmMessage };
        RdmMessage.TryParse(rdmMessage, out var request);
        return await RequestAsync(packet, new IPEndPoint(node, Settings.Port),
            (p, from) => p is ArtRdmPacket r && from.Address.Equals(node) && r.PortAddress == address &&
                         (request is null || (r.Message is { } m && m.IsResponse && m.TransactionNumber == request.TransactionNumber)),
            timeout, cancellationToken).ConfigureAwait(false) as ArtRdmPacket;
    }

    /// <summary>Unicasts ArtRdmSub.</summary>
    public ValueTask SendRdmSubAsync(IPAddress node, ArtRdmSubPacket packet, CancellationToken cancellationToken = default) =>
        SendAsync(packet, node, cancellationToken);

    // ---------------------------------------------------------------- firmware

    /// <summary>
    /// Uploads a firmware (.alf) or UBEA (.alu) file block by block, waiting up to 30 s for each ArtFirmwareReply.
    /// Checks the file's OEM list against the node first when <paramref name="node"/> is known.
    /// </summary>
    public async Task<ArtNetFirmwareReplyType> UploadFirmwareAsync(IPAddress target, ArtNetFirmwareFile file, bool ubea = false,
        IProgress<ArtNetFirmwareProgress>? progress = null, ArtNetRemoteNode? node = null, CancellationToken cancellationToken = default)
    {
        if (node is not null && file.OemCodes.Count > 0 && !file.SupportsOem(node.Oem))
            throw new InvalidOperationException($"The firmware file is not valid for OEM 0x{node.Oem:X4}.");
        var packets = file.ToPackets(ubea);
        var last = ArtNetFirmwareReplyType.Fail;
        for (int i = 0; i < packets.Count; i++)
        {
            var reply = await RequestAsync(packets[i], new IPEndPoint(target, Settings.Port),
                (p, from) => p is ArtFirmwareReplyPacket && from.Address.Equals(target),
                ArtNetConstants.FirmwareReplyTimeout, cancellationToken).ConfigureAwait(false) as ArtFirmwareReplyPacket;
            if (reply is null) throw new TimeoutException($"No ArtFirmwareReply for block {i} within 30 s.");
            last = reply.Type;
            progress?.Report(new ArtNetFirmwareProgress(i + 1, packets.Count, last));
            if (last == ArtNetFirmwareReplyType.Fail) return last;
        }
        return last;
    }

    // ---------------------------------------------------------------- request / response

    /// <summary>Sends <paramref name="packet"/> and waits for the first received packet matching <paramref name="match"/>.</summary>
    public async Task<ArtNetPacket?> RequestAsync(ArtNetPacket packet, IPEndPoint target, Func<ArtNetPacket, IPEndPoint, bool> match,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var waiter = new Waiter(match);
        lock (_waiters) _waiters.Add(waiter);
        try
        {
            await SendAsync(packet, target, cancellationToken).ConfigureAwait(false);
            return await waiter.Tcs.Task.WaitAsync(timeout ?? Settings.RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            lock (_waiters) _waiters.Remove(waiter);
        }
    }

    private void CompleteWaiters(ArtNetPacket packet, IPEndPoint from)
    {
        List<Waiter> matched;
        lock (_waiters)
        {
            if (_waiters.Count == 0) return;
            matched = _waiters.Where(w => SafeMatch(w, packet, from)).ToList();
        }
        foreach (var w in matched) w.Tcs.TrySetResult(packet);
    }

    private static bool SafeMatch(Waiter w, ArtNetPacket p, IPEndPoint from)
    {
        try { return w.Match(p, from); } catch { return false; }
    }

    // ---------------------------------------------------------------- receiving

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[65536];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize) { continue; }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                RaiseError(ex);
                try { await Task.Delay(100, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }

            try { HandleDatagram(buffer.AsSpan(0, r.ReceivedBytes), (IPEndPoint)r.RemoteEndPoint); }
            catch (Exception ex) { RaiseError(ex); }
        }
    }

    /// <summary>Feeds a datagram into the node as if it had been received (tests, replay, other transports).</summary>
    public void InjectDatagram(ReadOnlySpan<byte> data, IPEndPoint from) => HandleDatagram(data, from);

    private void HandleDatagram(ReadOnlySpan<byte> data, IPEndPoint from)
    {
        if (!ArtNetPacketParser.TryParse(data, out var packet)) return;
        if (from.Address.IsIPv4MappedToIPv6) from = new IPEndPoint(from.Address.MapToIPv4(), from.Port);
        bool local = IsLocalAddress(from.Address);
        if (local && Settings.IgnoreOwnDmx && packet is ArtDmxPacket or ArtSyncPacket or ArtNzsPacket) return;

        _nodes.TryGetValue((from.Address, 1), out var node);

        if (packet is ArtPollReplyPacket pr)
            node = HandlePollReply(pr, from, local);

        CompleteWaiters(packet, from);
        Raise(PacketReceived, Args(packet, from, node));

        switch (packet)
        {
            case ArtPollPacket p: HandlePoll(p, from); Raise(PollReceived, Args(p, from, node)); break;
            case ArtPollReplyPacket p: Raise(PollReplyReceived, Args(p, from, node)); break;
            case ArtDmxPacket p: HandleDmx(p, from, node); break;
            case ArtSyncPacket p: HandleSync(from); Raise(SyncReceived, Args(p, from, node)); break;
            case ArtVlcPacket p: Raise(VlcReceived, Args(p, from, node)); break;
            case ArtNzsPacket p: Raise(NzsReceived, Args(p, from, node)); break;
            case ArtAddressPacket p: HandleAddress(p, from); Raise(AddressReceived, Args(p, from, node)); break;
            case ArtInputPacket p: HandleInput(p, from); Raise(InputReceived, Args(p, from, node)); break;
            case ArtIpProgPacket p: Raise(IpProgReceived, Args(p, from, node)); break;
            case ArtIpProgReplyPacket p: Raise(IpProgReplyReceived, Args(p, from, node)); break;
            case ArtDataRequestPacket p: HandleDataRequest(p, from); Raise(DataRequestReceived, Args(p, from, node)); break;
            case ArtDataReplyPacket p: Raise(DataReplyReceived, Args(p, from, node)); break;
            case ArtDiagDataPacket p: Raise(DiagDataReceived, Args(p, from, node)); break;
            case ArtTimeCodePacket p: Raise(TimeCodeReceived, Args(p, from, node)); break;
            case ArtCommandPacket p: Raise(CommandReceived, Args(p, from, node)); break;
            case ArtTriggerPacket p: Raise(TriggerReceived, Args(p, from, node)); break;
            case ArtTodRequestPacket p: Raise(TodRequestReceived, Args(p, from, node)); break;
            case ArtTodDataPacket p:
                TodDataReceivedDirect?.Invoke(this, Args(p, from, node));
                Raise(TodDataReceived, Args(p, from, node));
                break;
            case ArtTodControlPacket p: Raise(TodControlReceived, Args(p, from, node)); break;
            case ArtRdmPacket p: Raise(RdmReceived, Args(p, from, node)); break;
            case ArtRdmSubPacket p: Raise(RdmSubReceived, Args(p, from, node)); break;
            case ArtFirmwareMasterPacket p: Raise(FirmwareMasterReceived, Args(p, from, node)); break;
            case ArtFirmwareReplyPacket p: Raise(FirmwareReplyReceived, Args(p, from, node)); break;
            case ArtUnknownPacket p: Raise(UnknownReceived, Args(p, from, node)); break;
        }
    }

    /// <summary>Raised synchronously on the receive thread (used by <see cref="RequestTodAsync"/>).</summary>
    private event EventHandler<ArtNetPacketEventArgs<ArtTodDataPacket>>? TodDataReceivedDirect;

    private ArtNetRemoteNode HandlePollReply(ArtPollReplyPacket reply, IPEndPoint from, bool local)
    {
        byte bind = Math.Max((byte)1, reply.BindIndex);
        var key = (from.Address, bind);
        bool isNew = false;
        ArtNetRemoteNode node;
        bool changed;
        lock (_pollReplyLock) // InjectDatagram may run beside the receive loop
        {
            node = _nodes.GetOrAdd(key, k =>
            {
                isNew = true;
                return new ArtNetRemoteNode(k.Address, k.Bind) { IsLocal = local, EventContext = Settings.EventContext };
            });
            bool c = false;
            RunAsHandler(() => // PropertyChanged handlers run here when there is no EventContext
            {
                c = node.Update(reply);
                node.LastSeen = DateTime.UtcNow;
            });
            changed = c;
        }
        if (isNew) Raise(NodeDiscovered, new ArtNetNodeEventArgs(node));
        else if (changed) Raise(NodeUpdated, new ArtNetNodeEventArgs(node));
        return node;
    }

    private void HandlePoll(ArtPollPacket poll, IPEndPoint from)
    {
        var ip = from.Address;
        if (poll.Flags.HasFlag(ArtPollFlags.Diagnostics))
            _diagSubscribers[ip] = new DiagSubscriber(poll.Flags.HasFlag(ArtPollFlags.DiagnosticsUnicast), poll.DiagPriority, DateTime.UtcNow);
        else
            _diagSubscribers.TryRemove(ip, out _);
        if (poll.Flags.HasFlag(ArtPollFlags.ReplyOnChange)) _changeSubscribers[ip] = DateTime.UtcNow;
        else _changeSubscribers.TryRemove(ip, out _);

        if (!Settings.ReplyToPolls || _socket is null) return;
        _ = ReplyToPollAsync(poll, ip);
    }

    private async Task ReplyToPollAsync(ArtPollPacket poll, IPAddress to)
    {
        try
        {
            var max = Settings.MaxReplyDelay;
            if (max > TimeSpan.Zero)
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * max.TotalMilliseconds)).ConfigureAwait(false);
            if (!IsRunning) return;
            await SendPollRepliesAsync(to, poll).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { RaiseError(ex); }
    }

    private void HandleDmx(ArtDmxPacket packet, IPEndPoint from, ArtNetRemoteNode? node)
    {
        Raise(DmxReceived, Args(packet, from, node));
        var address = packet.PortAddress;
        var universe = _universes.GetOrAdd(address, a => new ArtNetUniverse(a, MergeModeFor(a))
        {
            MergeTimeout = Settings.MergeTimeout,
            UseSequenceNumbers = Settings.UseSequenceNumbers,
        });
        bool wasMerging = universe.IsMerging;
        var output = universe.Apply(from.Address, packet, out bool merging);
        if (output is null) return;
        _lastDmxSource = from.Address;

        var args = new ArtNetUniverseEventArgs(address, output, universe.Sources, merging, false);
        lock (_syncLock)
        {
            // Leave synchronous mode here too (not only in the keep-alive loop) so a buffered frame is never
            // released after a newer one.
            ExpireSync(DateTime.UtcNow);
            if (_syncActive && !merging)
                _pendingSync[address] = args;
            else
            {
                _pendingSync.TryRemove(address, out _); // superseded
                Raise(UniverseChanged, args);
            }
        }

        if (merging != wasMerging && PortsSnapshot().Any(p => p.Kind == ArtNetPortKind.Output && p.Address == address))
            _ = NotifyChangedSafeAsync();
    }

    /// <summary>Returns to non-synchronous mode when no ArtSync arrived for 4 s. Caller holds <see cref="_syncLock"/>.</summary>
    private void ExpireSync(DateTime now)
    {
        if (!_syncActive || now - _lastSync < ArtNetConstants.SyncTimeout) return;
        _syncActive = false;
        FlushPendingSync(false);
    }

    private void HandleSync(IPEndPoint from)
    {
        if (!Settings.EnableSync) return;
        // Ignore ArtSync from a controller other than the one that sent the most recent ArtDmx, and while merging.
        if (_lastDmxSource is not null && !_lastDmxSource.Equals(from.Address)) return;
        if (_universes.Values.Any(u => u.IsMerging)) return;
        lock (_syncLock)
        {
            _syncActive = true;
            _lastSync = DateTime.UtcNow;
            FlushPendingSync(true);
        }
    }

    private void FlushPendingSync(bool synchronous)
    {
        foreach (var key in _pendingSync.Keys.ToArray())
        {
            if (!_pendingSync.TryRemove(key, out var a)) continue;
            Raise(UniverseChanged, new ArtNetUniverseEventArgs(a.Address, a.Data, a.Sources, a.Merging, synchronous));
        }
    }

    private ArtNetMergeMode MergeModeFor(PortAddress address) =>
        PortsSnapshot().FirstOrDefault(p => p.Kind == ArtNetPortKind.Output && p.Address == address)?.MergeMode ?? Settings.DefaultMergeMode;

    /// <summary>Received state of a universe, or null if nothing arrived for it.</summary>
    public ArtNetUniverse? GetUniverse(PortAddress address) => _universes.TryGetValue(address, out var u) ? u : null;

    // ---------------------------------------------------------------- acting as a node

    private int PortIndexForBind(byte bindIndex) => Math.Max(1, (int)bindIndex) - 1;

    private void HandleAddress(ArtAddressPacket p, IPEndPoint from)
    {
        if (!Settings.AcceptRemoteProgramming) return;
        int portIndex = PortIndexForBind(p.BindIndex);
        bool changed = false;
        ArtNetUniverse? cleared = null;
        lock (_stateLock)
        {
            bool hasPort = portIndex < Settings.Ports.Count;
            if (!hasPort && portIndex > 0) return; // addressed to a bind this node does not have
            if (p.ShortName.Length > 0)
            {
                // Bind 1 is the root device: its name is Settings.ShortName, but a port name overrides it in ArtPollReply.
                if (portIndex == 0) Settings.ShortName = p.ShortName;
                if (hasPort && (portIndex > 0 || !string.IsNullOrEmpty(Settings.Ports[0].Name)))
                    Settings.Ports[portIndex] = Settings.Ports[portIndex] with { Name = p.ShortName };
                SetNodeReport(ArtNetNodeReportCode.ShNameOk, "Short name programmed");
                changed = true;
            }
            if (p.LongName.Length > 0)
            {
                Settings.LongName = p.LongName;
                SetNodeReport(ArtNetNodeReportCode.LoNameOk, "Long name programmed");
                changed = true;
            }
            if (hasPort)
            {
                var port = Settings.Ports[portIndex];
                var a = port.Address;
                int net = ArtAddressPacket.IsProgrammed(p.NetSwitch) ? p.NetSwitch & 0x7F : a.Net;
                int sub = ArtAddressPacket.IsProgrammed(p.SubSwitch) ? p.SubSwitch & 0x0F : a.SubNet;
                byte sw = port.Kind == ArtNetPortKind.Output ? p.SwOut[0] : p.SwIn[0];
                int uni = ArtAddressPacket.IsProgrammed(sw) ? sw & 0x0F : a.Universe;
                var newAddress = new PortAddress(net, sub, uni);
                if (newAddress != a)
                {
                    Settings.Ports[portIndex] = port with { Address = newAddress };
                    _authority = ArtNetPortAddressAuthority.Network;
                    changed = true;
                }
            }
            if (p.AcnPriority <= 200 && p.AcnPriority != _acnPriority) { _acnPriority = p.AcnPriority; changed = true; }
            changed |= ApplyCommand(p.Command, portIndex, hasPort, out cleared);
        }
        if (cleared is not null)
        {
            lock (_syncLock)
            {
                _pendingSync.TryRemove(cleared.Address, out _);
                Raise(UniverseChanged, new ArtNetUniverseEventArgs(cleared.Address, cleared.GetData(), cleared.Sources, cleared.IsMerging, false));
            }
        }
        if (changed) Raise(ConfigurationChanged, EventArgs.Empty);
        _ = SendRepliesSafeAsync(from.Address);
    }

    private bool ApplyCommand(ArtNetAddressCommand command, int portIndex, bool hasPort, out ArtNetUniverse? cleared)
    {
        cleared = null;
        byte c = (byte)command;
        ArtNetPortConfig? port = hasPort ? Settings.Ports[portIndex] : null;
        ArtNetUniverse? universe = port is not null && _universes.TryGetValue(port.Address, out var u) ? u : null;
        switch (command)
        {
            case ArtNetAddressCommand.None: return false;
            case ArtNetAddressCommand.CancelMerge:
                if (universe is not null) universe.CancelMerge();
                else if (port is null) foreach (var x in _universes.Values) x.CancelMerge(); // node without ports
                return true;
            case ArtNetAddressCommand.LedNormal: _indicator = ArtNetIndicatorState.Normal; return true;
            case ArtNetAddressCommand.LedMute: _indicator = ArtNetIndicatorState.Mute; return true;
            case ArtNetAddressCommand.LedLocate: _indicator = ArtNetIndicatorState.Locate; return true;
            case ArtNetAddressCommand.ResetRxFlags: SetNodeReport(ArtNetNodeReportCode.PowerOk, "Receive flags reset"); return true;
            case ArtNetAddressCommand.FailHold: _failsafe = ArtNetFailsafeState.Hold; return true;
            case ArtNetAddressCommand.FailZero: _failsafe = ArtNetFailsafeState.Zero; return true;
            case ArtNetAddressCommand.FailFull: _failsafe = ArtNetFailsafeState.Full; return true;
            case ArtNetAddressCommand.FailScene: _failsafe = ArtNetFailsafeState.Scene; return true;
        }
        if (port is null || (c & 0x0F) != 0) return false; // per-port commands: only port 0 of each bind in Art-Net 4
        switch (c & 0xF0)
        {
            case 0x10: port.MergeMode = ArtNetMergeMode.Ltp; if (universe is not null) universe.MergeMode = ArtNetMergeMode.Ltp; return true;
            case 0x50: port.MergeMode = ArtNetMergeMode.Htp; if (universe is not null) universe.MergeMode = ArtNetMergeMode.Htp; return true;
            case 0x20: Settings.Ports[portIndex] = port with { Kind = ArtNetPortKind.Output }; return true;
            case 0x30: Settings.Ports[portIndex] = port with { Kind = ArtNetPortKind.Input }; return true;
            case 0x60: port.Sacn = false; return true;
            case 0x70: port.Sacn = true; return true;
            case 0x90:
                universe?.Clear();
                cleared = universe; // UniverseChanged is raised by the caller outside the state lock
                return true;
            case 0xA0: port.ContinuousOutput = false; return true;
            case 0xB0: port.ContinuousOutput = true; return true;
            case 0xC0: port.RdmDisabled = false; return true;
            case 0xD0: port.RdmDisabled = true; return true;
            default: return false;
        }
    }

    private void HandleInput(ArtInputPacket p, IPEndPoint from)
    {
        if (!Settings.AcceptRemoteProgramming) return;
        int portIndex = PortIndexForBind(p.BindIndex);
        bool changed = false;
        lock (_stateLock)
        {
            if (portIndex < Settings.Ports.Count && Settings.Ports[portIndex].Kind == ArtNetPortKind.Input)
            {
                Settings.Ports[portIndex].InputDisabled = p.IsDisabled(0);
                changed = true;
            }
        }
        if (changed) Raise(ConfigurationChanged, EventArgs.Empty);
        _ = SendRepliesSafeAsync(from.Address);
    }

    private void HandleDataRequest(ArtDataRequestPacket p, IPEndPoint from)
    {
        var replies = Settings.DataReplies;
        if (replies.Count == 0) return;
        string? payload = null;
        if (p.RequestCode == ArtNetDataRequestCode.Poll) payload = string.Empty;
        else if (replies.TryGetValue(p.RequestCode, out var s)) payload = s;
        if (payload is null) return;
        var reply = new ArtDataReplyPacket
        {
            EstaManufacturer = Settings.EstaManufacturer,
            Oem = Settings.Oem,
            Request = p.Request,
            PayloadText = payload,
        };
        _ = SendSafeAsync(reply, new IPEndPoint(from.Address, Settings.Port));
    }

    private async Task SendRepliesSafeAsync(IPAddress to)
    {
        try { await SendPollRepliesAsync(to).ConfigureAwait(false); }
        catch (Exception ex) { RaiseError(ex); }
    }

    private async Task SendSafeAsync(ArtNetPacket packet, IPEndPoint to)
    {
        try { await SendAsync(packet, to).ConfigureAwait(false); }
        catch (Exception ex) { RaiseError(ex); }
    }

    // ---------------------------------------------------------------- loops

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            if (Settings.SendPolls) await SafePollAsync(ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Settings.PollInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (Settings.SendPolls) await SafePollAsync(ct).ConfigureAwait(false);

                var cutoff = DateTime.UtcNow - Settings.NodeTimeout;
                foreach (var (key, node) in _nodes)
                {
                    if (node.LastSeen >= cutoff) continue;
                    bool removed;
                    lock (_pollReplyLock) removed = node.LastSeen < cutoff && _nodes.TryRemove(new KeyValuePair<(IPAddress, byte), ArtNetRemoteNode>(key, node));
                    if (removed) Raise(NodeLost, new ArtNetNodeEventArgs(node, timedOut: true));
                }

                var stale = DateTime.UtcNow - TimeSpan.FromSeconds(30);
                foreach (var (ip, since) in _changeSubscribers) if (since < stale) _changeSubscribers.TryRemove(ip, out _);
                foreach (var (ip, sub) in _diagSubscribers) if (sub.Since < stale) _diagSubscribers.TryRemove(ip, out _);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SafePollAsync(CancellationToken ct)
    {
        try { await PollAsync(null, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { RaiseError(ex); }
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = DateTime.UtcNow;
                // If the receive thread holds the lock it is handling ArtDmx / ArtSync and expires sync itself.
                if (_syncActive && Monitor.TryEnter(_syncLock))
                {
                    try { ExpireSync(now); }
                    finally { Monitor.Exit(_syncLock); }
                }

                var keepAlive = Settings.DmxKeepAlive;
                if (keepAlive <= TimeSpan.Zero) continue;
                foreach (var (address, o) in _outputs)
                {
                    if (now - o.LastSent < keepAlive) continue;
                    // Skip this tick when a user send is in progress; that frame is newer anyway.
                    if (!o.SendGate.Wait(0)) continue;
                    try
                    {
                        if (DateTime.UtcNow - o.LastSent < keepAlive) continue;
                        ArtDmxPacket packet;
                        lock (o) packet = NextPacket(address, o, ArtNetConstants.DmxChannels);
                        await SendDmxPacketAsync(packet, o, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { RaiseError(ex); }
                    finally { o.SendGate.Release(); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---------------------------------------------------------------- helpers

    private static ArtNetPacketEventArgs<T> Args<T>(T packet, IPEndPoint from, ArtNetRemoteNode? node) where T : ArtNetPacket => new(packet, from, node);

    private void Raise<T>(EventHandler<T>? handler, T args) where T : EventArgs
    {
        if (handler is null) return;
        var ctx = Settings.EventContext;
        if (ctx is null) RunAsHandler(() => Invoke(handler, args));
        else ctx.Post(_ => Invoke(handler, args), null);
    }

    private void Raise(EventHandler? handler, EventArgs args)
    {
        if (handler is null) return;
        var ctx = Settings.EventContext;
        void Run() { try { handler(this, args); } catch (Exception ex) { RaiseError(ex); } }
        if (ctx is null) RunAsHandler(Run);
        else ctx.Post(_ => Run(), null);
    }

    /// <summary>Runs user code synchronously on this thread, marked so <see cref="StopAsync"/> does not wait for itself.</summary>
    private void RunAsHandler(Action action)
    {
        var previous = t_handlerNode;
        t_handlerNode = this;
        try { action(); }
        finally { t_handlerNode = previous; }
    }

    private void Invoke<T>(EventHandler<T> handler, T args) where T : EventArgs
    {
        try { handler(this, args); }
        catch (Exception ex) when (handler is not EventHandler<ArtNetErrorEventArgs>) { RaiseError(ex); }
        catch (Exception ex) { Debug.WriteLine($"ArtNet: error handler threw: {ex}"); }
    }

    private void RaiseError(Exception ex)
    {
        if (Error is null) { Debug.WriteLine($"ArtNet: {ex}"); return; }
        Raise(Error, new ArtNetErrorEventArgs(ex));
    }

    private Socket CreateSocket()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Share 6454 with other Art-Net software on this machine where the OS allows it.
            try { s.ExclusiveAddressUse = false; } catch (SocketException) { } catch (PlatformNotSupportedException) { }
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.EnableBroadcast = true;
            try { s.ReceiveBufferSize = Settings.ReceiveBufferSize; } catch (SocketException) { }
            if (OperatingSystem.IsWindows())
            {
                // SIO_UDP_CONNRESET: stop ICMP "port unreachable" from breaking ReceiveFrom.
                const int SIO_UDP_CONNRESET = -1744830452;
                try { s.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch (SocketException) { }
            }
            // Bind to Any: on Linux / Android a socket bound to a unicast address does not receive broadcasts.
            s.Bind(new IPEndPoint(IPAddress.Any, Settings.Port));
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    /// <summary>The local address reported to a controller: the configured address, the interface on the requester's network, or the default interface.</summary>
    private IPAddress ReplyAddressFor(IPAddress? requester)
    {
        if (!Settings.LocalAddress.Equals(IPAddress.Any)) return Settings.LocalAddress;
        if (requester is not null && !IPAddress.IsLoopback(requester))
        {
            var match = ArtNetNetworkInterface.GetAll().FirstOrDefault(i => i.IsUp && ArtNetNetworkInterface.SameNetwork(i.Address, requester, i.Mask));
            if (match is not null) return match.Address;
        }
        return Interface?.Address ?? IPAddress.Any;
    }

    private bool IsLocalAddress(IPAddress address) => IPAddress.IsLoopback(address) || _localAddresses.Contains(address);

    private static HashSet<IPAddress> GetLocalAddresses()
    {
        try { return ArtNetNetworkInterface.GetAll(includeLoopback: true).Select(i => i.Address).ToHashSet(); }
        catch { return []; }
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x is null || y is null ? 0 : x.AsSpan().SequenceCompareTo(y);
    }
}
