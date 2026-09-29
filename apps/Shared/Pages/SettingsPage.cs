using ArtNet.Shared.Services;
using ArtNet.Networking;

namespace ArtNet.Shared.Pages;

/// <summary>This node's identity, network interface, ports and behaviour. Saved with Preferences; applying restarts the node.</summary>
public sealed class SettingsPage : ContentPage
{
    private sealed record InterfaceOption(string Display, string Address)
    {
        public override string ToString() => Display;
    }

    public SettingsPage(ArtNetService service)
    {
        Title = "Settings";
        var s = service.Settings;
        var message = Ui.Caption();

        var shortName = Ui.Entry(s.ShortName, "short name", 240);
        shortName.MaxLength = 17;
        var longName = Ui.Entry(s.LongName, "long name", 420);
        longName.MaxLength = 63;
        var styles = ArtNetOptionCatalog.Styles;
        var style = Ui.Picker(styles, Math.Max(0, styles.ToList().FindIndex(o => o.Value == s.Style)), 260);

        var interfaces = new List<InterfaceOption> { new("Automatic (default interface)", "") };
        interfaces.AddRange(ArtNetNetworkInterface.GetAll().Where(n => n.IsUp)
            .Select(n => new InterfaceOption($"{n.Name} – {n.Address} (broadcast {n.Broadcast})", n.Address.ToString())));
        if (!string.IsNullOrWhiteSpace(s.InterfaceAddress) && interfaces.All(i => i.Address != s.InterfaceAddress))
            interfaces.Add(new($"{s.InterfaceAddress} – not available (the default interface is used)", s.InterfaceAddress));
        var iface = Ui.Picker(interfaces, Math.Max(0, interfaces.FindIndex(i => i.Address == s.InterfaceAddress)), 420);
        var broadcast = Ui.Entry(s.BroadcastAddress, "e.g. 2.255.255.255 (empty = from interface)", 260);

        var outputs = Ui.Entry(s.OutputUniverses, "e.g. 1, 2, 0:1:0", 240);
        var inputs = Ui.Entry(s.InputUniverses, "e.g. 10", 240);
        var merges = ArtNetOptionCatalog.MergeModes;
        var merge = Ui.Picker(merges, (int)s.MergeMode, 140);
        var pollInterval = Ui.Entry(s.PollIntervalMs.ToString(), "ms", 100, Keyboard.Numeric);
        var keepAlive = Ui.Entry(s.KeepAliveMs.ToString(), "ms", 100, Keyboard.Numeric);
        var url = Ui.Entry(s.ProductUrl, "https://… (answered to ArtDataRequest)", 360);

        bool sendPolls = s.SendPolls, broadcastDmx = s.BroadcastDmxWithoutSubscribers, sync = s.EnableSync,
             programming = s.AcceptRemoteProgramming, autoStart = s.AutoStart;

        var preview = Ui.Mono("", 12);
        void Preview()
        {
            try { preview.Text = string.Join(Environment.NewLine, new ArtNetNode(service.BuildNodeSettings()).CreatePollReplies().Select(r => r.Summary)); }
            catch (Exception ex) { preview.Text = ex.Message; }
        }
        Preview();

        // A controller can reprogram names and ports (ArtAddress / ArtInput); the service saves them, show them here.
        service.SettingsChanged += (_, _) =>
        {
            shortName.Text = s.ShortName;
            longName.Text = s.LongName;
            outputs.Text = s.OutputUniverses;
            inputs.Text = s.InputUniverses;
            Preview();
            message.Text = "A controller reprogrammed this node; the new names and ports were saved.";
        };

        Content = Ui.Page(
            Ui.Card("This node",
                Ui.Row(Ui.Field("Short name (17)", shortName), Ui.Field("Long name (63)", longName)),
                Ui.Field("Style (ArtPollReply)", style),
                Ui.Field("Product URL", url)),
            Ui.Card("Network",
                Ui.Field("Interface (sets the reported IP and the directed broadcast address)", iface),
                Ui.Field("Broadcast address override", broadcast),
                Ui.Caption("Art-Net's native networks are 2.x.x.x and 10.x.x.x with mask 255.0.0.0 (broadcast 2.255.255.255 / 10.255.255.255)."),
                Ui.Row(Ui.Field("ArtPoll interval (ms, 1000-10000; spec 2500-3000)", pollInterval), Ui.Switch("Send ArtPoll (controller)", sendPolls, v => sendPolls = v))),
            Ui.Card("Ports",
                Ui.Caption("Each port is announced in its own ArtPollReply (bind index 1, 2, …). Output ports make compliant controllers unicast those universes to this app."),
                Ui.Row(Ui.Field("Output universes", outputs), Ui.Field("Input universes", inputs), Ui.Field("Merge mode", merge)),
                Ui.Field("Announced replies", preview)),
            Ui.Card("Behaviour",
                Ui.Field("DMX keep-alive (ms, 0 = off; spec 800-1000)", keepAlive),
                Ui.Switch("Broadcast ArtDmx when a universe has no subscribers (not Art-Net 4 compliant)", broadcastDmx, v => broadcastDmx = v),
                Ui.Switch("Honour ArtSync (synchronous output)", sync, v => sync = v),
                Ui.Switch("Accept ArtAddress / ArtInput programming of this node", programming, v => programming = v),
                Ui.Switch("Start the node when the app opens", autoStart, v => autoStart = v)),
            Ui.Button("Save and apply", async () =>
            {
                // Validate everything before changing any setting, so an error leaves the settings as they were.
                AppSettings.ParseUniverses(outputs.Text);
                AppSettings.ParseUniverses(inputs.Text);
                int pollMs = ParseMs(pollInterval.Text, "ArtPoll interval", 2500);
                if (pollMs is < AppSettings.MinPollIntervalMs or > AppSettings.MaxPollIntervalMs)
                    throw new FormatException($"ArtPoll interval must be {AppSettings.MinPollIntervalMs}-{AppSettings.MaxPollIntervalMs} ms.");
                int keepAliveMs = ParseMs(keepAlive.Text, "DMX keep-alive", 900);
                if (!string.IsNullOrWhiteSpace(broadcast.Text)) AppSettings.ParseIPv4(broadcast.Text, "Broadcast address override");
                s.ShortName = string.IsNullOrWhiteSpace(shortName.Text) ? AppBrand.Name : shortName.Text.Trim();
                s.LongName = longName.Text ?? "";
                s.Style = ((ArtNetOption<ArtNetStyle>)style.SelectedItem!).Value;
                s.InterfaceAddress = (iface.SelectedItem as InterfaceOption)?.Address ?? "";
                s.BroadcastAddress = broadcast.Text?.Trim() ?? "";
                s.OutputUniverses = outputs.Text ?? "";
                s.InputUniverses = inputs.Text ?? "";
                s.MergeMode = ((ArtNetOption<ArtNetMergeMode>)merge.SelectedItem!).Value;
                s.PollIntervalMs = pollMs;
                s.KeepAliveMs = keepAliveMs;
                s.ProductUrl = url.Text ?? "";
                s.SendPolls = sendPolls;
                s.BroadcastDmxWithoutSubscribers = broadcastDmx;
                s.EnableSync = sync;
                s.AcceptRemoteProgramming = programming;
                s.AutoStart = autoStart;
                s.Save();
                Preview();
                if (service.IsRunning)
                {
                    await service.RestartAsync();
                    message.Text = service.IsRunning ? "Saved. Node restarted with the new settings." : $"Saved, but the node could not start: {service.LastError}";
                }
                else message.Text = "Saved.";
            }, message),
            message);
    }

    private static int ParseMs(string? text, string what, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (!int.TryParse(text.Trim(), out int ms) || ms < 0) throw new FormatException($"{what} must be a whole number of milliseconds.");
        return ms;
    }
}
