using System.Net;
using ArtNet.Desktop.Services;

namespace ArtNet.Desktop.Pages;

/// <summary>
/// Show control and the remaining packets: ArtTimeCode generator, ArtTrigger, ArtCommand, ArtDiagData, ArtSync,
/// ArtNzs, ArtVlc and raw datagrams, plus a log of received show-control packets.
/// </summary>
public sealed class ShowPage : ContentPage
{
    private readonly ArtNetService _service;
    private CancellationTokenSource? _timecodeCts;

    public ShowPage(ArtNetService service)
    {
        _service = service;
        Title = "Show";
        Content = Ui.Page(ReceivedCard(), TimeCodeCard(), TriggerCard(), CommandCard(), DiagCard(), NzsCard(), VlcCard(), RawCard());
    }

    private static IPAddress? Target(Entry e) =>
        string.IsNullOrWhiteSpace(e.Text) ? null : IPAddress.Parse(e.Text.Trim());

    private View ReceivedCard()
    {
        var tc = new Label { Style = Ui.Style("Mono"), FontSize = 28, BindingContext = _service };
        tc.SetBinding(Label.TextProperty, static (ArtNetService s) => s.LastTimeCode);
        var list = new CollectionView
        {
            ItemsSource = _service.ShowEvents,
            HeightRequest = 220,
            EmptyView = Ui.Caption("Received ArtTrigger, ArtCommand, ArtDiagData, ArtVlc and programming of this node appear here."),
            ItemTemplate = new DataTemplate(() =>
            {
                var time = Ui.Mono("", 12);
                time.SetBinding(Label.TextProperty, static (ShowEvent e) => e.Time);
                var src = Ui.Caption();
                src.SetBinding(Label.TextProperty, static (ShowEvent e) => e.Source);
                var text = new Label { LineBreakMode = LineBreakMode.WordWrap };
                text.SetBinding(Label.TextProperty, static (ShowEvent e) => e.Text);
                var g = new Grid { ColumnDefinitions = [new(new GridLength(96)), new(new GridLength(110)), new(GridLength.Star)], ColumnSpacing = 8, Padding = new Thickness(0, 3) };
                g.Add(time, 0, 0); g.Add(src, 1, 0); g.Add(text, 2, 0);
                return g;
            }),
        };
        return Ui.Card("Received", Ui.Caption("Last ArtTimeCode"), tc, list, Ui.Button("Clear", _service.ClearShowEvents));
    }

