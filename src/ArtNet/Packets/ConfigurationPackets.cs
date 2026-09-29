using System.Net;
using System.Text;

namespace ArtNet;

/// <summary>
/// ArtIpProg (OpIpProg 0xF800): unicast by a controller to reprogram a node's IP address, subnet mask and default
/// gateway. With no command bits set it is an enquiry. Nodes that support it answer with ArtIpProgReply.
/// </summary>
public sealed class ArtIpProgPacket : ArtNetPacket
{
    public const int FullSize = 34;
    public const int MinSize = 34;

    public override ArtNetOpCode OpCode => ArtNetOpCode.IpProg;
    public override int Size => FullSize;

    public ArtIpProgCommand Command { get; set; }
    public IPAddress ProgIp { get; set; } = IPAddress.Any;
    public IPAddress ProgSubnetMask { get; set; } = IPAddress.Any;

    /// <summary>Deprecated.</summary>
    public ushort ProgPort { get; set; }

    public IPAddress ProgDefaultGateway { get; set; } = IPAddress.Any;

    public bool IsEnquiry => Command == ArtIpProgCommand.None;

    /// <summary>Enquiry only: asks for the current settings.</summary>
    public static ArtIpProgPacket Enquiry() => new();

    /// <summary>Programs a static IP, mask and (optionally) gateway.</summary>
    public static ArtIpProgPacket Program(IPAddress ip, IPAddress mask, IPAddress? gateway = null)
    {
        var c = ArtIpProgCommand.EnableProgramming | ArtIpProgCommand.ProgramIp | ArtIpProgCommand.ProgramSubnetMask;
        if (gateway is not null) c |= ArtIpProgCommand.ProgramGateway;
        return new() { Command = c, ProgIp = ip, ProgSubnetMask = mask, ProgDefaultGateway = gateway ?? IPAddress.Any };
    }

    /// <summary>Switches the node to DHCP.</summary>
    public static ArtIpProgPacket EnableDhcp() => new() { Command = ArtIpProgCommand.EnableProgramming | ArtIpProgCommand.EnableDhcp };

    /// <summary>Returns IP, mask and port to their defaults.</summary>
    public static ArtIpProgPacket ResetToDefault() => new() { Command = ArtIpProgCommand.EnableProgramming | ArtIpProgCommand.ResetToDefault };

    protected override void WriteBody(Span<byte> p)
    {
        p[14] = (byte)Command;
        Bin.Ip(p, 16, ProgIp);
        Bin.Ip(p, 20, ProgSubnetMask);
        Bin.U16BE(p, 24, ProgPort);
        Bin.Ip(p, 26, ProgDefaultGateway);
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        Command = (ArtIpProgCommand)Bin.U8(p, 14);
        ProgIp = Bin.Ip(p, 16);
        ProgSubnetMask = Bin.Ip(p, 20);
        ProgPort = Bin.U16BE(p, 24);
        ProgDefaultGateway = Bin.Ip(p, 26);
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "IP Programming";
        f.Add(new(s, "Command", IsEnquiry ? "Enquiry only" : Command.ToDisplayName(), Bin.HexByte((byte)Command)));
        f.Add(new(s, "IP Address", ProgIp.ToString()));
        f.Add(new(s, "Subnet Mask", ProgSubnetMask.ToString()));
        f.Add(new(s, "Default Gateway", ProgDefaultGateway.ToString()));
        f.Add(new(s, "Port (deprecated)", ProgPort.ToString()));
    }

    public override string Summary => IsEnquiry ? "ArtIpProg · enquiry" : $"ArtIpProg · {Command.ToDisplayName()} · {ProgIp}/{ProgSubnetMask}";
}

/// <summary>ArtIpProgReply (OpIpProgReply 0xF900): a node's current IP settings, unicast to the sender of ArtIpProg.</summary>
public sealed class ArtIpProgReplyPacket : ArtNetPacket
{
    public const int FullSize = 34;
    public const int MinSize = 28;

    public override ArtNetOpCode OpCode => ArtNetOpCode.IpProgReply;
    public override int Size => FullSize;

    public IPAddress ProgIp { get; set; } = IPAddress.Any;
    public IPAddress ProgSubnetMask { get; set; } = IPAddress.Any;

    /// <summary>Deprecated.</summary>
    public ushort ProgPort { get; set; }

    public ArtIpProgStatus Status { get; set; }
    public IPAddress ProgDefaultGateway { get; set; } = IPAddress.Any;

    public bool DhcpEnabled => Status.HasFlag(ArtIpProgStatus.DhcpEnabled);

