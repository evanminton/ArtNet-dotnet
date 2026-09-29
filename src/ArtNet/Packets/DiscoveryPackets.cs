using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace ArtNet;

/// <summary>
/// ArtPoll (OpPoll 0x2000): discovers controllers, nodes and media servers. Broadcast by a controller to the directed
/// broadcast address every 2.5-3 s; every device answers with ArtPollReply. Consumers accept 14 bytes or more.
/// </summary>
public sealed class ArtPollPacket : ArtNetPacket
{
    public const int FullSize = 22;
    public const int MinSize = 14;

    public override ArtNetOpCode OpCode => ArtNetOpCode.Poll;
    public override int Size => FullSize;

    /// <summary>Behaviour requested from nodes.</summary>
    public ArtPollFlags Flags { get; set; }

    /// <summary>Lowest diagnostics priority that should be sent (Table 5).</summary>
    public ArtNetDiagnosticPriority DiagPriority { get; set; } = ArtNetDiagnosticPriority.Low;

    /// <summary>Top of the Port-Address range tested in targeted mode.</summary>
    public ushort TargetPortAddressTop { get; set; }

    /// <summary>Bottom of the Port-Address range tested in targeted mode.</summary>
    public ushort TargetPortAddressBottom { get; set; }

    /// <summary>ESTA manufacturer code of the sender.</summary>
    public ushort EstaManufacturer { get; set; }

    /// <summary>OEM code of the sender.</summary>
    public ushort Oem { get; set; }

    public bool TargetedMode => Flags.HasFlag(ArtPollFlags.TargetedMode);

    /// <summary>True when a node subscribed to <paramref name="address"/> must reply (always true without targeted mode).</summary>
    public bool Targets(PortAddress address) =>
        !TargetedMode || (address.Value >= TargetPortAddressBottom && address.Value <= TargetPortAddressTop);

    /// <summary>Creates a targeted poll for an inclusive Port-Address range.</summary>
    public static ArtPollPacket Targeted(PortAddress bottom, PortAddress top, ArtPollFlags flags = ArtPollFlags.None) => new()
    {
        Flags = flags | ArtPollFlags.TargetedMode,
        TargetPortAddressBottom = bottom.Value,
        TargetPortAddressTop = top.Value,
    };

    protected override void WriteBody(Span<byte> p)
    {
        p[12] = (byte)Flags;
        p[13] = (byte)DiagPriority;
        Bin.U16BE(p, 14, TargetPortAddressTop);
        Bin.U16BE(p, 16, TargetPortAddressBottom);
        Bin.U16BE(p, 18, EstaManufacturer);
        Bin.U16BE(p, 20, Oem);
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        Flags = (ArtPollFlags)Bin.U8(p, 12);
        DiagPriority = (ArtNetDiagnosticPriority)Bin.U8(p, 13);
        TargetPortAddressTop = Bin.U16BE(p, 14);
        TargetPortAddressBottom = Bin.U16BE(p, 16);
        EstaManufacturer = Bin.U16BE(p, 18);
        Oem = Bin.U16BE(p, 20);
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "Poll";
        f.Add(new(s, "Flags", Flags.ToDisplayName(), Bin.HexByte((byte)Flags)));
        f.Add(new(s, "Reply On Change", Flags.HasFlag(ArtPollFlags.ReplyOnChange) ? "Yes – send ArtPollReply whenever conditions change" : "No – only reply to ArtPoll / ArtAddress"));
        f.Add(new(s, "Diagnostics", !Flags.HasFlag(ArtPollFlags.Diagnostics) ? "Not requested"
            : Flags.HasFlag(ArtPollFlags.DiagnosticsUnicast) ? "Requested, unicast" : "Requested, broadcast"));
        f.Add(new(s, "Diagnostics Priority", DiagPriority.ToDisplayName(), Bin.HexByte((byte)DiagPriority)));
        f.Add(new(s, "VLC", Flags.HasFlag(ArtPollFlags.DisableVlc) ? "Disabled" : "Enabled"));
        f.Add(new(s, "Targeted Mode", TargetedMode
            ? $"On – Port-Address {TargetPortAddressBottom} to {TargetPortAddressTop}" : "Off"));
        f.Add(new(s, "ESTA Manufacturer", ArtNetText.FormatEsta(EstaManufacturer), Bin.HexWord(EstaManufacturer)));
        f.Add(new(s, "OEM", Bin.HexWord(Oem)));
    }

