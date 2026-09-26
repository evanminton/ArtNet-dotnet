namespace ArtNet;

/// <summary>
/// ArtDmx (OpDmx / OpOutput 0x5000): zero start code DMX512 data for one universe. Unicast to subscribers only.
/// Length should be even, 2-512.
/// </summary>
public sealed class ArtDmxPacket : ArtNetPacket
{
    public const int HeaderSize = 18;
    public const int MinSize = 18;

    public override ArtNetOpCode OpCode => ArtNetOpCode.Dmx;
    public override int Size => HeaderSize + Data.Length;

    /// <summary>1-255 incrementing to allow re-sequencing; 0 disables the feature.</summary>
    public byte Sequence { get; set; }

    /// <summary>Physical input port the data came from (used for merging).</summary>
    public byte Physical { get; set; }

    public PortAddress PortAddress { get; set; } = new(1);

    private byte[] _data = new byte[ArtNetConstants.DmxChannels];

    /// <summary>Channel levels (index 0 = channel 1), 1-512 bytes. Odd lengths are padded to even when set.</summary>
    public byte[] Data
    {
        get => _data;
        set => _data = NormalizeLength(value);
    }

    /// <summary>Level of a 1-based DMX channel (0 if the packet is shorter).</summary>
    public byte this[int channel] => channel >= 1 && channel <= _data.Length ? _data[channel - 1] : (byte)0;

    /// <summary>Pads to an even length in 2-512 as the spec recommends (truncates above 512).</summary>
    public static byte[] NormalizeLength(byte[]? data)
    {
        data ??= [];
        int len = Math.Clamp(data.Length, 2, ArtNetConstants.DmxChannels);
        if ((len & 1) != 0) len++;
        if (len == data.Length) return data;
        var b = new byte[len];
        data.AsSpan(0, Math.Min(data.Length, len)).CopyTo(b);
        return b;
    }

    protected override void WriteBody(Span<byte> p)
    {
        p[12] = Sequence;
        p[13] = Physical;
        p[14] = PortAddress.SubUni;
        p[15] = PortAddress.Net;
        Bin.U16BE(p, 16, (ushort)_data.Length);
        _data.CopyTo(p[HeaderSize..]);
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        Sequence = Bin.U8(p, 12);
        Physical = Bin.U8(p, 13);
        PortAddress = PortAddress.FromBytes(Bin.U8(p, 15), Bin.U8(p, 14));
        int len = Math.Min((int)Bin.U16BE(p, 16), ArtNetConstants.DmxChannels);
        // Accept what actually arrived (truncated packets and odd lengths from lenient senders).
        _data = Bin.Bytes(p, HeaderSize, len);
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "DMX";
        f.Add(new(s, "Port-Address", PortAddress.ToString(), PortAddress.Value.ToString()));
        f.Add(new(s, "Net / Sub-Net / Universe", PortAddress.ToNetSubUniString()));
        f.Add(new(s, "Sequence", Sequence == 0 ? "0 (disabled)" : Sequence.ToString()));
        f.Add(new(s, "Physical Port", Physical.ToString()));
        f.Add(new(s, "Channels", _data.Length.ToString()));
        int active = _data.Count(b => b != 0);
        f.Add(new(s, "Non-zero Channels", active.ToString()));
        foreach (var line in ArtNetFormatter.DmxLines(_data))
            f.Add(new("Levels", line.Label, line.Values));
    }

    public override string Summary =>
        $"ArtDmx · universe {PortAddress} · seq {Sequence} · {_data.Length} ch · {_data.Count(b => b != 0)} non-zero";
}

/// <summary>
/// ArtSync (OpSync 0x5200): directed broadcast by a controller to output previously sent ArtDmx synchronously.
/// </summary>
public sealed class ArtSyncPacket : ArtNetPacket
{
    public const int FullSize = 14;
    public const int MinSize = 12;

    public override ArtNetOpCode OpCode => ArtNetOpCode.Sync;
    public override int Size => FullSize;

    protected override void WriteBody(Span<byte> p) { }
    protected override void ReadBody(ReadOnlySpan<byte> p) { }
    protected override void DescribeBody(List<ArtNetField> f) =>
        f.Add(new("Sync", "Action", "Output all buffered ArtDmx now"));

    public override string Summary => "ArtSync";
}

/// <summary>
/// ArtNzs (OpNzs 0x5100): DMX512 data with a non-zero start code (not RDM). Length 1-512.
/// </summary>
public class ArtNzsPacket : ArtNetPacket
{
    public const int HeaderSize = 18;
    public const int MinSize = 18;

    public override ArtNetOpCode OpCode => ArtNetOpCode.Nzs;
    public override int Size => HeaderSize + DataLength;