    protected override void WriteBody(Span<byte> p)
    {
        Bin.Ip(p, 16, ProgIp);
        Bin.Ip(p, 20, ProgSubnetMask);
        Bin.U16BE(p, 24, ProgPort);
        p[26] = (byte)Status;
        Bin.Ip(p, 28, ProgDefaultGateway);
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        ProgIp = Bin.Ip(p, 16);
        ProgSubnetMask = Bin.Ip(p, 20);
        ProgPort = Bin.U16BE(p, 24);
        Status = (ArtIpProgStatus)Bin.U8(p, 26);
        ProgDefaultGateway = Bin.Ip(p, 28);
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "IP Settings";
        f.Add(new(s, "IP Address", ProgIp.ToString()));
        f.Add(new(s, "Subnet Mask", ProgSubnetMask.ToString()));
        f.Add(new(s, "Default Gateway", ProgDefaultGateway.ToString()));
        f.Add(new(s, "DHCP", DhcpEnabled ? "Enabled" : "Disabled", Bin.HexByte((byte)Status)));
        f.Add(new(s, "Port (deprecated)", ProgPort.ToString()));
    }

    public override string Summary => $"ArtIpProgReply · {ProgIp}/{ProgSubnetMask} gw {ProgDefaultGateway}{(DhcpEnabled ? " · DHCP" : "")}";
}

/// <summary>
/// ArtAddress (OpAddress 0x6000): unicast by a controller to reprogram a node's names, Port-Address switches,
/// sACN priority and to send a configuration command. The node replies with ArtPollReply.
/// Switch values are only applied when bit 7 is set (see <see cref="Program"/>); 0x00 resets to the physical switch;
/// 0x7F (without bit 7) means "no change".
/// </summary>
public sealed class ArtAddressPacket : ArtNetPacket
{
    public const int FullSize = 107;
    public const int MinSize = 107;

    /// <summary>Switch value meaning "no change".</summary>
    public const byte NoChange = 0x7F;

    /// <summary>Switch value meaning "reset to the physical switch setting".</summary>
    public const byte ResetToPhysical = 0x00;

    /// <summary>AcnPriority value meaning "no change".</summary>
    public const byte AcnPriorityNoChange = 255;

    public override ArtNetOpCode OpCode => ArtNetOpCode.Address;
    public override int Size => FullSize;

    /// <summary>Raw NetSwitch (value | 0x80 to program).</summary>
    public byte NetSwitch { get; set; } = NoChange;

    /// <summary>Bound node addressed (1 = root device).</summary>
    public byte BindIndex { get; set; } = 1;

    /// <summary>Port name; ignored by the node when empty.</summary>
    public string ShortName { get; set; } = string.Empty;

    /// <summary>Long name; ignored by the node when empty.</summary>
    public string LongName { get; set; } = string.Empty;

    /// <summary>Raw SwIn[4] (value | 0x80 to program).</summary>
    public byte[] SwIn { get; private set; } = [NoChange, NoChange, NoChange, NoChange];

    /// <summary>Raw SwOut[4] (value | 0x80 to program).</summary>
    public byte[] SwOut { get; private set; } = [NoChange, NoChange, NoChange, NoChange];

    /// <summary>Raw SubSwitch (value | 0x80 to program).</summary>
    public byte SubSwitch { get; set; } = NoChange;

    /// <summary>sACN priority 0-200, 255 = no change.</summary>
    public byte AcnPriority { get; set; } = AcnPriorityNoChange;

    public ArtNetAddressCommand Command { get; set; }

    /// <summary>Encodes a switch value to be programmed (sets bit 7).</summary>
    public static byte Program(int value) => (byte)(0x80 | (value & 0x7F));

    /// <summary>True when a raw switch byte asks the node to program a value.</summary>
    public static bool IsProgrammed(byte raw) => (raw & 0x80) != 0;

    /// <summary>Programs Net, Sub-Net and output port 1 universe so that port 1 outputs <paramref name="address"/>.</summary>
    public ArtAddressPacket SetOutputAddress(PortAddress address, int port = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(port, SwOut.Length);
        NetSwitch = Program(address.Net);
        SubSwitch = Program(address.SubNet);
        SwOut[port] = Program(address.Universe);
        return this;
    }

    /// <summary>Programs Net, Sub-Net and input port universe so that the port inputs to <paramref name="address"/>.</summary>
    public ArtAddressPacket SetInputAddress(PortAddress address, int port = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(port, SwIn.Length);
        NetSwitch = Program(address.Net);
        SubSwitch = Program(address.SubNet);
        SwIn[port] = Program(address.Universe);
        return this;
    }

    /// <summary>A command-only packet (names and switches unchanged).</summary>
    public static ArtAddressPacket ForCommand(ArtNetAddressCommand command, byte bindIndex = 1) => new() { Command = command, BindIndex = bindIndex };

