using System.Diagnostics;
using ArtNet.Networking;
using System.Text;

namespace ArtNet.Desktop.Services;

/// <summary>Window size and position, remembered between runs.</summary>
public static class WindowPlacement
{
    public const double DefaultWidth = 1280, DefaultHeight = 820, MinWidth = 960, MinHeight = 600;

    public static void Restore(Window window)
    {
        var p = Preferences.Default;
        window.MinimumWidth = MinWidth;
        window.MinimumHeight = MinHeight;
        window.Width = Math.Max(MinWidth, p.Get("Window.Width", DefaultWidth));
        window.Height = Math.Max(MinHeight, p.Get("Window.Height", DefaultHeight));
        double x = p.Get("Window.X", double.NaN), y = p.Get("Window.Y", double.NaN);
        // Ignore positions that are far off-screen (monitor unplugged since the last run).
        var display = DeviceDisplay.Current.MainDisplayInfo;
        double screenW = display.Width / Math.Max(1, display.Density), screenH = display.Height / Math.Max(1, display.Density);
        if (!double.IsNaN(x) && !double.IsNaN(y) && x > -window.Width + 80 && y >= 0 && x < screenW * 3 - 80 && y < screenH * 3 - 80)
        {
            window.X = x;
            window.Y = y;
        }
    }

    public static void Save(Window window)
    {
        if (window.Width <= 0 || window.Height <= 0) return;
        var p = Preferences.Default;
        p.Set("Window.Width", window.Width);
        p.Set("Window.Height", window.Height);
        p.Set("Window.X", window.X);
        p.Set("Window.Y", window.Y);
    }
}

/// <summary>Desktop helpers: log export, folders, about text.</summary>
public static class DesktopActions
{
    /// <summary>Documents\Art-Net Desktop – exports land here.</summary>
    public static string ExportFolder
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Art-Net Desktop");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Writes the visible packet log (newest first) with every decoded field and opens the file.</summary>
    public static async Task<string> ExportPacketLogAsync(ArtNetService service)
    {
        var entries = service.PacketLog.ToArray();
        if (entries.Length == 0) throw new InvalidOperationException("The packet log is empty.");
        var sb = new StringBuilder();
        sb.AppendLine($"Art-Net Desktop packet log – exported {DateTime.Now:yyyy-MM-dd HH:mm:ss} – {entries.Length} packet(s)");
        sb.AppendLine();
        foreach (var e in entries)
        {
            sb.AppendLine($"=== {e.TimeText}  from {e.Source}  {e.TypeText}");
            sb.AppendLine(e.Details);
            sb.AppendLine("Raw:");
            sb.AppendLine(ArtNetFormatter.HexDump(e.Packet.ToArray()));
            sb.AppendLine();
        }
        var path = Path.Combine(ExportFolder, $"packets-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(path, sb.ToString());
        return path;
    }

    /// <summary>Writes a snapshot of every discovered node's ArtPollReply in readable form.</summary>
    public static async Task<string> ExportNodesAsync(ArtNetService service)
    {
        var nodes = service.Nodes.ToArray();
        if (nodes.Length == 0) throw new InvalidOperationException("No nodes discovered yet.");
        var sb = new StringBuilder();
        sb.AppendLine($"Art-Net Desktop node list – exported {DateTime.Now:yyyy-MM-dd HH:mm:ss} – {nodes.Length} node(s)");
        foreach (var n in nodes)
        {
            sb.AppendLine();
            sb.AppendLine($"=== {n.DisplayName}  {n.AddressText}");
            string? section = null;
            foreach (var f in n.Reply.Describe())
            {
                if (f.Section != section) { section = f.Section; sb.AppendLine($"[{section}]"); }
                sb.AppendLine($"  {f.Name,-28} {f.Value}{(f.Raw is null ? "" : $"   [{f.Raw}]")}");
            }
        }
        var path = Path.Combine(ExportFolder, $"nodes-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(path, sb.ToString());
        return path;
    }

    public static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    public static void OpenFile(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public static string AboutText =>
        $"Art-Net Desktop {AppInfo.Current.VersionString}{Environment.NewLine}" +
        $"ArtNet library {typeof(ArtNetNode).Assembly.GetName().Version}{Environment.NewLine}" +
        $"Art-Net 4 (protocol 14), UDP port 6454{Environment.NewLine}{Environment.NewLine}" +
        $"Settings: {FileSystem.AppDataDirectory}{Environment.NewLine}" +
        $"Exports: {ExportFolder}{Environment.NewLine}{Environment.NewLine}" +
        "Art-Net™ Designed by and Copyright Artistic Licence.";

    public const string FirewallHelp =
        "Art-Net uses UDP port 6454. If no devices appear:\n\n" +
        "• Allow Art-Net Desktop through Windows Defender Firewall (Private networks, and Public if your lighting network is classified as Public), " +
        "or add an inbound rule for UDP 6454.\n" +
        "• Pick the lighting network adapter in Settings → Interface. Art-Net's native ranges are 2.x.x.x and 10.x.x.x with mask 255.0.0.0.\n" +
        "• Other Art-Net software can share port 6454, but only one application receives unicast packets on some systems.";
}