    public byte Sequence { get; set; }

    /// <summary>Alternate start code (must not be 0 or 0xCC).</summary>
    public virtual byte StartCode { get; set; } = 0x17;

    public PortAddress PortAddress { get; set; } = new(1);

    private byte[] _data = [0];

    /// <summary>Slot data, 1-512 bytes.</summary>
    public virtual byte[] Data
    {
        get => _data;
        set => _data = value is { Length: > ArtNetConstants.DmxChannels } ? value[..ArtNetConstants.DmxChannels] : value ?? [];
    }

    protected virtual int DataLength => Data.Length;

    protected virtual void WriteData(Span<byte> p) => Data.CopyTo(p);

    protected virtual void ReadData(ReadOnlySpan<byte> d) => Data = d.ToArray();

    protected override void WriteBody(Span<byte> p)
    {
        p[12] = Sequence;
        p[13] = StartCode;
        p[14] = PortAddress.SubUni;
        p[15] = PortAddress.Net;
        Bin.U16BE(p, 16, (ushort)DataLength);
        WriteData(p[HeaderSize..]);
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        Sequence = Bin.U8(p, 12);
        ReadStartCode(Bin.U8(p, 13));
        PortAddress = PortAddress.FromBytes(Bin.U8(p, 15), Bin.U8(p, 14));
        int len = Math.Min((int)Bin.U16BE(p, 16), ArtNetConstants.DmxChannels);
        ReadData(Bin.Bytes(p, HeaderSize, len));
    }

    protected virtual void ReadStartCode(byte code) => StartCode = code;

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "NZS";
        f.Add(new(s, "Port-Address", PortAddress.ToString(), PortAddress.Value.ToString()));
        f.Add(new(s, "Start Code", ArtNetText.FormatStartCode(StartCode), Bin.HexByte(StartCode)));
        f.Add(new(s, "Sequence", Sequence == 0 ? "0 (disabled)" : Sequence.ToString()));
        f.Add(new(s, "Length", DataLength.ToString()));
        f.Add(new(s, "Data", Bin.Hex(Data, 64)));
    }

    public override string Summary => $"ArtNzs · universe {PortAddress} · start code {Bin.HexByte(StartCode)} · {DataLength} slots";
}

/// <summary>
/// ArtVlc: an ArtNzs with start code 0x91 and magic numbers 0x41 0x4C 0x45 carrying Visible Light Communication data.
/// </summary>
public sealed class ArtVlcPacket : ArtNzsPacket
{
    /// <summary>Bytes of VLC header before the payload (Vlc[0..21]).</summary>
    public const int VlcHeaderSize = 22;

    public override string Title => "ArtVlc";

    public override byte StartCode
    {
        get => ArtNetConstants.VlcStartCode;
        set { /* fixed */ }
    }

    public ArtVlcFlags Flags { get; set; }

    /// <summary>0 = first packet of a transaction, 0xFFFF = final, others increment and roll over to 1 at 0xFFFE.</summary>
    public ushort Transaction { get; set; }

    /// <summary>Target slot 1-512; 0 = all devices on this Port-Address.</summary>
    public ushort SlotAddress { get; set; }

    /// <summary>Unsigned 16-bit additive checksum of the payload (computed when writing).</summary>
    public ushort PayloadChecksum { get; private set; }

    /// <summary>Modulation depth 1-100 %; 0 = transmitter default.</summary>
    public byte Depth { get; set; }

    /// <summary>Modulation frequency in Hz; 0 = default.</summary>
    public ushort Frequency { get; set; }

    /// <summary>Modulation type; 0 = default.</summary>
    public ushort Modulation { get; set; }

    public ArtVlcPayloadLanguage PayloadLanguage { get; set; }

    /// <summary>Beacon repeat frequency in Hz; 0 = default.</summary>
    public ushort BeaconRepeat { get; set; }

    private byte[] _payload = [];

    /// <summary>VLC payload, 0-480 bytes.</summary>
    public byte[] Payload
    {
        get => _payload;
        set => _payload = value is { Length: > ArtNetConstants.MaxVlcPayload } ? value[..ArtNetConstants.MaxVlcPayload] : value ?? [];
    }

    /// <summary>Payload as text (BeaconUrl / BeaconText).</summary>
    public string PayloadText
    {
        get => System.Text.Encoding.ASCII.GetString(_payload).TrimEnd('\0');
        set => Payload = System.Text.Encoding.ASCII.GetBytes(value ?? string.Empty);
    }

    /// <summary>Checksum as it should be for the current payload.</summary>
    public ushort ComputeChecksum()
    {
        ushort sum = 0;
        foreach (byte b in _payload) sum += b;
        return sum;
    }

    public bool ChecksumValid => PayloadChecksum == ComputeChecksum();

