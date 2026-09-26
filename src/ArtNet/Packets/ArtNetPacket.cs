using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace ArtNet;

/// <summary>A single human-readable field of a decoded packet.</summary>
/// <param name="Section">Group, e.g. "Header", "Port 1", "Status".</param>
/// <param name="Name">Readable field name.</param>
/// <param name="Value">Readable value.</param>
/// <param name="Raw">Raw value when it differs from <paramref name="Value"/>.</param>
public sealed record ArtNetField(string Section, string Name, string Value, string? Raw = null)
{
    public override string ToString() => Raw is null ? $"{Name}: {Value}" : $"{Name}: {Value} [{Raw}]";
}

/// <summary>
/// Byte helpers. Art-Net transmits the OpCode and ArtPollReply → Port low byte first; every other
/// multi-byte field is Hi/Lo (big-endian). Reads beyond the buffer return 0 ("missing fields are assumed to be zero").
/// </summary>
internal static class Bin
{
    public static byte U8(ReadOnlySpan<byte> s, int o) => o < s.Length ? s[o] : (byte)0;
    public static ushort U16BE(ReadOnlySpan<byte> s, int o) => (ushort)((U8(s, o) << 8) | U8(s, o + 1));
    public static ushort U16LE(ReadOnlySpan<byte> s, int o) => (ushort)(U8(s, o) | (U8(s, o + 1) << 8));
    public static uint U32BE(ReadOnlySpan<byte> s, int o) => (uint)((U8(s, o) << 24) | (U8(s, o + 1) << 16) | (U8(s, o + 2) << 8) | U8(s, o + 3));