    public override string Summary => TargetedMode
        ? $"ArtPoll · targeted {TargetPortAddressBottom}-{TargetPortAddressTop}"
        : $"ArtPoll · {Flags.ToDisplayName()}";
}

/// <summary>One of the four ports described by an ArtPollReply.</summary>
/// <param name="Index">0-3 within the reply.</param>
/// <param name="Direction">Input and/or output capability (PortTypes bits 7-6).</param>
/// <param name="Protocol">PortTypes bits 5-0.</param>
/// <param name="InputAddress">Port-Address of the input side (NetSwitch + SubSwitch + SwIn).</param>
/// <param name="OutputAddress">Port-Address of the output side (NetSwitch + SubSwitch + SwOut).</param>
/// <param name="GoodInput">Input status (GoodInput[]).</param>
/// <param name="GoodOutputA">Output status (GoodOutputA[]).</param>
/// <param name="GoodOutputB">Output status (GoodOutputB[]).</param>
public sealed record ArtNetPortInfo(
    int Index,
    ArtNetPortDirection Direction,
    ArtNetPortProtocol Protocol,
    PortAddress InputAddress,
    PortAddress OutputAddress,
    ArtNetGoodInput GoodInput,
    ArtNetGoodOutputA GoodOutputA,
    ArtNetGoodOutputB GoodOutputB)
{
    public bool CanInput => Direction.HasFlag(ArtNetPortDirection.Input);
    public bool CanOutput => Direction.HasFlag(ArtNetPortDirection.Output);

    public string DirectionText => (CanInput, CanOutput) switch
    {
        (true, true) => "Input + Output",
        (true, false) => "Input",
        (false, true) => "Output",
        _ => "None",
    };

    public override string ToString()
    {
        var parts = new List<string> { $"Port {Index + 1}: {Protocol.ToDisplayName()} {DirectionText}" };
        if (CanOutput) parts.Add($"out {OutputAddress} {GoodOutputA.ToDisplayName()}");
        if (CanInput) parts.Add($"in {InputAddress} {GoodInput.ToDisplayName()}");
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// ArtPollReply (OpPollReply 0x2100): a device's status. Unicast only. 239 bytes; consumers accept 207 or more.
/// Has no protocol version field. One reply describes up to four ports; Art-Net 4 devices with more ports send one
/// reply per bind (BindIndex 1, 2, …), typically one port per reply.
/// </summary>
public sealed class ArtPollReplyPacket : ArtNetPacket
{
    public const int FullSize = 239;
    public const int MinSize = 207;

    public override ArtNetOpCode OpCode => ArtNetOpCode.PollReply;
    public override bool HasProtocolVersion => false;
    public override int Size => FullSize;

    /// <summary>Node IP address (bound nodes may share the root node's address).</summary>
    public IPAddress IpAddress { get; set; } = IPAddress.Any;

    /// <summary>Always 0x1936 (low byte first).</summary>
    public ushort Port { get; set; } = ArtNetConstants.Port;

    /// <summary>Firmware revision (VersInfoH:VersInfoL).</summary>
    public ushort FirmwareVersion { get; set; }

    /// <summary>Bits 14-8 of the Port-Address in the bottom 7 bits.</summary>
    public byte NetSwitch { get; set; }

    /// <summary>Bits 7-4 of the Port-Address in the bottom 4 bits.</summary>
    public byte SubSwitch { get; set; }

    /// <summary>OEM code identifying the product.</summary>
    public ushort Oem { get; set; } = ArtNetConstants.OemUnknown;

    /// <summary>User BIOS extension area firmware version (0 = not programmed).</summary>
    public byte UbeaVersion { get; set; }

    /// <summary>Raw Status1 register.</summary>
    public byte Status1 { get; set; }

    /// <summary>ESTA manufacturer code (transmitted Lo then Hi).</summary>
    public ushort EstaManufacturer { get; set; }

    /// <summary>Short name / port name (17 characters + null).</summary>
    public string ShortName { get; set; } = string.Empty;

    /// <summary>Long name (63 characters + null); identical in every bind of a device.</summary>
    public string LongName { get; set; } = string.Empty;

    /// <summary>"#xxxx [yyyy] zzzzz…" status report (see <see cref="FormatNodeReport"/>).</summary>
    public string NodeReport { get; set; } = string.Empty;

    /// <summary>Number of input or output ports (the larger), 0-4.</summary>
    public ushort NumPorts { get; set; }

    /// <summary>PortTypes[4]: bit 7 output, bit 6 input, bits 5-0 protocol.</summary>
    public byte[] PortTypes { get; private set; } = new byte[4];

    /// <summary>GoodInput[4].</summary>
    public byte[] GoodInput { get; private set; } = new byte[4];

    /// <summary>GoodOutputA[4].</summary>
    public byte[] GoodOutputA { get; private set; } = new byte[4];

    /// <summary>SwIn[4]: bits 3-0 of each input's Port-Address.</summary>
    public byte[] SwIn { get; private set; } = new byte[4];

    /// <summary>SwOut[4]: bits 3-0 of each output's Port-Address.</summary>
    public byte[] SwOut { get; private set; } = new byte[4];

    /// <summary>sACN priority used when received DMX is converted to sACN.</summary>
    public byte AcnPriority { get; set; }

    /// <summary>Macro key inputs (bit n = macro n+1 active).</summary>
    public byte SwMacro { get; set; }

    /// <summary>Remote trigger inputs (bit n = remote n+1 active).</summary>
    public byte SwRemote { get; set; }

    /// <summary>Equipment style (Table 4).</summary>
    public ArtNetStyle Style { get; set; } = ArtNetStyle.Node;

    /// <summary>MAC address (all zero if unknown).</summary>
    public byte[] Mac { get; private set; } = new byte[6];

    /// <summary>IP of the root device when part of a larger or modular product.</summary>
    public IPAddress BindIp { get; set; } = IPAddress.Any;

    /// <summary>Order of bound devices; 0 or 1 = root device.</summary>
    public byte BindIndex { get; set; } = 1;

    /// <summary>Status2 flags.</summary>
    public ArtNetStatus2 Status2 { get; set; } = ArtNetStatus2.PortAddress15Bit;

    /// <summary>GoodOutputB[4].</summary>
    public byte[] GoodOutputB { get; private set; } = new byte[4];

    /// <summary>Raw Status3 register.</summary>
    public byte Status3 { get; set; }

    /// <summary>RDMnet &amp; LLRP default responder UID.</summary>
    public RdmUid DefaultResponder { get; set; }

    /// <summary>User specific data.</summary>
    public ushort User { get; set; }

    /// <summary>Maximum ArtDmx rate in Hz this device can process (0-44 = DMX512 rate).</summary>
    public ushort RefreshRate { get; set; }

    /// <summary>How RDM status messages are collected (only valid when Status3 bit 1 is set).</summary>
    public byte BackgroundQueuePolicy { get; set; }

    // ------------------------------------------------------------ typed views of the bit fields

    public ArtNetStatus1 Status1Flags
    {
        get => (ArtNetStatus1)(Status1 & 0x07);
        set => Status1 = (byte)((Status1 & 0xF8) | ((byte)value & 0x07));
    }

    public ArtNetIndicatorState IndicatorState
    {
        get => (ArtNetIndicatorState)((Status1 >> 6) & 0x03);
        set => Status1 = (byte)((Status1 & 0x3F) | (((byte)value & 0x03) << 6));
    }

    public ArtNetPortAddressAuthority PortAddressAuthority
    {
        get => (ArtNetPortAddressAuthority)((Status1 >> 4) & 0x03);
        set => Status1 = (byte)((Status1 & 0xCF) | (((byte)value & 0x03) << 4));
    }

    public ArtNetStatus3 Status3Flags
    {
        get => (ArtNetStatus3)(Status3 & 0x3F);
        set => Status3 = (byte)((Status3 & 0xC0) | ((byte)value & 0x3F));
    }

    public ArtNetFailsafeState FailsafeState
    {
        get => (ArtNetFailsafeState)((Status3 >> 6) & 0x03);
        set => Status3 = (byte)((Status3 & 0x3F) | (((byte)value & 0x03) << 6));
    }

    public string MacAddress => Bin.Mac(Mac);

    public bool IsRootDevice => BindIndex <= 1;

    /// <summary>
    /// Ports actually described: the larger of NumPorts (capped to 4) and the highest port PortTypes marks as input or
    /// output. The spec makes the port information implicit in PortTypes, and some nodes under-report NumPorts.
    /// </summary>
    public int PortCount
    {
        get
        {
            int n = Math.Min((int)NumPorts, ArtNetConstants.PortsPerBind);
            for (int i = ArtNetConstants.PortsPerBind - 1; i >= n; i--)
                if ((PortTypes[i] & 0xC0) != 0) return i + 1;
            return n;
        }
    }

    public PortAddress GetInputAddress(int port) => PortAddress.FromSwitches(NetSwitch, SubSwitch, SwIn[port]);
    public PortAddress GetOutputAddress(int port) => PortAddress.FromSwitches(NetSwitch, SubSwitch, SwOut[port]);

    public ArtNetPortInfo GetPort(int index) => new(
        index,
        (ArtNetPortDirection)(PortTypes[index] & 0xC0),
        (ArtNetPortProtocol)(PortTypes[index] & 0x3F),
        GetInputAddress(index),
        GetOutputAddress(index),
        (ArtNetGoodInput)GoodInput[index],
        (ArtNetGoodOutputA)GoodOutputA[index],
        (ArtNetGoodOutputB)GoodOutputB[index]);

    /// <summary>The described ports.</summary>
    public IReadOnlyList<ArtNetPortInfo> Ports => Enumerable.Range(0, PortCount).Select(GetPort).ToArray();

    /// <summary>
    /// Universes this device is subscribed to: every Port-Address listed in SwIn or SwOut of a described port
    /// (the spec's definition of a unicast subscription).
    /// </summary>
    public IEnumerable<PortAddress> SubscribedAddresses
    {
        get
        {
            for (int i = 0; i < PortCount; i++)
            {
                var dir = (ArtNetPortDirection)(PortTypes[i] & 0xC0);
                if (dir.HasFlag(ArtNetPortDirection.Output)) yield return GetOutputAddress(i);
                if (dir.HasFlag(ArtNetPortDirection.Input)) yield return GetInputAddress(i);
            }
        }
    }

    /// <summary>True when an ArtDmx for <paramref name="address"/> must be unicast to this device.</summary>
    public bool IsSubscribedTo(PortAddress address) => SubscribedAddresses.Contains(address);

    /// <summary>Output ports that want ArtDmx for <paramref name="address"/>.</summary>
    public bool OutputsAddress(PortAddress address)
    {
        for (int i = 0; i < PortCount; i++)
            if ((PortTypes[i] & 0x80) != 0 && GetOutputAddress(i) == address) return true;
        return false;
    }

    /// <summary>Sets port <paramref name="index"/> and grows <see cref="NumPorts"/> as needed.</summary>
    public void SetPort(int index, ArtNetPortDirection direction, ArtNetPortProtocol protocol, byte swIn, byte swOut)
    {
        if (index is < 0 or >= ArtNetConstants.PortsPerBind) throw new ArgumentOutOfRangeException(nameof(index));
        PortTypes[index] = (byte)((byte)direction | ((byte)protocol & 0x3F));
        SwIn[index] = (byte)(swIn & 0x0F);
        SwOut[index] = (byte)(swOut & 0x0F);
        if (NumPorts < index + 1) NumPorts = (ushort)(index + 1);
    }

    // ------------------------------------------------------------ node report

    private static readonly Regex NodeReportPattern = new(@"^#([0-9A-Fa-f]{1,4})\s*\[(\d{1,4})\]\s*(.*)$", RegexOptions.Compiled);

    /// <summary>Formats "#xxxx [yyyy] text": hex status code (Table 3), decimal counter 0-9999, English text.</summary>
    public static string FormatNodeReport(ArtNetNodeReportCode code, int counter, string? text = null) =>
        $"#{(ushort)code:x4} [{counter % 10000:0000}] {text ?? code.ToDescription()}";

    /// <summary>Splits a node report into code, counter and text.</summary>
    public static bool TryParseNodeReport(string? report, out ArtNetNodeReportCode code, out int counter, out string text)
    {
        code = default; counter = 0; text = report ?? string.Empty;
        if (string.IsNullOrEmpty(report)) return false;
        var m = NodeReportPattern.Match(report.Trim());
        if (!m.Success) return false;
        code = (ArtNetNodeReportCode)ushort.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        counter = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        text = m.Groups[3].Value;
        return true;
    }

    // ------------------------------------------------------------ codec

    protected override void WriteBody(Span<byte> p)
    {
        Bin.Ip(p, 10, IpAddress);
        Bin.U16LE(p, 14, Port);
        Bin.U16BE(p, 16, FirmwareVersion);
        p[18] = (byte)(NetSwitch & 0x7F);
        p[19] = (byte)(SubSwitch & 0x0F);
        Bin.U16BE(p, 20, Oem);
        p[22] = UbeaVersion;
        p[23] = Status1;
        Bin.U16LE(p, 24, EstaManufacturer);
        Bin.Ascii(p, 26, 18, ShortName);
        Bin.Ascii(p, 44, 64, LongName);
        Bin.Ascii(p, 108, 64, NodeReport);
        Bin.U16BE(p, 172, NumPorts);
        PortTypes.AsSpan(0, 4).CopyTo(p[174..]);
        GoodInput.AsSpan(0, 4).CopyTo(p[178..]);
        GoodOutputA.AsSpan(0, 4).CopyTo(p[182..]);
        for (int i = 0; i < 4; i++)
        {
            p[186 + i] = (byte)(SwIn[i] & 0x0F);
            p[190 + i] = (byte)(SwOut[i] & 0x0F);
        }
        p[194] = AcnPriority;
        p[195] = SwMacro;
        p[196] = SwRemote;
        p[200] = (byte)Style;
        Mac.AsSpan(0, 6).CopyTo(p[201..]);
        Bin.Ip(p, 207, BindIp);
        p[211] = BindIndex;
        p[212] = (byte)Status2;
        GoodOutputB.AsSpan(0, 4).CopyTo(p[213..]);
        p[217] = Status3;
        DefaultResponder.Write(p[218..]);
        Bin.U16BE(p, 224, User);
        Bin.U16BE(p, 226, RefreshRate);
        p[228] = BackgroundQueuePolicy;
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        IpAddress = Bin.Ip(p, 10);
        Port = Bin.U16LE(p, 14);
        FirmwareVersion = Bin.U16BE(p, 16);
        NetSwitch = (byte)(Bin.U8(p, 18) & 0x7F);
        SubSwitch = (byte)(Bin.U8(p, 19) & 0x0F);
        Oem = Bin.U16BE(p, 20);
        UbeaVersion = Bin.U8(p, 22);
        Status1 = Bin.U8(p, 23);
        EstaManufacturer = Bin.U16LE(p, 24);
        ShortName = Bin.Ascii(p, 26, 18);
        LongName = Bin.Ascii(p, 44, 64);
        NodeReport = Bin.Ascii(p, 108, 64);
        NumPorts = Bin.U16BE(p, 172);
        PortTypes = Bin.FixedBytes(p, 174, 4);
        GoodInput = Bin.FixedBytes(p, 178, 4);
        GoodOutputA = Bin.FixedBytes(p, 182, 4);
        SwIn = Bin.FixedBytes(p, 186, 4);
        SwOut = Bin.FixedBytes(p, 190, 4);
        for (int i = 0; i < 4; i++) { SwIn[i] &= 0x0F; SwOut[i] &= 0x0F; }
        AcnPriority = Bin.U8(p, 194);
        SwMacro = Bin.U8(p, 195);
        SwRemote = Bin.U8(p, 196);
        Style = (ArtNetStyle)Bin.U8(p, 200);
        Mac = Bin.FixedBytes(p, 201, 6);
        BindIp = Bin.Ip(p, 207);
        BindIndex = Bin.U8(p, 211);
        Status2 = (ArtNetStatus2)Bin.U8(p, 212);
        GoodOutputB = Bin.FixedBytes(p, 213, 4);
        Status3 = Bin.U8(p, 217);
        DefaultResponder = RdmUid.Read(Bin.FixedBytes(p, 218, 6));
        User = Bin.U16BE(p, 224);
        RefreshRate = Bin.U16BE(p, 226);
        BackgroundQueuePolicy = Bin.U8(p, 228);
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string n = "Node";
        f.Add(new(n, "IP Address", IpAddress.ToString()));
        f.Add(new(n, "Port", Port.ToString(), Bin.HexWord(Port)));
        f.Add(new(n, "Short Name", ShortName));
        f.Add(new(n, "Long Name", LongName));
        f.Add(new(n, "Style", Style.ToDisplayName(), ((byte)Style).ToString()));
        f.Add(new(n, "Firmware Version", ArtNetText.FormatVersion(FirmwareVersion), Bin.HexWord(FirmwareVersion)));
        f.Add(new(n, "UBEA Version", UbeaVersion.ToString()));
        f.Add(new(n, "OEM", Bin.HexWord(Oem)));
        f.Add(new(n, "ESTA Manufacturer", ArtNetText.FormatEsta(EstaManufacturer), Bin.HexWord(EstaManufacturer)));
        f.Add(new(n, "MAC Address", MacAddress));
        f.Add(new(n, "Bind IP", BindIp.ToString()));
        f.Add(new(n, "Bind Index", BindIndex <= 1 ? $"{BindIndex} (root device)" : BindIndex.ToString()));
        f.Add(new(n, "User Data", Bin.HexWord(User)));
        f.Add(new(n, "Refresh Rate", RefreshRate <= ArtNetConstants.DmxRefreshRate ? $"{ArtNetConstants.DmxRefreshRate} Hz (DMX512)" : $"{RefreshRate} Hz", RefreshRate.ToString()));

        const string r = "Report";
        if (TryParseNodeReport(NodeReport, out var code, out int counter, out string text))
        {
            f.Add(new(r, "Code", code.ToDisplayName(), Bin.HexWord((ushort)code)));
            f.Add(new(r, "Counter", counter.ToString()));
            f.Add(new(r, "Text", text));
        }
        else f.Add(new(r, "Node Report", NodeReport));

        const string s = "Status";
        f.Add(new(s, "Status1", ArtNetText.JoinFlags(Status1Flags.ToDisplayName(), $"Indicators {IndicatorState.ToDisplayName()}", $"Port-Address set by {PortAddressAuthority.ToDisplayName()}"), Bin.HexByte(Status1)));
        f.Add(new(s, "Indicators", IndicatorState.ToDisplayName()));
        f.Add(new(s, "Port-Address Authority", PortAddressAuthority.ToDisplayName()));
        f.Add(new(s, "RDM", Status1Flags.HasFlag(ArtNetStatus1.RdmCapable) ? "Capable" : "Not capable"));
        f.Add(new(s, "Boot", Status1Flags.HasFlag(ArtNetStatus1.BootedFromRom) ? "Booted from ROM" : "Normal firmware boot"));
        f.Add(new(s, "Status2", Status2.ToDisplayName(), Bin.HexByte((byte)Status2)));
        f.Add(new(s, "Status3", ArtNetText.JoinFlags(Status3Flags.ToDisplayName(), $"Failsafe: {FailsafeState.ToDisplayName()}"), Bin.HexByte(Status3)));
        f.Add(new(s, "Failsafe", FailsafeState.ToDescription()));
        if (Status3Flags.HasFlag(ArtNetStatus3.BackgroundQueueSupported))
            f.Add(new(s, "Background Queue Policy", ArtNetText.FormatBackgroundQueuePolicy(BackgroundQueuePolicy), BackgroundQueuePolicy.ToString()));
        f.Add(new(s, "Default Responder UID", DefaultResponder.ToString()));
        f.Add(new(s, "sACN Priority", AcnPriority.ToString()));
        f.Add(new(s, "Macro Keys", ArtNetText.FormatBits(SwMacro, "Macro"), Bin.HexByte(SwMacro)));
        f.Add(new(s, "Remote Triggers", ArtNetText.FormatBits(SwRemote, "Remote"), Bin.HexByte(SwRemote)));

        const string a = "Addressing";
        f.Add(new(a, "Net Switch", NetSwitch.ToString()));
        f.Add(new(a, "Sub-Net Switch", SubSwitch.ToString()));
        f.Add(new(a, "Ports", NumPorts.ToString()));
        for (int i = 0; i < PortCount; i++)
        {
            var port = GetPort(i);
            string sec = $"Port {i + 1}";
            f.Add(new(sec, "Type", $"{port.Protocol.ToDisplayName()} · {port.DirectionText}", Bin.HexByte(PortTypes[i])));
            if (port.CanOutput)
            {
                f.Add(new(sec, "Output Port-Address", port.OutputAddress.ToString(), SwOut[i].ToString()));
                f.Add(new(sec, "Output Status", port.GoodOutputA.ToDisplayName(), Bin.HexByte(GoodOutputA[i])));
                f.Add(new(sec, "Output Status B", port.GoodOutputB.ToDisplayName(), Bin.HexByte(GoodOutputB[i])));
            }
            if (port.CanInput)
            {
                f.Add(new(sec, "Input Port-Address", port.InputAddress.ToString(), SwIn[i].ToString()));
                f.Add(new(sec, "Input Status", port.GoodInput.ToDisplayName(), Bin.HexByte(GoodInput[i])));
            }
        }
    }

    public override string Summary
    {
        get
        {
            var subs = string.Join(", ", SubscribedAddresses.Distinct().Select(x => x.Value));
            return $"ArtPollReply · {ShortName} · {IpAddress}" + (BindIndex > 1 ? $" bind {BindIndex}" : "") +
                   (subs.Length > 0 ? $" · universes {subs}" : "");
        }
    }
}
