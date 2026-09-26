using System.Text;

namespace ArtNet;

/// <summary>Human-readable dumps of packets and raw bytes.</summary>
public static class ArtNetFormatter
{
    /// <summary>Multi-line dump grouped by section.</summary>
    public static string Format(ArtNetPacket packet, bool includeHeader = true)
    {
        var sb = new StringBuilder();
        sb.Append(packet.Title).Append(" (").Append(packet.Size).AppendLine(" bytes)");
        string? section = null;
        foreach (var f in packet.Describe())
        {
            if (!includeHeader && f.Section == "Header") continue;
            if (f.Section != section)
            {
                section = f.Section;
                sb.Append("  ").AppendLine(section);
            }
            sb.Append("    ").Append(f.Name).Append(": ").Append(f.Value);
            if (f.Raw is not null) sb.Append("  [").Append(f.Raw).Append(']');
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>One-line summary.</summary>
    public static string Summarize(ArtNetPacket packet) => packet.Summary;

    /// <summary>Classic 16-bytes-per-line hex dump with ASCII column.</summary>
    public static string HexDump(ReadOnlySpan<byte> data, int bytesPerLine = 16)
    {
        var sb = new StringBuilder();
        for (int o = 0; o < data.Length; o += bytesPerLine)
        {
            var line = data.Slice(o, Math.Min(bytesPerLine, data.Length - o));
            sb.Append(o.ToString("X4")).Append("  ");
            for (int i = 0; i < bytesPerLine; i++)
            {
                sb.Append(i < line.Length ? line[i].ToString("X2") : "  ");
                sb.Append(i == bytesPerLine / 2 - 1 ? "  " : " ");
            }
            sb.Append(' ');
            foreach (byte b in line) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>DMX levels as rows of 16 channels: ("1-16", "000 255 …").</summary>
    public static IEnumerable<(string Label, string Values)> DmxLines(ReadOnlyMemory<byte> data, int perLine = 16, bool skipZeroLines = true)
    {
        var lines = new List<(string, string)>();
        var span = data.Span;
        for (int o = 0; o < span.Length; o += perLine)
        {
            var line = span.Slice(o, Math.Min(perLine, span.Length - o));
            if (skipZeroLines && line.IndexOfAnyExcept((byte)0) < 0) continue;
            var sb = new StringBuilder();
            for (int i = 0; i < line.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(line[i].ToString("D3")); }
            lines.Add(($"{o + 1}-{o + line.Length}", sb.ToString()));
        }
        return lines;
    }

    /// <summary>A DMX universe as a text grid with channel numbers (levels 0-255 or percent).</summary>
    public static string DmxGrid(ReadOnlySpan<byte> data, bool percent = false, int perLine = 16)
    {
        var sb = new StringBuilder();
        for (int o = 0; o < data.Length; o += perLine)
        {
            sb.Append((o + 1).ToString().PadLeft(3)).Append(" │");
            for (int i = o; i < Math.Min(o + perLine, data.Length); i++)
                sb.Append(' ').Append(percent ? ArtNetText.Percent(data[i]).PadLeft(3) : data[i].ToString().PadLeft(3));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
