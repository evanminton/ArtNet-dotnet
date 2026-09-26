using System.Text;

namespace ArtNet;

/// <summary>ArtDiagData (OpDiagData 0x2300): diagnostics text for display, sent as requested by ArtPoll.</summary>
public sealed class ArtDiagDataPacket : ArtNetPacket
{
    public const int HeaderSize = 18;
    public const int MinSize = 18;

    public override ArtNetOpCode OpCode => ArtNetOpCode.DiagData;
    public override int Size => HeaderSize + Data.Length;

    public ArtNetDiagnosticPriority Priority { get; set; } = ArtNetDiagnosticPriority.Low;

    /// <summary>Logical DMX port the message relates to (0 = general).</summary>
    public byte LogicalPort { get; set; }

    private byte[] _data = [0];

    /// <summary>Raw null-terminated ASCII text (max 512 bytes).</summary>
    public byte[] Data
    {
        get => _data;
        set => _data = value is { Length: > ArtNetConstants.MaxTextLength } ? value[..ArtNetConstants.MaxTextLength] : value ?? [];
    }

    public string Text
    {
        get => Bin.Ascii(_data, 0, _data.Length);
        set => _data = Bin.NullTerminated(value);
    }

    protected override void WriteBody(Span<byte> p)
    {
        p[13] = (byte)Priority;
        p[14] = LogicalPort;
        Bin.U16BE(p, 16, (ushort)_data.Length);
        _data.CopyTo(p[HeaderSize..]);
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        Priority = (ArtNetDiagnosticPriority)Bin.U8(p, 13);
        LogicalPort = Bin.U8(p, 14);
        Data = Bin.Bytes(p, HeaderSize, Math.Min((int)Bin.U16BE(p, 16), ArtNetConstants.MaxTextLength));
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "Diagnostics";
        f.Add(new(s, "Priority", Priority.ToDisplayName(), Bin.HexByte((byte)Priority)));
        f.Add(new(s, "Logical Port", LogicalPort == 0 ? "General" : LogicalPort.ToString()));
        f.Add(new(s, "Text", Text));
    }

    public override string Summary => $"ArtDiagData · {Priority.ToDisplayName()} · {Text}";
}

/// <summary>ArtTimeCode (OpTimeCode 0x9700): time code compatible with LTC and MTC.</summary>
public sealed class ArtTimeCodePacket : ArtNetPacket
{
    public const int FullSize = 19;
    public const int MinSize = 19;

    public override ArtNetOpCode OpCode => ArtNetOpCode.TimeCode;
    public override int Size => FullSize;

    /// <summary>Stream identifier (0 = master).</summary>
    public byte StreamId { get; set; }

    public byte Frames { get; set; }
    public byte Seconds { get; set; }
    public byte Minutes { get; set; }
    public byte Hours { get; set; }
    public ArtNetTimeCodeType Type { get; set; } = ArtNetTimeCodeType.Smpte;

    /// <summary>Nominal frames per second of <see cref="Type"/>.</summary>
    public double FrameRate => Type.FrameRate();

    /// <summary>Time of day represented (frames converted with the nominal rate).</summary>
    public TimeSpan ToTimeSpan() => new TimeSpan(0, Hours, Minutes, Seconds) + TimeSpan.FromSeconds(Frames / FrameRate);

    /// <summary>Builds a time code from a time span.</summary>
    public static ArtTimeCodePacket FromTimeSpan(TimeSpan time, ArtNetTimeCodeType type, byte streamId = 0)
    {
        int fps = type.NominalFrames();
        return new ArtTimeCodePacket
        {
            StreamId = streamId,
            Type = type,
            Hours = (byte)(time.Hours % 24),
            Minutes = (byte)time.Minutes,
            Seconds = (byte)time.Seconds,
            Frames = (byte)Math.Min(fps - 1, (int)(time.Milliseconds / 1000.0 * type.FrameRate())),
        };
    }

    /// <summary>"HH:MM:SS:FF" (';' before frames for drop frame).</summary>
    public string TimeText => $"{Hours:00}:{Minutes:00}:{Seconds:00}{(Type == ArtNetTimeCodeType.DropFrame ? ';' : ':')}{Frames:00}";

