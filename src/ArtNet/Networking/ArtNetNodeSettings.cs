using System.Net;

namespace ArtNet.Networking;

/// <summary>A DMX port this node announces in its ArtPollReply (one port per bind, BindIndex = position + 1).</summary>
/// <param name="Kind">Output ports subscribe to ArtDmx for <paramref name="Address"/>; input ports transmit it.</param>
/// <param name="Address">Port-Address of the port.</param>
/// <param name="Name">Optional port name (ArtPollReply → ShortName for this bind); defaults to the node's short name.</param>
public sealed record ArtNetPortConfig(ArtNetPortKind Kind, PortAddress Address, string? Name = null)
{
    public ArtNetPortProtocol Protocol { get; init; } = ArtNetPortProtocol.Dmx512;

    /// <summary>Merge mode used when two sources send to this output.</summary>
    public ArtNetMergeMode MergeMode { get; set; } = ArtNetMergeMode.Htp;

    /// <summary>Input disabled by ArtInput.</summary>
    public bool InputDisabled { get; set; }

    /// <summary>Converts from/to sACN instead of Art-Net (reported only).</summary>
    public bool Sacn { get; set; }

    /// <summary>RDM disabled on this output (reported only).</summary>
    public bool RdmDisabled { get; set; }

    /// <summary>Continuous (true) or delta output style (reported only).</summary>
    public bool ContinuousOutput { get; set; }

    public static ArtNetPortConfig Output(PortAddress address, string? name = null) => new(ArtNetPortKind.Output, address, name);
    public static ArtNetPortConfig Input(PortAddress address, string? name = null) => new(ArtNetPortKind.Input, address, name);

    public override string ToString() => $"{Kind.ToDisplayName()} {Address}" + (Name is null ? "" : $" \"{Name}\"");
}

/// <summary>Configuration of an <see cref="ArtNetNode"/>.</summary>
public sealed class ArtNetNodeSettings
{
    /// <summary>Short name (17 characters) reported in ArtPollReply.</summary>
    public string ShortName { get; set; } = "ArtNet.NET";

    /// <summary>Long name (63 characters) reported in ArtPollReply.</summary>
    public string LongName { get; set; } = "ArtNet.NET node";

    /// <summary>Equipment style reported in ArtPollReply (Controller, Node, Config, …).</summary>
    public ArtNetStyle Style { get; set; } = ArtNetStyle.Controller;

    /// <summary>OEM code (register one with Artistic Licence; defaults to OemUnknown 0x00FF).</summary>
    public ushort Oem { get; set; } = ArtNetConstants.OemUnknown;

    /// <summary>ESTA manufacturer code (defaults to the ESTA prototype ID 0x7FF0).</summary>
    public ushort EstaManufacturer { get; set; } = ArtNetConstants.EstaPrototype;

    /// <summary>Firmware revision reported in ArtPollReply.</summary>
    public ushort FirmwareVersion { get; set; } = 0x0100;

    /// <summary>Ports this node announces. Output ports receive unicast ArtDmx from compliant controllers.</summary>
    public List<ArtNetPortConfig> Ports { get; set; } = [];

    /// <summary>Local address to bind (Any = all interfaces). Also reported in ArtPollReply when set.</summary>
    public IPAddress LocalAddress { get; set; } = IPAddress.Any;

    /// <summary>Directed broadcast address for ArtPoll / ArtSync / broadcasts; null = derived from the local interface.</summary>
    public IPAddress? BroadcastAddress { get; set; }

    /// <summary>UDP port (0x1936 = 6454). Change only for tests.</summary>
    public int Port { get; set; } = ArtNetConstants.Port;

    /// <summary>Broadcast ArtPoll every <see cref="PollInterval"/> (controller behaviour).</summary>
    public bool SendPolls { get; set; } = true;

    /// <summary>2.5-3 s per the spec.</summary>
    public TimeSpan PollInterval { get; set; } = ArtNetConstants.PollInterval;

