namespace ArtNet;

/// <summary>
/// Art-Net firmware (.alf) / UBEA (.alu) upload file: a 1060-byte (530-word) header followed by 16-bit big-endian
/// data words. Header: checksum, firmware version, 30-byte user name, 256 valid OEM codes, 255 spare words, length.
/// </summary>
public sealed class ArtNetFirmwareFile
{
    public const int HeaderSize = 1060;
    public const int HeaderWords = HeaderSize / 2;
    public const int UserNameSize = 30;
    public const int OemCount = 256;

    /// <summary>16-bit one's-complement checksum of the data area as stored in the file.</summary>
    public ushort Checksum { get; set; }

    /// <summary>Firmware revision; higher is newer.</summary>
    public ushort FirmwareVersion { get; set; }

    /// <summary>Human-readable description (max 29 characters + null).</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>OEM codes this file is valid for (unused entries are 0x0000 and omitted here).</summary>
    public List<ushort> OemCodes { get; } = [];

    /// <summary>Firmware data (even length; big-endian words, manufacturer specific).</summary>
    public byte[] Data { get; set; } = [];

    /// <summary>Length field: words of firmware data following the header.</summary>
    public uint DataWords => (uint)((Data.Length + 1) / 2);

    /// <summary>Total length in words including the header (what ArtFirmwareMaster → FirmwareLength carries).</summary>
    public uint TotalWords => HeaderWords + DataWords;

    public bool ChecksumValid => Checksum == ComputeChecksum(Data);

    /// <summary>True when the file lists <paramref name="oem"/>; controllers must check this before sending.</summary>
    public bool SupportsOem(ushort oem) => OemCodes.Contains(oem);

    /// <summary>
    /// 16-bit one's-complement checksum (end-around carry sum of big-endian words, then inverted).
    /// </summary>
    public static ushort ComputeChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 2)
        {
            uint word = (uint)(data[i] << 8) | (i + 1 < data.Length ? data[i + 1] : 0u);
            sum += word;
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return (ushort)~sum;
    }

    /// <summary>Reads an .alf / .alu file.</summary>
    public static ArtNetFirmwareFile Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderSize) throw new FormatException($"Firmware file too short: {file.Length} bytes, header is {HeaderSize}.");
        var f = new ArtNetFirmwareFile
        {
            Checksum = Bin.U16BE(file, 0),
            FirmwareVersion = Bin.U16BE(file, 2),
            UserName = Bin.Ascii(file, 4, UserNameSize),
        };
        for (int i = 0; i < OemCount; i++)
        {
            ushort oem = Bin.U16BE(file, 34 + 2 * i);
            if (oem != 0) f.OemCodes.Add(oem);
        }
        uint words = Bin.U32BE(file, 1056);
        long bytes = Math.Min((long)words * 2, file.Length - HeaderSize);
        f.Data = file.Slice(HeaderSize, (int)bytes).ToArray();
        return f;
    }

    public static ArtNetFirmwareFile Load(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Encodes the file (recomputes the checksum when <paramref name="updateChecksum"/> is true).</summary>
    public byte[] ToArray(bool updateChecksum = true)
    {
        if (OemCodes.Count > OemCount)
            throw new InvalidOperationException($"A firmware file lists at most {OemCount} OEM codes; this one has {OemCodes.Count}.");
        if (updateChecksum) Checksum = ComputeChecksum(Data);
        int dataBytes = (int)DataWords * 2;
        var b = new byte[HeaderSize + dataBytes];
        Bin.U16BE(b, 0, Checksum);
        Bin.U16BE(b, 2, FirmwareVersion);
        Bin.Ascii(b, 4, UserNameSize, UserName);
        for (int i = 0; i < OemCodes.Count; i++) Bin.U16BE(b, 34 + 2 * i, OemCodes[i]);
        Bin.U32BE(b, 1056, DataWords);
        Data.CopyTo(b, HeaderSize);
        return b;
    }

    /// <summary>
    /// Splits the complete file (header + data) into ArtFirmwareMaster packets of 512 words.
    /// </summary>
    public IReadOnlyList<ArtFirmwareMasterPacket> ToPackets(bool ubea = false)
    {
        var bytes = ToArray(updateChecksum: false);
        int blocks = (bytes.Length + ArtFirmwareMasterPacket.DataBytes - 1) / ArtFirmwareMasterPacket.DataBytes;
        uint total = (uint)((bytes.Length + 1) / 2);
        var list = new List<ArtFirmwareMasterPacket>(blocks);
        for (int i = 0; i < blocks; i++)
        {
            var type = i == 0 ? (ubea ? ArtNetFirmwareMasterType.UbeaFirst : ArtNetFirmwareMasterType.FirmFirst)
                : i == blocks - 1 ? (ubea ? ArtNetFirmwareMasterType.UbeaLast : ArtNetFirmwareMasterType.FirmLast)
                : (ubea ? ArtNetFirmwareMasterType.UbeaCont : ArtNetFirmwareMasterType.FirmCont);
            var p = new ArtFirmwareMasterPacket { Type = type, BlockId = (byte)i, FirmwareLength = total };
            int start = i * ArtFirmwareMasterPacket.DataBytes;
            p.SetData(bytes.AsSpan(start, Math.Min(ArtFirmwareMasterPacket.DataBytes, bytes.Length - start)));
            list.Add(p);
        }
        return list;
    }

    public override string ToString() =>
        $"\"{UserName}\" v{ArtNetText.FormatVersion(FirmwareVersion)} · {Data.Length} bytes · OEM {string.Join(", ", OemCodes.Select(o => $"0x{o:X4}"))} · checksum {(ChecksumValid ? "OK" : "BAD")}";
}