    /// <summary>Advances by one frame (wrapping at 24 h; drop frame skips frames 0 and 1 in minutes not divisible by 10).</summary>
    public void Increment()
    {
        int fps = Type.NominalFrames();
        if (++Frames < fps) return;
        Frames = 0;
        if (++Seconds >= 60)
        {
            Seconds = 0;
            if (++Minutes >= 60) { Minutes = 0; Hours = (byte)((Hours + 1) % 24); }
        }
        if (Type == ArtNetTimeCodeType.DropFrame && Seconds == 0 && Minutes % 10 != 0) Frames = 2;
    }

    protected override void WriteBody(Span<byte> p)
    {
        p[13] = StreamId;
        p[14] = Frames;
        p[15] = Seconds;
        p[16] = Minutes;
        p[17] = Hours;
        p[18] = (byte)Type;
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        StreamId = Bin.U8(p, 13);
        Frames = Bin.U8(p, 14);
        Seconds = Bin.U8(p, 15);
        Minutes = Bin.U8(p, 16);
        Hours = Bin.U8(p, 17);
        Type = (ArtNetTimeCodeType)Bin.U8(p, 18);
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "Time Code";
        f.Add(new(s, "Time", TimeText));
        f.Add(new(s, "Type", Type.ToDisplayName(), ((byte)Type).ToString()));
        f.Add(new(s, "Stream", StreamId == 0 ? "0 (master)" : StreamId.ToString()));
        f.Add(new(s, "Hours", Hours.ToString()));
        f.Add(new(s, "Minutes", Minutes.ToString()));
        f.Add(new(s, "Seconds", Seconds.ToString()));
        f.Add(new(s, "Frames", Frames.ToString()));
    }

    public override string Summary => $"ArtTimeCode · {TimeText} {Type.ToDisplayName()}" + (StreamId != 0 ? $" · stream {StreamId}" : "");
}

/// <summary>
/// ArtCommand (OpCommand 0x2400): text "Command=Data&amp;" property commands. Art-Net defined commands
/// (SwoutText, SwinText) are sent with EstaMan = 0xFFFF.
/// </summary>
public sealed class ArtCommandPacket : ArtNetPacket
{
    public const int HeaderSize = 16;
    public const int MinSize = 16;

    public override ArtNetOpCode OpCode => ArtNetOpCode.Command;
    public override int Size => HeaderSize + Data.Length;

    public ushort EstaManufacturer { get; set; } = ArtNetConstants.Global;

    private byte[] _data = [0];

    /// <summary>Raw null-terminated ASCII text (max 512 bytes).</summary>
    public byte[] Data
    {
        get => _data;
        set => _data = value is { Length: > ArtNetConstants.MaxTextLength } ? value[..ArtNetConstants.MaxTextLength] : value ?? [];
    }

    public string Text
    {
        get => Bin.Ascii(_data, 0, _data.Length);
        set => _data = Bin.NullTerminated(value);
    }

    /// <summary>"SwoutText=Playback&amp;" – relabels the ArtPollReply SwOut fields.</summary>
    public static ArtCommandPacket SwoutText(string label) => new() { Text = $"SwoutText={label}&" };

    /// <summary>"SwinText=Record&amp;" – relabels the ArtPollReply SwIn fields.</summary>
    public static ArtCommandPacket SwinText(string label) => new() { Text = $"SwinText={label}&" };

    /// <summary>Builds "A=1&amp;B=2&amp;".</summary>
    public static ArtCommandPacket FromCommands(IEnumerable<KeyValuePair<string, string>> commands, ushort esta = ArtNetConstants.Global) =>
        new() { EstaManufacturer = esta, Text = string.Concat(commands.Select(c => $"{c.Key}={c.Value}&")) };

    /// <summary>Parses the text into (command, data) pairs; command names are case insensitive.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ParseCommands()
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var part in Text.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            list.Add(eq < 0 ? new(part, string.Empty) : new(part[..eq].Trim(), part[(eq + 1)..]));
        }
        return list;
    }

    protected override void WriteBody(Span<byte> p)
    {
        Bin.U16BE(p, 12, EstaManufacturer);
        Bin.U16BE(p, 14, (ushort)_data.Length);
        _data.CopyTo(p[HeaderSize..]);
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        EstaManufacturer = Bin.U16BE(p, 12);
        Data = Bin.Bytes(p, HeaderSize, Math.Min((int)Bin.U16BE(p, 14), ArtNetConstants.MaxTextLength));
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "Command";
        f.Add(new(s, "ESTA Manufacturer", ArtNetText.FormatEsta(EstaManufacturer), Bin.HexWord(EstaManufacturer)));
        f.Add(new(s, "Text", Text));
        foreach (var c in ParseCommands())
            f.Add(new(s, c.Key, c.Value.Length == 0 ? "(no data)" : c.Value, ArtNetText.DescribeCommand(c.Key)));
    }

    public override string Summary => $"ArtCommand · {Text}";
}

