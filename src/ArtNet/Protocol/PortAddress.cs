using System.Globalization;

namespace ArtNet;

/// <summary>
/// 15-bit Art-Net Port-Address: bit 15 = 0, bits 14-8 = Net, bits 7-4 = Sub-Net, bits 3-0 = Universe.
/// The valid range is 1-32767; 0 is deprecated (sACN compatibility) but still representable.
/// </summary>
public readonly record struct PortAddress : IComparable<PortAddress>, ISpanFormattable
{
    private readonly ushort _value;

    /// <summary>Creates a Port-Address from its 15-bit value (bit 15 is ignored).</summary>
    public PortAddress(int value)
    {
        if (value is < 0 or > ArtNetConstants.MaxPortAddress)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Port-Address must be 0-32767.");
        _value = (ushort)value;
    }

    /// <summary>Creates a Port-Address from Net (0-127), Sub-Net (0-15) and Universe (0-15).</summary>
    public PortAddress(int net, int subNet, int universe)
    {
        if (net is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(net), net, "Net must be 0-127.");
        if (subNet is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(subNet), subNet, "Sub-Net must be 0-15.");
        if (universe is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(universe), universe, "Universe must be 0-15.");
        _value = (ushort)((net << 8) | (subNet << 4) | universe);
    }

    /// <summary>15-bit value 0-32767.</summary>
    public ushort Value => _value;

    /// <summary>Bits 14-8 (0-127).</summary>
    public byte Net => (byte)((_value >> 8) & 0x7F);

    /// <summary>Bits 7-4 (0-15).</summary>
    public byte SubNet => (byte)((_value >> 4) & 0x0F);

    /// <summary>Bits 3-0 (0-15).</summary>
    public byte Universe => (byte)(_value & 0x0F);

    /// <summary>Low byte (Sub-Net + Universe) as carried in ArtDmx → SubUni.</summary>
    public byte SubUni => (byte)(_value & 0xFF);

    /// <summary>Zero is deprecated in Art-Net 4.</summary>
    public bool IsDeprecatedZero => _value == 0;

    /// <summary>Kiloverse (group of 1024 universes) this address belongs to.</summary>
    public int Kiloverse => _value / 1024;

    /// <summary>Builds a Port-Address from the ArtDmx SubUni / Net bytes.</summary>
    public static PortAddress FromBytes(byte net, byte subUni) => new(((net & 0x7F) << 8) | subUni);

    /// <summary>Builds a Port-Address from ArtPollReply NetSwitch, SubSwitch and a SwIn/SwOut entry.</summary>
    public static PortAddress FromSwitches(byte netSwitch, byte subSwitch, byte sw) =>
        new(((netSwitch & 0x7F) << 8) | ((subSwitch & 0x0F) << 4) | (sw & 0x0F));

    public static implicit operator ushort(PortAddress a) => a._value;

    public static explicit operator PortAddress(int value) => new(value);

    public int CompareTo(PortAddress other) => _value.CompareTo(other._value);

    public static bool operator <(PortAddress a, PortAddress b) => a._value < b._value;
    public static bool operator >(PortAddress a, PortAddress b) => a._value > b._value;
    public static bool operator <=(PortAddress a, PortAddress b) => a._value <= b._value;
    public static bool operator >=(PortAddress a, PortAddress b) => a._value >= b._value;

    /// <summary>"Net:Sub:Universe", e.g. "0:1:5".</summary>
    public string ToNetSubUniString() => $"{Net}:{SubNet}:{Universe}";

    /// <summary>"21 (0:1:5)".</summary>
    public override string ToString() => $"{_value} ({Net}:{SubNet}:{Universe})";

    public string ToString(string? format, IFormatProvider? formatProvider) => format switch
    {
        null or "" or "G" => ToString(),
        "N" => ToNetSubUniString(),
        "D" => _value.ToString(formatProvider),
        "X" => "0x" + _value.ToString("X4", formatProvider),
        _ => throw new FormatException($"Unknown Port-Address format '{format}'. Use G, N, D or X."),
    };

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        string s = ToString(format.IsEmpty ? null : format.ToString(), provider);
        if (s.Length > destination.Length) { charsWritten = 0; return false; }
        s.AsSpan().CopyTo(destination);
        charsWritten = s.Length;
        return true;
    }

    /// <summary>
    /// Parses "21", "0x15", "0:1:5", "0.1.5" or "0/1/5" (Net:Sub-Net:Universe), or "1:5" (Sub-Net:Universe, Net 0).
    /// </summary>
    public static PortAddress Parse(string text) =>
        TryParse(text, out var a) ? a : throw new FormatException($"'{text}' is not a Port-Address. Use 0-32767, 0x0000-0x7FFF or Net:Sub:Universe.");

    public static bool TryParse(string? text, out PortAddress address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        var parts = t.Split([':', '.', '/'], StringSplitOptions.TrimEntries);
        if (parts.Length is 2 or 3)
        {
            var nums = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out nums[i])) return false;
            int net = parts.Length == 3 ? nums[0] : 0, sub = nums[^2], uni = nums[^1];
            if (net is < 0 or > 127 || sub is < 0 or > 15 || uni is < 0 or > 15) return false;
            address = new PortAddress(net, sub, uni);
            return true;
        }
        int value;
        bool ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        if (!ok || value is < 0 or > ArtNetConstants.MaxPortAddress) return false;
        address = new PortAddress(value);
        return true;
    }
}
