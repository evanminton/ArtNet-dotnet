namespace ArtNet;

/// <summary>
/// ArtTodRequest (OpTodRequest 0x8000): asks output gateways for their Table of Devices. Nodes must not treat it as
/// a request for full discovery. Up to 32 Port-Address low bytes, combined with <see cref="Net"/>.
/// </summary>
public sealed class ArtTodRequestPacket : ArtNetPacket
{
    public const int FullSize = 24 + ArtNetConstants.MaxTodRequestAddresses;
    public const int MinSize = 24;

    public override ArtNetOpCode OpCode => ArtNetOpCode.TodRequest;
    public override int Size => FullSize;

    /// <summary>Top 7 bits of the Port-Addresses.</summary>
    public byte Net { get; set; }

    public ArtNetTodRequestCommand Command { get; set; }

    /// <summary>Low bytes (Sub-Net + Universe) of the Port-Addresses that must respond; max 32.</summary>
    public List<byte> Addresses { get; private set; } = [];

    /// <summary>The Port-Addresses addressed by this request.</summary>
    public IEnumerable<PortAddress> PortAddresses => Addresses.Select(a => PortAddress.FromBytes(Net, a));

    /// <summary>Builds a request for one or more universes (all must share the same Net).</summary>
    public static ArtTodRequestPacket For(params PortAddress[] addresses)
    {
        if (addresses.Length == 0) throw new ArgumentException("At least one Port-Address is required.", nameof(addresses));
        if (addresses.Length > ArtNetConstants.MaxTodRequestAddresses) throw new ArgumentException("At most 32 Port-Addresses.", nameof(addresses));
        byte net = addresses[0].Net;
        if (addresses.Any(a => a.Net != net)) throw new ArgumentException("All Port-Addresses must share the same Net.", nameof(addresses));
        var p = new ArtTodRequestPacket { Net = net };
        p.Addresses.AddRange(addresses.Select(a => a.SubUni));
        return p;
    }

    protected override void WriteBody(Span<byte> p)
    {
        p[21] = (byte)(Net & 0x7F);
        p[22] = (byte)Command;
        int n = Math.Min(Addresses.Count, ArtNetConstants.MaxTodRequestAddresses);
        p[23] = (byte)n;
        for (int i = 0; i < n; i++) p[24 + i] = Addresses[i];
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        Net = (byte)(Bin.U8(p, 21) & 0x7F);
        Command = (ArtNetTodRequestCommand)Bin.U8(p, 22);
        int n = Math.Min((int)Bin.U8(p, 23), ArtNetConstants.MaxTodRequestAddresses);
        Addresses = Bin.FixedBytes(p, 24, n).ToList();
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "TOD Request";
        f.Add(new(s, "Command", Command.ToDisplayName(), Bin.HexByte((byte)Command)));
        f.Add(new(s, "Net", Net.ToString()));
        f.Add(new(s, "Port-Addresses", Addresses.Count == 0 ? "(none)" : string.Join(", ", PortAddresses)));
    }

    public override string Summary => $"ArtTodRequest · {string.Join(", ", PortAddresses.Select(a => a.Value))}";
}

/// <summary>
/// ArtTodData (OpTodData 0x8100): all or part of an output gateway's Table of Devices (max 200 UIDs per packet).
/// </summary>
public sealed class ArtTodDataPacket : ArtNetPacket
{
    public const int HeaderSize = 28;
    public const int MinSize = 28;

    public override ArtNetOpCode OpCode => ArtNetOpCode.TodData;
    public override int Size => HeaderSize + RdmUid.Size * Math.Min(Uids.Count, ArtNetConstants.MaxUidsPerTodData);

    public ArtNetRdmVersion RdmVersion { get; set; } = ArtNetRdmVersion.Standard;

    /// <summary>Physical port 1-4 (with BindIndex identifies the port: (BindIndex-1) × NumPorts + Port).</summary>
    public byte Port { get; set; } = 1;

    public byte BindIndex { get; set; } = 1;

    public PortAddress PortAddress { get; set; } = new(1);

