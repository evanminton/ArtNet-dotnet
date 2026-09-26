using System.Globalization;

namespace ArtNet;

/// <summary>48-bit RDM unique ID: 16-bit ESTA manufacturer ID + 32-bit device ID, big-endian on the wire.</summary>
public readonly record struct RdmUid(ushort ManufacturerId, uint DeviceId) : IComparable<RdmUid>
{
    public const int Size = 6;

    /// <summary>Broadcast to all devices (FFFF:FFFFFFFF).</summary>
    public static RdmUid Broadcast => new(0xFFFF, 0xFFFFFFFF);

    /// <summary>Broadcast to all devices of one manufacturer.</summary>
    public static RdmUid ManufacturerBroadcast(ushort manufacturerId) => new(manufacturerId, 0xFFFFFFFF);

    /// <summary>48-bit value.</summary>
    public ulong Value => ((ulong)ManufacturerId << 32) | DeviceId;

    public bool IsBroadcast => DeviceId == 0xFFFFFFFF;

    public static RdmUid Read(ReadOnlySpan<byte> s) => s.Length < Size
        ? default
        : new((ushort)((s[0] << 8) | s[1]), (uint)((s[2] << 24) | (s[3] << 16) | (s[4] << 8) | s[5]));

    public void Write(Span<byte> s)
    {
        s[0] = (byte)(ManufacturerId >> 8);
        s[1] = (byte)ManufacturerId;
        s[2] = (byte)(DeviceId >> 24);
        s[3] = (byte)(DeviceId >> 16);
        s[4] = (byte)(DeviceId >> 8);
        s[5] = (byte)DeviceId;
    }

    public byte[] ToArray()
    {
        var b = new byte[Size];
        Write(b);
        return b;
    }

    public int CompareTo(RdmUid other) => Value.CompareTo(other.Value);

    /// <summary>"MMMM:DDDDDDDD" in hex.</summary>
    public override string ToString() => $"{ManufacturerId:X4}:{DeviceId:X8}";

    /// <summary>Parses "MMMM:DDDDDDDD" or 12 hex digits.</summary>
    public static RdmUid Parse(string text) =>
        TryParse(text, out var uid) ? uid : throw new FormatException($"'{text}' is not an RDM UID (MMMM:DDDDDDDD).");

    public static bool TryParse(string? text, out RdmUid uid)
    {
        uid = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hex = text.Trim().Replace(":", "").Replace("-", "").Replace(" ", "");
        if (hex.Length != 12 || !ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        uid = new RdmUid((ushort)(v >> 32), (uint)v);
        return true;
    }
}