    /// <summary>Flags sent in our ArtPoll.</summary>
    public ArtPollFlags PollFlags { get; set; } = ArtPollFlags.ReplyOnChange;

    /// <summary>Lowest diagnostics priority requested in our ArtPoll.</summary>
    public ArtNetDiagnosticPriority PollDiagnosticPriority { get; set; } = ArtNetDiagnosticPriority.Low;

    /// <summary>Answer ArtPoll with ArtPollReply (every Art-Net device must).</summary>
    public bool ReplyToPolls { get; set; } = true;

    /// <summary>Upper bound of the random ArtPollReply delay (spec: up to 1 s). Zero replies immediately.</summary>
    public TimeSpan MaxReplyDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Nodes not heard from for this long are removed.</summary>
    public TimeSpan NodeTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Re-send the last frame of every output universe at this interval (0 = off). Spec: 800-1000 ms.</summary>
    public TimeSpan DmxKeepAlive { get; set; } = ArtNetConstants.DmxKeepAlive;

    /// <summary>
    /// Not spec compliant: broadcast ArtDmx when no node is subscribed to the universe (helps legacy / misconfigured receivers).
    /// </summary>
    public bool BroadcastDmxWithoutSubscribers { get; set; }

    /// <summary>Extra unicast destinations for every ArtDmx (e.g. nodes on another subnet or not replying to ArtPoll).</summary>
    public List<IPEndPoint> StaticDmxTargets { get; set; } = [];

    /// <summary>Drop out-of-order ArtDmx using the sequence field.</summary>
    public bool UseSequenceNumbers { get; set; } = true;

    /// <summary>Default merge mode for received universes not configured in <see cref="Ports"/>.</summary>
    public ArtNetMergeMode DefaultMergeMode { get; set; } = ArtNetMergeMode.Htp;

    /// <summary>A failed merge source is held for this long (spec: 10 s).</summary>
    public TimeSpan MergeTimeout { get; set; } = ArtNetConstants.MergeTimeout;

    /// <summary>Honour ArtSync (synchronous output mode, 4 s timeout).</summary>
    public bool EnableSync { get; set; } = true;

    /// <summary>Apply ArtAddress / ArtInput / ArtCommand addressed to this node (names, switches, commands).</summary>
    public bool AcceptRemoteProgramming { get; set; } = true;

    /// <summary>URLs returned for ArtDataRequest (Poll is answered automatically when any are set).</summary>
    public Dictionary<ArtNetDataRequestCode, string> DataReplies { get; set; } = [];

    /// <summary>Ignore packets whose source is one of this machine's addresses (except ArtPoll/ArtPollReply).</summary>
    public bool IgnoreOwnDmx { get; set; }

    /// <summary>When set, events are posted here (use the UI SynchronizationContext in MAUI apps).</summary>
    public SynchronizationContext? EventContext { get; set; }

    /// <summary>Socket receive buffer size.</summary>
    public int ReceiveBufferSize { get; set; } = 1 << 20;

    /// <summary>Timeout for request/response helpers (ArtIpProg, ArtDataRequest, ArtAddress).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>MAC address reported in ArtPollReply (null = from the bound interface).</summary>
    public byte[]? MacAddress { get; set; }

    /// <summary>Status2 flags reported (15-bit Port-Address support is always set).</summary>
    public ArtNetStatus2 Status2 { get; set; } = ArtNetStatus2.PortAddress15Bit | ArtNetStatus2.DhcpCapable;

    /// <summary>User data reported in ArtPollReply.</summary>
    public ushort UserData { get; set; }

    /// <summary>Maximum refresh rate reported (0 = DMX512 rate).</summary>
    public ushort RefreshRate { get; set; }

    public ArtNetNodeSettings Clone()
    {
        var c = (ArtNetNodeSettings)MemberwiseClone();
        c.Ports = Ports.Select(p => p with { }).ToList();
        c.StaticDmxTargets = [.. StaticDmxTargets];
        c.DataReplies = new(DataReplies);
        return c;
    }
}
