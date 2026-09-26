namespace ArtNet.Maui.Services;

/// <summary>User settings persisted with MAUI Preferences.</summary>
public sealed class AppSettings
{
    public string ShortName { get; set; } = "Art-Net Monitor";
    public string LongName { get; set; } = "Art-Net Monitor (ArtNet.NET MAUI app)";
    public ArtNetStyle Style { get; set; } = ArtNetStyle.Config;
    /// <summary>Local IPv4 address of the chosen interface; empty = automatic.</summary>
    public string InterfaceAddress { get; set; } = string.Empty;
    /// <summary>Explicit broadcast address; empty = from the interface.</summary>
    public string BroadcastAddress { get; set; } = string.Empty;
    public bool SendPolls { get; set; } = true;
    public int PollIntervalMs { get; set; } = 2500;
    /// <summary>Output ports announced (comma separated Port-Addresses) – compliant controllers unicast these to us.</summary>
    public string OutputUniverses { get; set; } = string.Empty;
    /// <summary>Input ports announced (comma separated Port-Addresses).</summary>
    public string InputUniverses { get; set; } = string.Empty;
    public ArtNetMergeMode MergeMode { get; set; } = ArtNetMergeMode.Htp;
    public bool BroadcastDmxWithoutSubscribers { get; set; }
    public bool EnableSync { get; set; } = true;
    public bool AcceptRemoteProgramming { get; set; } = true;
    public int KeepAliveMs { get; set; } = 900;
    public string ProductUrl { get; set; } = string.Empty;
    public bool AutoStart { get; set; } = true;

    public static AppSettings Load()
    {
        var p = Preferences.Default;
        var d = new AppSettings();
        return new AppSettings
        {
            ShortName = p.Get(nameof(ShortName), d.ShortName),
            LongName = p.Get(nameof(LongName), d.LongName),
            Style = (ArtNetStyle)p.Get(nameof(Style), (int)d.Style),
            InterfaceAddress = p.Get(nameof(InterfaceAddress), d.InterfaceAddress),
            BroadcastAddress = p.Get(nameof(BroadcastAddress), d.BroadcastAddress),
            SendPolls = p.Get(nameof(SendPolls), d.SendPolls),
            PollIntervalMs = p.Get(nameof(PollIntervalMs), d.PollIntervalMs),
            OutputUniverses = p.Get(nameof(OutputUniverses), d.OutputUniverses),
            InputUniverses = p.Get(nameof(InputUniverses), d.InputUniverses),
            MergeMode = (ArtNetMergeMode)p.Get(nameof(MergeMode), (int)d.MergeMode),
            BroadcastDmxWithoutSubscribers = p.Get(nameof(BroadcastDmxWithoutSubscribers), d.BroadcastDmxWithoutSubscribers),
            EnableSync = p.Get(nameof(EnableSync), d.EnableSync),
            AcceptRemoteProgramming = p.Get(nameof(AcceptRemoteProgramming), d.AcceptRemoteProgramming),
            KeepAliveMs = p.Get(nameof(KeepAliveMs), d.KeepAliveMs),
            ProductUrl = p.Get(nameof(ProductUrl), d.ProductUrl),
            AutoStart = p.Get(nameof(AutoStart), d.AutoStart),
        };
    }

    public void Save()
    {
        var p = Preferences.Default;
        p.Set(nameof(ShortName), ShortName);
        p.Set(nameof(LongName), LongName);
        p.Set(nameof(Style), (int)Style);
        p.Set(nameof(InterfaceAddress), InterfaceAddress);
        p.Set(nameof(BroadcastAddress), BroadcastAddress);
        p.Set(nameof(SendPolls), SendPolls);
        p.Set(nameof(PollIntervalMs), PollIntervalMs);
        p.Set(nameof(OutputUniverses), OutputUniverses);
        p.Set(nameof(InputUniverses), InputUniverses);
        p.Set(nameof(MergeMode), (int)MergeMode);
        p.Set(nameof(BroadcastDmxWithoutSubscribers), BroadcastDmxWithoutSubscribers);
        p.Set(nameof(EnableSync), EnableSync);
        p.Set(nameof(AcceptRemoteProgramming), AcceptRemoteProgramming);
        p.Set(nameof(KeepAliveMs), KeepAliveMs);
        p.Set(nameof(ProductUrl), ProductUrl);
        p.Set(nameof(AutoStart), AutoStart);
    }

    /// <summary>Parses a comma separated list of Port-Addresses ("1, 2, 0:1:0").</summary>
    public static IReadOnlyList<PortAddress> ParseUniverses(string? text) =>
        (text ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(PortAddress.Parse).Distinct().ToArray();
}