    public ArtNetTodDataCommand CommandResponse { get; set; }

    /// <summary>Total number of RDM devices discovered on this universe.</summary>
    public ushort UidTotal { get; set; }

    /// <summary>Index of this packet when the table spans several packets.</summary>
    public byte BlockCount { get; set; }

    /// <summary>UIDs in this packet.</summary>
    public List<RdmUid> Uids { get; private set; } = [];

    /// <summary>Splits a full table into ArtTodData packets of up to 200 UIDs.</summary>
    public static IReadOnlyList<ArtTodDataPacket> Split(PortAddress address, IReadOnlyList<RdmUid> table, byte port = 1, byte bindIndex = 1)
    {
        var list = new List<ArtTodDataPacket>();
        int blocks = Math.Max(1, (table.Count + ArtNetConstants.MaxUidsPerTodData - 1) / ArtNetConstants.MaxUidsPerTodData);
        // UidTotal is 16 bits and BlockCount 8 bits: larger tables cannot be described without wrapping.
        if (table.Count > ushort.MaxValue || blocks > byte.MaxValue + 1)
            throw new ArgumentOutOfRangeException(nameof(table), table.Count, $"A TOD holds at most {Math.Min((int)ushort.MaxValue, (byte.MaxValue + 1) * ArtNetConstants.MaxUidsPerTodData)} UIDs.");
        for (int b = 0; b < blocks; b++)
        {
            var p = new ArtTodDataPacket
            {
                PortAddress = address, Port = port, BindIndex = bindIndex,
                UidTotal = (ushort)table.Count, BlockCount = (byte)b,
            };
            p.Uids.AddRange(table.Skip(b * ArtNetConstants.MaxUidsPerTodData).Take(ArtNetConstants.MaxUidsPerTodData));
            list.Add(p);
        }
        return list;
    }

    protected override void WriteBody(Span<byte> p)
    {
        p[12] = (byte)RdmVersion;
        p[13] = Port;
        p[20] = BindIndex;
        p[21] = PortAddress.Net;
        p[22] = (byte)CommandResponse;
        p[23] = PortAddress.SubUni;
        Bin.U16BE(p, 24, UidTotal);
        p[26] = BlockCount;
        int n = Math.Min(Uids.Count, ArtNetConstants.MaxUidsPerTodData);
        p[27] = (byte)n;
        for (int i = 0; i < n; i++) Uids[i].Write(p.Slice(HeaderSize + i * RdmUid.Size, RdmUid.Size));
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        RdmVersion = (ArtNetRdmVersion)Bin.U8(p, 12);
        Port = Bin.U8(p, 13);
        BindIndex = Bin.U8(p, 20);
        PortAddress = PortAddress.FromBytes(Bin.U8(p, 21), Bin.U8(p, 23));
        CommandResponse = (ArtNetTodDataCommand)Bin.U8(p, 22);
        UidTotal = Bin.U16BE(p, 24);
        BlockCount = Bin.U8(p, 26);
        int n = Bin.U8(p, 27);
        Uids = [];
        for (int i = 0; i < n && HeaderSize + (i + 1) * RdmUid.Size <= p.Length; i++)
            Uids.Add(RdmUid.Read(p.Slice(HeaderSize + i * RdmUid.Size, RdmUid.Size)));
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "TOD";
        f.Add(new(s, "Port-Address", PortAddress.ToString(), PortAddress.Value.ToString()));
        f.Add(new(s, "Response", CommandResponse.ToDisplayName(), Bin.HexByte((byte)CommandResponse)));
        f.Add(new(s, "RDM Version", RdmVersion.ToDisplayName()));
        f.Add(new(s, "Physical Port", Port.ToString()));
        f.Add(new(s, "Bind Index", BindIndex.ToString()));
        f.Add(new(s, "Total Devices", UidTotal.ToString()));
        f.Add(new(s, "Block", BlockCount.ToString()));
        f.Add(new(s, "UIDs In Packet", Uids.Count.ToString()));
        for (int i = 0; i < Uids.Count; i++)
            f.Add(new("Devices", $"UID {BlockCount * ArtNetConstants.MaxUidsPerTodData + i + 1}", Uids[i].ToString()));
    }