    private View TimeCodeCard()
    {
        var status = Ui.Caption();
        var types = ArtNetOptionCatalog.TimeCodeTypes;
        var type = Ui.Picker(types, 3, 180);
        var start = Ui.Entry("00:00:00:00", "HH:MM:SS:FF", 140);
        var stream = Ui.Entry("0", "stream", 80, Keyboard.Numeric);
        var to = Ui.Entry("", "unicast IP (empty = broadcast)", 220);
        var running = Ui.Mono("", 22);

        ArtTimeCodePacket Build()
        {
            var t = ((ArtNetOption<ArtNetTimeCodeType>)type.SelectedItem!).Value;
            var parts = (start.Text ?? "0:0:0:0").Split(':', ';', '.');
            if (parts.Length != 4) throw new FormatException("Start must be HH:MM:SS:FF.");
            var v = parts.Select(int.Parse).ToArray();
            int fps = t.NominalFrames();
            if (v[0] is < 0 or > 23 || v[1] is < 0 or > 59 || v[2] is < 0 or > 59 || v[3] < 0 || v[3] >= fps)
                throw new FormatException($"Start must be within 00:00:00:00-23:59:59:{fps - 1:00}.");
            return new ArtTimeCodePacket
            {
                Type = t, Hours = (byte)v[0], Minutes = (byte)v[1], Seconds = (byte)v[2],
                Frames = (byte)v[3], StreamId = Ui.ParseByte(stream.Text, "Stream"),
            };
        }

        return Ui.Card("Time code (ArtTimeCode)",
            Ui.Row(Ui.Field("Type", type), Ui.Field("Start", start), Ui.Field("Stream", stream), Ui.Field("Destination", to)),
            Ui.Row(
                Ui.Button("Send one", async () =>
                {
                    var p = Build();
                    await _service.RequireNode().SendTimeCodeAsync(p, Target(to));
                    status.Text = p.Summary;
                }, status),
                Ui.Button("Run", () =>
                {
                    _timecodeCts?.Cancel();
                    var cts = _timecodeCts = new CancellationTokenSource();
                    var p = Build();
                    var node = _service.RequireNode();
                    var target = Target(to);
                    _ = Task.Run(async () =>
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        long frame = 0;
                        try
                        {
                            while (!cts.IsCancellationRequested)
                            {
                                await node.SendTimeCodeAsync(p, target);
                                if (frame % 3 == 0) { var text = p.TimeText; MainThread.BeginInvokeOnMainThread(() => running.Text = text); }
                                p.Increment();
                                frame++;
                                var due = TimeSpan.FromSeconds(frame / p.FrameRate) - sw.Elapsed;
                                if (due > TimeSpan.Zero) await Task.Delay(due, cts.Token);
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { MainThread.BeginInvokeOnMainThread(() => status.Text = "⚠ " + ex.Message); }
                    });
                    status.Text = $"Running {p.Type.ToDisplayName()} from {p.TimeText}.";
                    return Task.CompletedTask;
                }, status),
                Ui.Button("Stop", () => { _timecodeCts?.Cancel(); status.Text = "Stopped."; })),
            running,
            status);
    }

    private View TriggerCard()
    {
        var status = Ui.Caption();
        var keys = ArtNetOptionCatalog.TriggerKeys;
        var key = Ui.Picker(keys, 1, 180);
        var sub = Ui.Entry("1", "sub-key (number or character)", 120);
        var oem = Ui.Entry("0xFFFF", "OEM", 100);
        var rawKey = Ui.Entry("", "raw key (OEM ≠ 0xFFFF)", 120);
        var payload = Ui.Entry("", "payload hex (optional)", 220);
        var to = Ui.Entry("", "unicast IP (empty = broadcast)", 220);
        var description = Ui.Caption(keys[1].Description);
        key.SelectedIndexChanged += (_, _) => description.Text = ((ArtNetOption<ArtNetTriggerKey>)key.SelectedItem!).Description;

        return Ui.Card("Trigger (ArtTrigger)",
            Ui.Row(Ui.Field("Key", key), Ui.Field("Sub-key", sub), Ui.Field("OEM", oem), Ui.Field("Raw key", rawKey), Ui.Field("Payload", payload), Ui.Field("Destination", to)),
            description,
            Ui.Button("Send trigger", async () =>
            {
                ushort o = Convert.ToUInt16((oem.Text ?? "0xFFFF").Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
                var k = ((ArtNetOption<ArtNetTriggerKey>)key.SelectedItem!).Value;
                string s = sub.Text ?? "0";
                byte subKey = k == ArtNetTriggerKey.Ascii && s.Length == 1 && !char.IsDigit(s[0]) ? (byte)s[0] : Ui.ParseByte(s, "Sub-key");
                var p = new ArtTriggerPacket { Oem = o, Key = o == ArtNetConstants.Global ? (byte)k : Ui.ParseByte(rawKey.Text, "Raw key"), SubKey = subKey };
                p.SetData(Ui.ParseHex(payload.Text));
                await _service.RequireNode().SendTriggerAsync(p, Target(to));
                status.Text = p.Summary;
            }, status),
            status);
    }

    private View CommandCard()
    {
        var status = Ui.Caption();
        var text = Ui.Entry("SwoutText=Playback&", "Command=Data&", 360);
        var esta = Ui.Entry("0xFFFF", "ESTA", 100);
        var to = Ui.Entry("", "unicast IP (empty = broadcast)", 220);
        return Ui.Card("Command (ArtCommand)",
            Ui.Caption("Art-Net defined commands (ESTA 0xFFFF): SwoutText=label& relabels outputs, SwinText=label& relabels inputs."),
            Ui.Row(Ui.Field("Text", text), Ui.Field("ESTA", esta), Ui.Field("Destination", to)),
            Ui.Button("Send command", async () =>
            {
                var p = new ArtCommandPacket
                {
                    Text = text.Text ?? "",
                    EstaManufacturer = Convert.ToUInt16((esta.Text ?? "0xFFFF").Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16),
                };
                await _service.RequireNode().SendCommandAsync(p, Target(to));
                status.Text = p.Summary;
            }, status),
            status);
    }

    private View DiagCard()
    {
        var status = Ui.Caption();
        var text = Ui.Entry("Hello from Art-Net Desktop", "text", 360);
        var priorities = ArtNetOptionCatalog.DiagnosticPriorities;
        var priority = Ui.Picker(priorities, 0, 160);
        var port = Ui.Entry("0", "logical port", 80, Keyboard.Numeric);
        return Ui.Card("Diagnostics (ArtDiagData)",
            Ui.Caption("'Send to subscribers' follows the ArtPoll rules (unicast to a single requesting controller, otherwise broadcast); 'Broadcast' always broadcasts."),
            Ui.Row(Ui.Field("Text", text), Ui.Field("Priority", priority), Ui.Field("Logical port", port)),
            Ui.Row(
                Ui.Button("Send to subscribers", async () =>
                {
                    var p = ((ArtNetOption<ArtNetDiagnosticPriority>)priority.SelectedItem!).Value;
                    bool sent = await _service.RequireNode().SendDiagnosticAsync(text.Text ?? "", p, Ui.ParseByte(port.Text, "Logical port"));
                    status.Text = sent ? "Sent." : "No controller has requested diagnostics at this priority.";
                }, status),
                Ui.Button("Broadcast", async () =>
                {
                    var p = new ArtDiagDataPacket
                    {
                        Text = text.Text ?? "",
                        Priority = ((ArtNetOption<ArtNetDiagnosticPriority>)priority.SelectedItem!).Value,
                        LogicalPort = Ui.ParseByte(port.Text, "Logical port"),
                    };
                    await _service.RequireNode().BroadcastAsync(p);
                    status.Text = p.Summary;
                }, status)),
            status);
    }

    private View NzsCard()
    {
        var status = Ui.Caption();
        var universe = Ui.Entry("1", "universe", 100);
        var startCode = Ui.Entry("0x17", "start code", 100);
        var data = Ui.Entry("48 65 6C 6C 6F", "data hex", 260);
        return Ui.Card("Non-zero start code (ArtNzs)",
            Ui.Row(Ui.Field("Universe", universe), Ui.Field("Start code", startCode), Ui.Field("Data", data)),
            Ui.Button("Send to subscribers", async () =>
            {
                var sc = Ui.ParseByte(startCode.Text, "Start code");
                if (sc is 0 or ArtNetConstants.RdmStartCode) throw new FormatException("Start code must not be 0x00 or 0xCC (RDM).");
                var p = new ArtNzsPacket { PortAddress = Ui.ParseUniverse(universe), StartCode = sc, Data = Ui.ParseHex(data.Text) };
                int n = await _service.RequireNode().SendNzsAsync(p);
                status.Text = $"{p.Summary} → {n} destination(s)";
            }, status),
            status);
    }

    private View VlcCard()
    {
        var status = Ui.Caption();
        var universe = Ui.Entry("1", "universe", 100);
        var languages = ArtNetOptionCatalog.VlcPayloadLanguages;
        var language = Ui.Picker(languages, 0, 180);
        var payload = Ui.Entry("https://art-net.org.uk", "payload", 300);
        var slot = Ui.Entry("0", "slot (0 = all)", 100, Keyboard.Numeric);
        var beacon = Ui.Entry("", "beacon repeat Hz", 120, Keyboard.Numeric);
        return Ui.Card("Visible light communication (ArtVlc)",
            Ui.Row(Ui.Field("Universe", universe), Ui.Field("Payload type", language), Ui.Field("Payload", payload), Ui.Field("Slot", slot), Ui.Field("Beacon", beacon)),
            Ui.Button("Send to subscribers", async () =>
            {
                var lang = ((ArtNetOption<ArtVlcPayloadLanguage>)language.SelectedItem!).Value;
                var p = new ArtVlcPacket { PortAddress = Ui.ParseUniverse(universe), PayloadLanguage = lang, SlotAddress = ushort.Parse(slot.Text ?? "0") };
                if (lang == ArtVlcPayloadLanguage.BeaconLocationId)
                {
                    ushort id = ushort.Parse(payload.Text ?? "0");
                    p.Payload = [(byte)(id >> 8), (byte)id];
                }
                else p.PayloadText = payload.Text ?? "";
                if (!string.IsNullOrWhiteSpace(beacon.Text)) { p.Flags |= ArtVlcFlags.Beacon; p.BeaconRepeat = ushort.Parse(beacon.Text); }
                int n = await _service.RequireNode().SendNzsAsync(p);
                status.Text = $"{p.Summary} → {n} destination(s)";
            }, status),
            status);
    }

    private View RawCard()
    {
        var status = Ui.Mono();
        var hex = Ui.Entry(Convert.ToHexString(new ArtPollPacket().ToArray()), "hex datagram", 520);
        var to = Ui.Entry("", "IP (empty = broadcast)", 200);
        return Ui.Card("Raw datagram",
            Ui.Row(Ui.Field("Hex", hex), Ui.Field("Destination", to)),
            Ui.Row(
                Ui.Button("Decode", () =>
                {
                    var bytes = Ui.ParseHex(hex.Text);
                    status.Text = ArtNetPacketParser.TryParse(bytes, out var p) ? ArtNetFormatter.Format(p) : "Not a valid Art-Net packet.";
                }, status),
                Ui.Button("Send", async () =>
                {
                    var node = _service.RequireNode();
                    var bytes = Ui.ParseHex(hex.Text);
                    var target = Target(to) ?? node.BroadcastAddress;
                    await node.SendRawAsync(bytes, new IPEndPoint(target, node.Settings.Port));
                    status.Text = $"Sent {bytes.Length} bytes to {target}.";
                }, status)),
            status);
    }
}