    /// <summary>True when an ArtNzs body carries the VLC magic numbers.</summary>
    public static bool IsVlc(ReadOnlySpan<byte> packet) =>
        packet.Length >= HeaderSize + 3 && packet[13] == ArtNetConstants.VlcStartCode &&
        packet[HeaderSize] == ArtNetConstants.VlcManIdHi && packet[HeaderSize + 1] == ArtNetConstants.VlcManIdLo &&
        packet[HeaderSize + 2] == ArtNetConstants.VlcSubCode;

    public override byte[] Data
    {
        get
        {
            var b = new byte[DataLength];
            WriteData(b);
            return b;
        }
        set => ReadData(value);
    }

    protected override int DataLength => VlcHeaderSize + _payload.Length;

    protected override void ReadStartCode(byte code) { }

    protected override void WriteData(Span<byte> d)
    {
        PayloadChecksum = ComputeChecksum();
        d[0] = ArtNetConstants.VlcManIdHi;
        d[1] = ArtNetConstants.VlcManIdLo;
        d[2] = ArtNetConstants.VlcSubCode;
        d[3] = (byte)Flags;
        Bin.U16BE(d, 4, Transaction);
        Bin.U16BE(d, 6, SlotAddress);
        Bin.U16BE(d, 8, (ushort)_payload.Length);
        Bin.U16BE(d, 10, PayloadChecksum);
        d[12] = 0;
        d[13] = Depth;
        Bin.U16BE(d, 14, Frequency);
        Bin.U16BE(d, 16, Modulation);
        Bin.U16BE(d, 18, (ushort)PayloadLanguage);
        Bin.U16BE(d, 20, BeaconRepeat);
        _payload.CopyTo(d[VlcHeaderSize..]);
    }

    protected override void ReadData(ReadOnlySpan<byte> d)
    {
        Flags = (ArtVlcFlags)Bin.U8(d, 3);
        Transaction = Bin.U16BE(d, 4);
        SlotAddress = Bin.U16BE(d, 6);
        int count = Math.Min((int)Bin.U16BE(d, 8), ArtNetConstants.MaxVlcPayload);
        PayloadChecksum = Bin.U16BE(d, 10);
        Depth = Bin.U8(d, 13);
        Frequency = Bin.U16BE(d, 14);
        Modulation = Bin.U16BE(d, 16);
        PayloadLanguage = (ArtVlcPayloadLanguage)Bin.U16BE(d, 18);
        BeaconRepeat = Bin.U16BE(d, 20);
        _payload = Bin.Bytes(d, VlcHeaderSize, count);
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "VLC";
        f.Add(new(s, "Port-Address", PortAddress.ToString(), PortAddress.Value.ToString()));
        f.Add(new(s, "Sequence", Sequence == 0 ? "0 (disabled)" : Sequence.ToString()));
        f.Add(new(s, "Flags", Flags.ToDisplayName(), Bin.HexByte((byte)Flags)));
        f.Add(new(s, "Payload Format", Flags.HasFlag(ArtVlcFlags.Ieee) ? "IEEE VLC data" : PayloadLanguage.ToDisplayName()));
        f.Add(new(s, "Transaction", Transaction switch { 0 => "0 (first)", 0xFFFF => "65535 (final)", _ => Transaction.ToString() }));
        f.Add(new(s, "Slot Address", SlotAddress == 0 ? "0 (all devices)" : SlotAddress.ToString()));
        f.Add(new(s, "Depth", Depth == 0 ? "Default" : $"{Depth} %"));
        f.Add(new(s, "Frequency", Frequency == 0 ? "Default" : $"{Frequency} Hz"));
        f.Add(new(s, "Modulation", Modulation == 0 ? "Default" : Modulation.ToString()));
        f.Add(new(s, "Beacon Repeat", BeaconRepeat == 0 ? "Default" : $"{BeaconRepeat} Hz"));
        f.Add(new(s, "Payload Checksum", ChecksumValid ? "Valid" : "Invalid", Bin.HexWord(PayloadChecksum)));
        f.Add(new(s, "Payload", PayloadLanguage is ArtVlcPayloadLanguage.BeaconUrl or ArtVlcPayloadLanguage.BeaconText && !Flags.HasFlag(ArtVlcFlags.Ieee)
            ? PayloadText
            : PayloadLanguage == ArtVlcPayloadLanguage.BeaconLocationId && _payload.Length >= 2
                ? $"Location {(_payload[0] << 8) | _payload[1]}"
                : Bin.Hex(_payload, 64)));
    }

    public override string Summary => $"ArtVlc · universe {PortAddress} · {PayloadLanguage.ToDisplayName()} · {_payload.Length} bytes";
}