    public override string Summary => $"ArtTodData · universe {PortAddress} · {CommandResponse.ToDisplayName()} · {Uids.Count}/{UidTotal} UIDs";
}

/// <summary>ArtTodControl (OpTodControl 0x8200): RDM discovery control for one output port. Answered with ArtTodData.</summary>
public sealed class ArtTodControlPacket : ArtNetPacket
{
    public const int FullSize = 24;
    public const int MinSize = 24;

    public override ArtNetOpCode OpCode => ArtNetOpCode.TodControl;
    public override int Size => FullSize;

    public PortAddress PortAddress { get; set; } = new(1);
    public ArtNetTodControlCommand Command { get; set; }

    protected override void WriteBody(Span<byte> p)
    {
        p[21] = PortAddress.Net;
        p[22] = (byte)Command;
        p[23] = PortAddress.SubUni;
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        PortAddress = PortAddress.FromBytes(Bin.U8(p, 21), Bin.U8(p, 23));
        Command = (ArtNetTodControlCommand)Bin.U8(p, 22);
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "TOD Control";
        f.Add(new(s, "Port-Address", PortAddress.ToString(), PortAddress.Value.ToString()));
        f.Add(new(s, "Command", Command.ToDisplayName(), Bin.HexByte((byte)Command)));
        f.Add(new(s, "Action", Command.ToDescription()));
    }

    public override string Summary => $"ArtTodControl · universe {PortAddress} · {Command.ToDisplayName()}";
}

/// <summary>ArtRdm (OpRdm 0x8300): one non-discovery RDM message (without the 0xCC start code). Unicast only.</summary>
public sealed class ArtRdmPacket : ArtNetPacket
{
    public const int HeaderSize = 24;
    public const int MinSize = 24;

    public override ArtNetOpCode OpCode => ArtNetOpCode.Rdm;
    public override int Size => HeaderSize + RdmPacket.Length;

    public ArtNetRdmVersion RdmVersion { get; set; } = ArtNetRdmVersion.Standard;

    /// <summary>Free entries in the gateway's RDM transmit queue (0 = not implemented).</summary>
    public byte FifoAvailable { get; set; }

    /// <summary>Size of the gateway's RDM transmit queue (0 = not implemented).</summary>
    public byte FifoMax { get; set; }

    public PortAddress PortAddress { get; set; } = new(1);
    public ArtNetRdmCommand Command { get; set; }

    /// <summary>RDM message excluding the DMX start code (starts with sub-start code 0x01).</summary>
    public byte[] RdmPacket { get; set; } = [];

    /// <summary>Decoded view of <see cref="RdmPacket"/> (null if too short).</summary>
    public RdmMessage? Message => RdmMessage.TryParse(RdmPacket, out var m) ? m : null;

    protected override void WriteBody(Span<byte> p)
    {
        p[12] = (byte)RdmVersion;
        p[19] = FifoAvailable;
        p[20] = FifoMax;
        p[21] = PortAddress.Net;
        p[22] = (byte)Command;
        p[23] = PortAddress.SubUni;
        RdmPacket.CopyTo(p[HeaderSize..]);
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        RdmVersion = (ArtNetRdmVersion)Bin.U8(p, 12);
        FifoAvailable = Bin.U8(p, 19);
        FifoMax = Bin.U8(p, 20);
        PortAddress = PortAddress.FromBytes(Bin.U8(p, 21), Bin.U8(p, 23));
        Command = (ArtNetRdmCommand)Bin.U8(p, 22);
        RdmPacket = Bin.Bytes(p, HeaderSize, p.Length - HeaderSize);
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "RDM";
        f.Add(new(s, "Port-Address", PortAddress.ToString(), PortAddress.Value.ToString()));
        f.Add(new(s, "Command", Command.ToDisplayName(), Bin.HexByte((byte)Command)));
        f.Add(new(s, "RDM Version", RdmVersion.ToDisplayName()));
        f.Add(new(s, "FIFO", FifoMax == 0 ? "Not implemented" : $"{FifoAvailable} of {FifoMax} free"));
        f.Add(new(s, "Length", $"{RdmPacket.Length} bytes"));
        if (Message is { } m) m.Describe(f);
        else f.Add(new(s, "Data", Bin.Hex(RdmPacket, 64)));
    }