    public static void U16BE(Span<byte> s, int o, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(s[o..], v);
    public static void U16LE(Span<byte> s, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(s[o..], v);
    public static void U32BE(Span<byte> s, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(s[o..], v);

    public static byte[] Bytes(ReadOnlySpan<byte> s, int o, int len)
    {
        if (o >= s.Length || len <= 0) return [];
        return s.Slice(o, Math.Min(len, s.Length - o)).ToArray();
    }

    /// <summary>Copies <paramref name="len"/> bytes, zero filling what is missing.</summary>
    public static byte[] FixedBytes(ReadOnlySpan<byte> s, int o, int len)
    {
        var b = new byte[len];
        if (o < s.Length) s.Slice(o, Math.Min(len, s.Length - o)).CopyTo(b);
        return b;
    }

    /// <summary>IPv4 address, first byte most significant.</summary>
    public static IPAddress Ip(ReadOnlySpan<byte> s, int o) => new(FixedBytes(s, o, 4));

    public static void Ip(Span<byte> s, int o, IPAddress? ip)
    {
        var f = s.Slice(o, 4);
        f.Clear();
        if (ip is null) return;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        ip.TryWriteBytes(f, out _);
    }

    /// <summary>Reads a null-terminated ASCII field (Latin-1 so it never throws).</summary>
    public static string Ascii(ReadOnlySpan<byte> s, int o, int len)
    {
        if (o >= s.Length || len <= 0) return string.Empty;
        var f = s.Slice(o, Math.Min(len, s.Length - o));
        int end = f.IndexOf((byte)0);
        if (end >= 0) f = f[..end];
        return Encoding.Latin1.GetString(f);
    }

    /// <summary>
    /// Writes a null-terminated ASCII field: at most <paramref name="len"/>-1 characters so the null always fits;
    /// non-ASCII characters become '?'.
    /// </summary>
    public static void Ascii(Span<byte> s, int o, int len, string? value)
    {
        var f = s.Slice(o, len);
        f.Clear();
        if (string.IsNullOrEmpty(value)) return;
        int n = Math.Min(value.Length, len - 1);
        for (int i = 0; i < n; i++)
        {
            char c = value[i];
            f[i] = c is >= ' ' and <= '~' ? (byte)c : (byte)'?';
        }
    }

    /// <summary>ASCII bytes of <paramref name="value"/> plus a null terminator, limited to <paramref name="max"/> bytes in total.</summary>
    public static byte[] NullTerminated(string? value, int max = ArtNetConstants.MaxTextLength)
    {
        value ??= string.Empty;
        int n = Math.Min(value.Length, max - 1);
        var b = new byte[n + 1];
        Ascii(b, 0, n + 1, value);
        return b;
    }

    public static string Hex(ReadOnlySpan<byte> data, int max = 32)
    {
        if (data.IsEmpty) return "(empty)";
        var hex = Convert.ToHexString(data[..Math.Min(max, data.Length)]);
        var sb = new StringBuilder();
        for (int i = 0; i < hex.Length; i += 2) { if (i > 0) sb.Append(' '); sb.Append(hex, i, 2); }
        if (data.Length > max) sb.Append($" … ({data.Length} bytes)");
        return sb.ToString();
    }

    public static string Mac(ReadOnlySpan<byte> mac) => mac.Length < 6
        ? "00:00:00:00:00:00"
        : $"{mac[0]:X2}:{mac[1]:X2}:{mac[2]:X2}:{mac[3]:X2}:{mac[4]:X2}:{mac[5]:X2}";

    public static string HexWord(ushort v) => $"0x{v:X4}";
    public static string HexByte(byte v) => $"0x{v:X2}";
}

/// <summary>Base class for every Art-Net packet: ID[8] "Art-Net\0", OpCode (low byte first) and usually ProtVerHi/Lo.</summary>
public abstract class ArtNetPacket
{
    /// <summary>OpCode written at bytes 8-9.</summary>
    public abstract ArtNetOpCode OpCode { get; }

    /// <summary>False only for ArtPollReply, which has no protocol version field.</summary>
    public virtual bool HasProtocolVersion => true;

    /// <summary>ProtVerHi:ProtVerLo (current value 14).</summary>
    public ushort ProtocolVersion { get; set; } = ArtNetConstants.ProtocolVersion;

    /// <summary>Encoded size in bytes.</summary>
    public abstract int Size { get; }

    /// <summary>Human-readable packet title, e.g. "ArtPollReply".</summary>
    public virtual string Title => OpCode.ToPacketName();

    /// <summary>Encodes the packet into a new array.</summary>
    public byte[] ToArray()
    {
        var buffer = new byte[Size];
        Write(buffer);
        return buffer;
    }

    /// <summary>Encodes the packet into <paramref name="destination"/> and returns the number of bytes written.</summary>
    public int Write(Span<byte> destination)
    {
        int size = Size;
        if (destination.Length < size)
            throw new ArgumentException($"Destination too small: {Title} needs {size} bytes.", nameof(destination));
        var p = destination[..size];
        p.Clear();
        ArtNetConstants.Id.CopyTo(p);
        Bin.U16LE(p, 8, (ushort)OpCode);
        if (HasProtocolVersion) Bin.U16BE(p, 10, ProtocolVersion);
        WriteBody(p);
        return size;
    }

    internal void Read(ReadOnlySpan<byte> packet)
    {
        if (HasProtocolVersion) ProtocolVersion = Bin.U16BE(packet, 10);
        ReadBody(packet);
    }

    /// <summary>Writes the fields after the header. Offsets are absolute (from byte 0).</summary>
    protected abstract void WriteBody(Span<byte> p);

    /// <summary>Reads the fields after the header. Offsets are absolute (from byte 0).</summary>
    protected abstract void ReadBody(ReadOnlySpan<byte> p);

    /// <summary>Adds human-readable fields for the body.</summary>
    protected abstract void DescribeBody(List<ArtNetField> fields);

    /// <summary>All fields (header + body) in human-readable form.</summary>
    public IReadOnlyList<ArtNetField> Describe()
    {
        var list = new List<ArtNetField>
        {
            new("Header", "OpCode", OpCode.ToDisplayName(), Bin.HexWord((ushort)OpCode)),
        };
        if (HasProtocolVersion) list.Add(new("Header", "Protocol Version", ProtocolVersion.ToString()));
        DescribeBody(list);
        return list;
    }

    /// <summary>One-line summary (used by packet logs).</summary>
    public virtual string Summary => Title;

    /// <summary>Multi-line human-readable dump.</summary>
    public override string ToString() => ArtNetFormatter.Format(this);
}

/// <summary>Any OpCode without a dedicated class (media, video, file and directory packets): header plus raw bytes.</summary>
public sealed class ArtUnknownPacket : ArtNetPacket
{
    private ArtNetOpCode _opCode;

    public ArtUnknownPacket(ArtNetOpCode opCode = default) => _opCode = opCode;

    public override ArtNetOpCode OpCode => _opCode;

    /// <summary>Bytes after the OpCode (including the protocol version, when the packet has one).</summary>
    public byte[] Body { get; set; } = [];

    public override bool HasProtocolVersion => false;

    public override int Size => ArtNetConstants.IdAndOpCodeSize + Body.Length;

    public override string Title => Enum.IsDefined(_opCode) ? _opCode.ToPacketName() : $"Unknown OpCode {Bin.HexWord((ushort)_opCode)}";

    internal void SetOpCode(ArtNetOpCode op) => _opCode = op;

    protected override void WriteBody(Span<byte> p) => Body.CopyTo(p[ArtNetConstants.IdAndOpCodeSize..]);

    protected override void ReadBody(ReadOnlySpan<byte> p) => Body = Bin.Bytes(p, ArtNetConstants.IdAndOpCodeSize, p.Length);

    protected override void DescribeBody(List<ArtNetField> f)
    {
        f.Add(new("Body", "Description", _opCode.ToDescription()));
        f.Add(new("Body", "Length", $"{Body.Length} bytes"));
        f.Add(new("Body", "Data", Bin.Hex(Body, 64)));
    }

    public override string Summary => $"{Title} · {Body.Length} bytes";
}
