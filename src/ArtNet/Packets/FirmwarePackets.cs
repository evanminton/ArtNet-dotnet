namespace ArtNet;

/// <summary>
/// ArtFirmwareMaster (OpFirmwareMaster 0xF200): one 512-word block of a firmware (.alf) or UBEA (.alu) upload,
/// unicast to a node which acknowledges every block with ArtFirmwareReply.
/// </summary>
public sealed class ArtFirmwareMasterPacket : ArtNetPacket
{
    public const int DataOffset = 40;
    public const int DataBytes = ArtNetConstants.FirmwareBlockWords * 2;
    public const int FullSize = DataOffset + DataBytes;
    public const int MinSize = DataOffset;

    public override ArtNetOpCode OpCode => ArtNetOpCode.FirmwareMaster;
    public override int Size => FullSize;

    public ArtNetFirmwareMasterType Type { get; set; }

    /// <summary>Consecutive block counter starting at 0 (wraps at 256).</summary>
    public byte BlockId { get; set; }

    /// <summary>Total number of 16-bit words of the upload including the file header (= file size in words).</summary>
    public uint FirmwareLength { get; set; }

    /// <summary>1024 bytes (512 words, hi byte first); the final block is null packed.</summary>
    public byte[] Data { get; private set; } = new byte[DataBytes];

    public bool IsUbea => Type is ArtNetFirmwareMasterType.UbeaFirst or ArtNetFirmwareMasterType.UbeaCont or ArtNetFirmwareMasterType.UbeaLast;
    public bool IsFirst => Type is ArtNetFirmwareMasterType.FirmFirst or ArtNetFirmwareMasterType.UbeaFirst;
    public bool IsLast => Type is ArtNetFirmwareMasterType.FirmLast or ArtNetFirmwareMasterType.UbeaLast;

    public void SetData(ReadOnlySpan<byte> block)
    {
        Array.Clear(Data);
        block[..Math.Min(block.Length, DataBytes)].CopyTo(Data);
    }

    protected override void WriteBody(Span<byte> p)
    {
        p[14] = (byte)Type;
        p[15] = BlockId;
        Bin.U32BE(p, 16, FirmwareLength);
        Data.AsSpan(0, DataBytes).CopyTo(p[DataOffset..]);
    }

    protected override void ReadBody(ReadOnlySpan<byte> p)
    {
        Type = (ArtNetFirmwareMasterType)Bin.U8(p, 14);
        BlockId = Bin.U8(p, 15);
        FirmwareLength = Bin.U32BE(p, 16);
        Data = Bin.FixedBytes(p, DataOffset, DataBytes);
    }

    protected override void DescribeBody(List<ArtNetField> f)
    {
        const string s = "Firmware";
        f.Add(new(s, "Type", Type.ToDisplayName(), Bin.HexByte((byte)Type)));
        f.Add(new(s, "Block", BlockId.ToString()));
        f.Add(new(s, "Total Length", $"{FirmwareLength} words ({FirmwareLength * 2L} bytes)"));
        f.Add(new(s, "Data", Bin.Hex(Data, 32)));
    }

    public override string Summary => $"ArtFirmwareMaster · {Type.ToDisplayName()} · block {BlockId} of {FirmwareLength} words";
}

/// <summary>ArtFirmwareReply (OpFirmwareReply 0xF300): a node's acknowledgement of each ArtFirmwareMaster block.</summary>
public sealed class ArtFirmwareReplyPacket : ArtNetPacket
{
    public const int FullSize = 36;
    public const int MinSize = 15;

    public override ArtNetOpCode OpCode => ArtNetOpCode.FirmwareReply;
    public override int Size => FullSize;

    public ArtNetFirmwareReplyType Type { get; set; }

    protected override void WriteBody(Span<byte> p) => p[14] = (byte)Type;

    protected override void ReadBody(ReadOnlySpan<byte> p) => Type = (ArtNetFirmwareReplyType)Bin.U8(p, 14);

    protected override void DescribeBody(List<ArtNetField> f) =>
        f.Add(new("Firmware", "Result", Type.ToDisplayName(), Bin.HexByte((byte)Type)));

    public override string Summary => $"ArtFirmwareReply · {Type.ToDisplayName()}";
}