    public override string Summary => Message is { } m
        ? $"ArtRdm · universe {PortAddress} · {m.Summary}"
        : $"ArtRdm · universe {PortAddress} · {RdmPacket.Length} bytes";
}

/// <summary>
/// ArtRdmSub (OpRdmSub 0x8400): compressed Get / Set / GetResponse / SetResponse data for many sub-devices at once.
/// All multi-byte fields are big-endian.
/// </summary>
public sealed class ArtRdmSubPacket : ArtNetPacket
{
    public const int HeaderSize = 32;
    public const int MinSize = 32;

    public override ArtNetOpCode OpCode => ArtNetOpCode.RdmSub;
    public override int Size => HeaderSize + 2 * Values.Count;

    public ArtNetRdmVersion RdmVersion { get; set; } = ArtNetRdmVersion.Standard;
    public RdmUid Uid { get; set; }
    public RdmCommandClass CommandClass { get; set; } = RdmCommandClass.Get;
    public ushort ParameterId { get; set; }

    /// <summary>First sub-device (0 = root, 1 = first sub-device).</summary>
    public ushort SubDevice { get; set; }

    /// <summary>Number of sub-devices packed (0 is illegal).</summary>
    public ushort SubCount { get; set; } = 1;

    /// <summary>Packed 16-bit data: SubCount entries for Set / GetResponse, none for Get / SetResponse.</summary>
    public List<ushort> Values { get; private set; } = [];

    /// <summary>Expected number of data entries for the command class.</summary>
    public int ExpectedValueCount => CommandClass is RdmCommandClass.Set or RdmCommandClass.GetResponse ? SubCount : 0;

    protected override void WriteBody(Span<byte> p)
    {
        p[12] = (byte)RdmVersion;
        Uid.Write(p[14..]);
        p[21] = (byte)CommandClass;
        Bin.U16BE(p, 22, ParameterId);
        Bin.U16BE(p, 24, SubDevice);
        Bin.U16BE(p, 26, SubCount);
        for (int i = 0; i < Values.Count; i++) Bin.U16BE(p, HeaderSize + 2 * i, Values[i]);
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        RdmVersion = (ArtNetRdmVersion)Bin.U8(p, 12);
        Uid = RdmUid.Read(Bin.FixedBytes(p, 14, 6));
        CommandClass = (RdmCommandClass)Bin.U8(p, 21);
        ParameterId = Bin.U16BE(p, 22);
        SubDevice = Bin.U16BE(p, 24);
        SubCount = Bin.U16BE(p, 26);
        Values = [];
        for (int o = HeaderSize; o + 2 <= p.Length; o += 2) Values.Add(Bin.U16BE(p, o));
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "RDM Sub-Device";
        f.Add(new(s, "UID", Uid.ToString()));
        f.Add(new(s, "Command Class", CommandClass.ToDisplayName(), Bin.HexByte((byte)CommandClass)));
        f.Add(new(s, "Parameter", RdmText.ParameterName(ParameterId), Bin.HexWord(ParameterId)));
        f.Add(new(s, "First Sub-Device", SubDevice == 0 ? "0 (root)" : SubDevice.ToString()));
        f.Add(new(s, "Sub-Device Count", SubCount.ToString()));
        f.Add(new(s, "RDM Version", RdmVersion.ToDisplayName()));
        for (int i = 0; i < Values.Count; i++)
            f.Add(new("Values", $"Sub-device {SubDevice + i}", Values[i].ToString(), Bin.HexWord(Values[i])));
    }

    public override string Summary => $"ArtRdmSub · {Uid} · {CommandClass.ToDisplayName()} {RdmText.ParameterName(ParameterId)} · {SubCount} sub-devices";
}