    protected override void WriteBody(Span<byte> p)
    {
        p[12] = NetSwitch;
        p[13] = BindIndex;
        Bin.Ascii(p, 14, 18, ShortName);
        Bin.Ascii(p, 32, 64, LongName);
        SwIn.AsSpan(0, 4).CopyTo(p[96..]);
        SwOut.AsSpan(0, 4).CopyTo(p[100..]);
        p[104] = SubSwitch;
        p[105] = AcnPriority;
        p[106] = (byte)Command;
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        NetSwitch = Bin.U8(p, 12);
        BindIndex = Bin.U8(p, 13);
        ShortName = Bin.Ascii(p, 14, 18);
        LongName = Bin.Ascii(p, 32, 64);
        SwIn = Bin.FixedBytes(p, 96, 4);
        SwOut = Bin.FixedBytes(p, 100, 4);
        SubSwitch = Bin.U8(p, 104);
        AcnPriority = Bin.U8(p, 105);
        Command = (ArtNetAddressCommand)Bin.U8(p, 106);
    }

    private static string Switch(byte raw) =>
        IsProgrammed(raw) ? $"Set to {raw & 0x7F}" : raw == 0 ? "Reset to physical switch" : "No change";

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "Programming";
        f.Add(new(s, "Bind Index", BindIndex.ToString()));
        f.Add(new(s, "Short Name", ShortName.Length == 0 ? "(no change)" : ShortName));
        f.Add(new(s, "Long Name", LongName.Length == 0 ? "(no change)" : LongName));
        f.Add(new(s, "Net Switch", Switch(NetSwitch), Bin.HexByte(NetSwitch)));
        f.Add(new(s, "Sub-Net Switch", Switch(SubSwitch), Bin.HexByte(SubSwitch)));
        for (int i = 0; i < 4; i++)
        {
            if (SwIn[i] != NoChange) f.Add(new(s, $"SwIn {i + 1}", Switch(SwIn[i]), Bin.HexByte(SwIn[i])));
            if (SwOut[i] != NoChange) f.Add(new(s, $"SwOut {i + 1}", Switch(SwOut[i]), Bin.HexByte(SwOut[i])));
        }
        f.Add(new(s, "sACN Priority", AcnPriority == AcnPriorityNoChange ? "No change" : AcnPriority.ToString(), AcnPriority.ToString()));
        f.Add(new(s, "Command", Command.ToDisplayName(), Bin.HexByte((byte)Command)));
        f.Add(new(s, "Command Action", Command.ToDescription()));
    }

    public override string Summary
    {
        get
        {
            var parts = new List<string> { "ArtAddress" };
            if (Command != ArtNetAddressCommand.None) parts.Add(Command.ToDisplayName());
            if (ShortName.Length > 0) parts.Add($"name \"{ShortName}\"");
            if (IsProgrammed(NetSwitch) || IsProgrammed(SubSwitch) || SwOut.Any(IsProgrammed) || SwIn.Any(IsProgrammed)) parts.Add("switches");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>ArtInput (OpInput 0x7000): enables or disables DMX512 inputs of a node. The node replies with ArtPollReply.</summary>
public sealed class ArtInputPacket : ArtNetPacket
{
    public const int FullSize = 20;
    public const int MinSize = 20;

    public override ArtNetOpCode OpCode => ArtNetOpCode.Input;
    public override int Size => FullSize;

    public byte BindIndex { get; set; } = 1;
    public ushort NumPorts { get; set; } = 1;

    /// <summary>Input[4]: bit 0 set = disable that input.</summary>
    public byte[] Input { get; private set; } = new byte[4];

    public bool IsDisabled(int port) => (Input[port] & 0x01) != 0;

    public void SetDisabled(int port, bool disabled) => Input[port] = (byte)(disabled ? 0x01 : 0x00);

    protected override void WriteBody(Span<byte> p)
    {
        p[13] = BindIndex;
        Bin.U16BE(p, 14, NumPorts);
        Input.AsSpan(0, 4).CopyTo(p[16..]);
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        BindIndex = Bin.U8(p, 13);
        NumPorts = Bin.U16BE(p, 14);
        Input = Bin.FixedBytes(p, 16, 4);
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "Inputs";
        f.Add(new(s, "Bind Index", BindIndex.ToString()));
        f.Add(new(s, "Ports", NumPorts.ToString()));
        for (int i = 0; i < Math.Clamp((int)NumPorts, 1, 4); i++)
            f.Add(new(s, $"Input {i + 1}", IsDisabled(i) ? "Disabled" : "Enabled", Bin.HexByte(Input[i])));
    }

    public override string Summary =>
        $"ArtInput · bind {BindIndex} · " + string.Join(" ", Enumerable.Range(0, Math.Clamp((int)NumPorts, 1, 4)).Select(i => IsDisabled(i) ? "off" : "on"));
}

/// <summary>ArtDataRequest (OpDataRequest 0x2700): asks a node for data such as product URLs (Table 4a).</summary>
public sealed class ArtDataRequestPacket : ArtNetPacket
{
    public const int FullSize = 40;
    public const int MinSize = 18;

    public override ArtNetOpCode OpCode => ArtNetOpCode.DataRequest;
    public override int Size => FullSize;

    public ushort EstaManufacturer { get; set; }
    public ushort Oem { get; set; }

    /// <summary>Requested data (0x8000-0xFFFF manufacturer specific).</summary>
    public ushort Request { get; set; }

    public ArtNetDataRequestCode RequestCode
    {
        get => (ArtNetDataRequestCode)Request;
        set => Request = (ushort)value;
    }

    protected override void WriteBody(Span<byte> p)
    {
        Bin.U16BE(p, 12, EstaManufacturer);
        Bin.U16BE(p, 14, Oem);
        Bin.U16BE(p, 16, Request);
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        EstaManufacturer = Bin.U16BE(p, 12);
        Oem = Bin.U16BE(p, 14);
        Request = Bin.U16BE(p, 16);
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "Request";
        f.Add(new(s, "ESTA Manufacturer", ArtNetText.FormatEsta(EstaManufacturer), Bin.HexWord(EstaManufacturer)));
        f.Add(new(s, "OEM", Bin.HexWord(Oem)));
        f.Add(new(s, "Request", ArtNetText.FormatDataRequest(Request), Bin.HexWord(Request)));
    }

    public override string Summary => $"ArtDataRequest · {ArtNetText.FormatDataRequest(Request)}";
}

/// <summary>ArtDataReply (OpDataReply 0x2800): the answer to ArtDataRequest; string payloads are null terminated.</summary>
public sealed class ArtDataReplyPacket : ArtNetPacket
{
    public const int HeaderSize = 20;
    public const int MinSize = 20;
    public const int MaxPayload = 512;

    public override ArtNetOpCode OpCode => ArtNetOpCode.DataReply;
    public override int Size => HeaderSize + Payload.Length;

    public ushort EstaManufacturer { get; set; }
    public ushort Oem { get; set; }
    public ushort Request { get; set; }

    public ArtNetDataRequestCode RequestCode
    {
        get => (ArtNetDataRequestCode)Request;
        set => Request = (ushort)value;
    }

    private byte[] _payload = [];

    /// <summary>Reply data, 0-512 bytes.</summary>
    public byte[] Payload
    {
        get => _payload;
        set => _payload = value is { Length: > MaxPayload } ? value[..MaxPayload] : value ?? [];
    }

    /// <summary>Payload as a null-terminated string (URLs etc.).</summary>
    public string PayloadText
    {
        get => Bin.Ascii(_payload, 0, _payload.Length);
        set => Payload = string.IsNullOrEmpty(value) ? [] : Bin.NullTerminated(value, MaxPayload);
    }

    protected override void WriteBody(Span<byte> p)
    {
        Bin.U16BE(p, 12, EstaManufacturer);
        Bin.U16BE(p, 14, Oem);
        Bin.U16BE(p, 16, Request);
        Bin.U16BE(p, 18, (ushort)_payload.Length);
        _payload.CopyTo(p[HeaderSize..]);
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        EstaManufacturer = Bin.U16BE(p, 12);
        Oem = Bin.U16BE(p, 14);
        Request = Bin.U16BE(p, 16);
        int len = Math.Min((int)Bin.U16BE(p, 18), MaxPayload);
        Payload = Bin.Bytes(p, HeaderSize, len);
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "Reply";
        f.Add(new(s, "ESTA Manufacturer", ArtNetText.FormatEsta(EstaManufacturer), Bin.HexWord(EstaManufacturer)));
        f.Add(new(s, "OEM", Bin.HexWord(Oem)));
        f.Add(new(s, "Contents", ArtNetText.FormatDataRequest(Request), Bin.HexWord(Request)));
        f.Add(new(s, "Payload Length", $"{_payload.Length} bytes"));
        if (_payload.Length > 0)
            f.Add(new(s, "Payload", IsText(_payload) ? PayloadText : Bin.Hex(_payload, 64)));
    }

    internal static bool IsText(ReadOnlySpan<byte> b)
    {
        foreach (byte c in b)
        {
            if (c == 0) break;
            if (c is < 0x20 or > 0x7E && c is not (byte)'\r' and not (byte)'\n' and not (byte)'\t') return false;
        }
        return true;
    }

    public override string Summary =>
        $"ArtDataReply · {ArtNetText.FormatDataRequest(Request)}" + (_payload.Length > 0 && IsText(_payload) ? $" · {PayloadText}" : $" · {_payload.Length} bytes");
}