/// <summary>
/// ArtTrigger (OpTrigger 0x9900): trigger macros. With OEM = 0xFFFF the Key is Table 7 (ASCII key, macro, soft key,
/// show); otherwise Key, SubKey and Data are manufacturer specific.
/// </summary>
public sealed class ArtTriggerPacket : ArtNetPacket
{
    public const int PayloadSize = 512;
    public const int FullSize = 18 + PayloadSize;
    public const int MinSize = 18;

    public override ArtNetOpCode OpCode => ArtNetOpCode.Trigger;
    public override int Size => FullSize;

    /// <summary>OEM code of nodes that shall accept this trigger (0xFFFF = Art-Net defined keys).</summary>
    public ushort Oem { get; set; } = ArtNetConstants.Global;

    public byte Key { get; set; }
    public byte SubKey { get; set; }

    /// <summary>Fixed 512-byte payload (interpretation defined by Key / OEM).</summary>
    public byte[] Data { get; private set; } = new byte[PayloadSize];

    public bool IsArtNetDefined => Oem == ArtNetConstants.Global;

    public ArtNetTriggerKey TriggerKey
    {
        get => (ArtNetTriggerKey)Key;
        set => Key = (byte)value;
    }

    public static ArtTriggerPacket Ascii(char c) => new() { Key = (byte)ArtNetTriggerKey.Ascii, SubKey = (byte)c };
    public static ArtTriggerPacket Macro(byte number) => new() { Key = (byte)ArtNetTriggerKey.Macro, SubKey = number };
    public static ArtTriggerPacket SoftKey(byte number) => new() { Key = (byte)ArtNetTriggerKey.Soft, SubKey = number };
    public static ArtTriggerPacket Show(byte number) => new() { Key = (byte)ArtNetTriggerKey.Show, SubKey = number };

    /// <summary>Copies <paramref name="payload"/> into the fixed data area (zero padded).</summary>
    public void SetData(ReadOnlySpan<byte> payload)
    {
        Array.Clear(Data);
        payload[..Math.Min(payload.Length, PayloadSize)].CopyTo(Data);
    }

    protected override void WriteBody(Span<byte> p)
    {
        Bin.U16BE(p, 14, Oem);
        p[16] = Key;
        p[17] = SubKey;
        Data.AsSpan(0, PayloadSize).CopyTo(p[18..]);
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        Oem = Bin.U16BE(p, 14);
        Key = Bin.U8(p, 16);
        SubKey = Bin.U8(p, 17);
        Data = Bin.FixedBytes(p, 18, PayloadSize);
    }

    public string Meaning => !IsArtNetDefined
        ? $"Manufacturer specific (OEM {Bin.HexWord(Oem)}) key {Key}, sub-key {SubKey}"
        : TriggerKey switch
        {
            ArtNetTriggerKey.Ascii => $"Key press '{(SubKey is >= 0x20 and < 0x7F ? ((char)SubKey).ToString() : Bin.HexByte(SubKey))}'",
            ArtNetTriggerKey.Macro => $"Run macro {SubKey}",
            ArtNetTriggerKey.Soft => $"Soft key {SubKey}",
            ArtNetTriggerKey.Show => $"Run show {SubKey}",
            _ => $"Undefined key {Key}, sub-key {SubKey}",
        };

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "Trigger";
        f.Add(new(s, "OEM", IsArtNetDefined ? "0xFFFF (Art-Net defined keys)" : Bin.HexWord(Oem)));
        f.Add(new(s, "Key", IsArtNetDefined && Enum.IsDefined(TriggerKey) ? TriggerKey.ToDisplayName() : Key.ToString(), Key.ToString()));
        f.Add(new(s, "Sub-Key", SubKey.ToString()));
        f.Add(new(s, "Meaning", Meaning));
        int used = Array.FindLastIndex(Data, b => b != 0) + 1;
        if (used > 0) f.Add(new(s, "Payload", Bin.Hex(Data.AsSpan(0, used), 64)));
    }

    public override string Summary => $"ArtTrigger · {Meaning}";
}
