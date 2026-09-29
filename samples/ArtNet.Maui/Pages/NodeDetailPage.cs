using System.Net;
using ArtNet.Maui.Services;
using ArtNet.Networking;

namespace ArtNet.Maui.Pages;

/// <summary>
/// Everything about one device: every ArtPollReply field in readable form, plus ArtAddress (names, universes,
/// every command), ArtInput, ArtIpProg, ArtDataRequest, RDM (TOD, TOD control, get/set) and firmware upload.
/// </summary>
public sealed class NodeDetailPage : ContentPage
{
    private readonly ArtNetService _service;
    private readonly ArtNetRemoteNode _node;
    private readonly ContentView _fields = new();

    public NodeDetailPage(ArtNetService service, ArtNetRemoteNode node)
    {
        _service = service;
        _node = node;
        Title = node.DisplayName;
        RefreshFields();

        Content = Ui.Page(
            Ui.Card("Status", _fields),
            IdentifyCard(),
            ProgramCard(),
            InputCard(),
            IpCard(),
            DataCard(),
            RdmCard(),
            FirmwareCard());
    }

    // Subscribe only while visible: the node outlives the page, so a permanent handler would keep every page alive.
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _node.PropertyChanged += OnNodeChanged;
        RefreshFields();
    }

    protected override void OnDisappearing()
    {
        _node.PropertyChanged -= OnNodeChanged;
        base.OnDisappearing();
    }

    private void OnNodeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArtNetRemoteNode.Reply)) MainThread.BeginInvokeOnMainThread(RefreshFields);
    }

    private ArtNetNode Net => _service.RequireNode();
    private IPAddress Ip => _node.Address;

    private void RefreshFields() =>
        _fields.Content = Ui.Fields(_node.Reply.Describe().Where(f => f.Section != "Header")
            .Prepend(new ArtNetField("Discovery", "Source", _node.AddressText))
            .Prepend(new ArtNetField("Discovery", "Last Seen", _node.LastSeen.ToLocalTime().ToString("HH:mm:ss")))
            .Prepend(new ArtNetField("Discovery", "Replies", _node.ReplyCount.ToString())));

    private string ReplyText(ArtPollReplyPacket? reply) =>
        reply is null ? "No ArtPollReply within the timeout." : $"Node replied: {reply.ShortName} · {reply.IndicatorState.ToDisplayName()} indicators · {reply.NodeReport}";

    // ------------------------------------------------------------------ ArtAddress commands

    private View IdentifyCard()
    {
        var status = Ui.Caption();
        var commands = ArtNetOptionCatalog.AddressCommands;
        var picker = Ui.Picker(commands, 0, 320);
        var description = Ui.Caption(commands[0].Description);
        picker.SelectedIndexChanged += (_, _) => description.Text = picker.SelectedItem is ArtNetOption<ArtNetAddressCommand> o ? o.Description : "";

        Button Cmd(string text, ArtNetAddressCommand c) => Ui.Button(text, async () =>
            status.Text = ReplyText(await Net.SendAddressAsync(Ip, ArtAddressPacket.ForCommand(c, _node.BindIndex))), status);

        return Ui.Card("Identify & commands (ArtAddress)",
            Ui.Row(Cmd("Locate", ArtNetAddressCommand.LedLocate), Cmd("Mute LEDs", ArtNetAddressCommand.LedMute),
                   Cmd("LEDs normal", ArtNetAddressCommand.LedNormal), Cmd("Cancel merge", ArtNetAddressCommand.CancelMerge),
                   Cmd("Reset Rx flags", ArtNetAddressCommand.ResetRxFlags)),
            Ui.Row(picker, Ui.Button("Send command", async () =>
            {
                if (picker.SelectedItem is not ArtNetOption<ArtNetAddressCommand> o) return;
                status.Text = ReplyText(await Net.SendAddressAsync(Ip, ArtAddressPacket.ForCommand(o.Value, _node.BindIndex)));
            }, status)),
            description,
            status);
    }

    // ------------------------------------------------------------------ ArtAddress programming

    private View ProgramCard()
    {
        var status = Ui.Caption();
        var shortName = Ui.Entry(_node.ShortName, "unchanged", 220);
        var longName = Ui.Entry(_node.LongName, "unchanged", 420);
        var port = Ui.Picker(new[] { "Port 1", "Port 2", "Port 3", "Port 4" }, 0, 120);
        var firstPort = _node.Ports.FirstOrDefault();
        // Empty unless typed: a pre-filled value would reprogram the port on every Program click.
        var universe = Ui.Entry("", firstPort is null ? "unchanged" : $"unchanged ({(firstPort.CanOutput ? firstPort.OutputAddress : firstPort.InputAddress).Value})", 140);
        var direction = Ui.Picker(new[] { "Output (SwOut)", "Input (SwIn)" }, firstPort is { CanOutput: false, CanInput: true } ? 1 : 0, 160);
        var acn = Ui.Entry("", "sACN priority 0-200", 160, Keyboard.Numeric);
        var bind = Ui.Entry(_node.BindIndex.ToString(), "bind", 80, Keyboard.Numeric);

        return Ui.Card("Program names & universes (ArtAddress)",
            Ui.Row(Ui.Field("Short name (17)", shortName), Ui.Field("Long name (63)", longName)),
            Ui.Row(Ui.Field("Bind index", bind), Ui.Field("Port", port), Ui.Field("Direction", direction), Ui.Field("Universe", universe), Ui.Field("sACN priority", acn)),
            Ui.Caption("Empty fields are left unchanged. The universe sets Net, Sub-Net and the port's SwIn/SwOut together."),
            Ui.Row(
                Ui.Button("Program", async () =>
                {
                    var p = new ArtAddressPacket
                    {
                        BindIndex = byte.Parse(bind.Text ?? "1"),
                        ShortName = shortName.Text == _node.ShortName ? "" : shortName.Text ?? "",
                        LongName = longName.Text == _node.LongName ? "" : longName.Text ?? "",
                    };
                    if (!string.IsNullOrWhiteSpace(universe.Text))
                    {
                        var a = Ui.ParseUniverse(universe);
                        if (direction.SelectedIndex == 1) p.SetInputAddress(a, port.SelectedIndex);
                        else p.SetOutputAddress(a, port.SelectedIndex);
                    }
                    if (!string.IsNullOrWhiteSpace(acn.Text)) p.AcnPriority = Ui.ParseByte(acn.Text, "sACN priority");
                    status.Text = ReplyText(await Net.SendAddressAsync(Ip, p));
                }, status),
                Ui.Button("Reset switches to physical", async () =>
                {
                    var p = new ArtAddressPacket { BindIndex = byte.Parse(bind.Text ?? "1"), NetSwitch = 0, SubSwitch = 0 };
                    p.SwIn[port.SelectedIndex] = 0;
                    p.SwOut[port.SelectedIndex] = 0;
                    status.Text = ReplyText(await Net.SendAddressAsync(Ip, p));
                }, status)),
            status);
    }

    // ------------------------------------------------------------------ ArtInput

    private View InputCard()
    {
        var status = Ui.Caption();
        var switches = Enumerable.Range(0, 4).Select(i => new CheckBox { IsChecked = false }).ToArray();
        for (int i = 0; i < _node.Ports.Count && i < 4; i++)
            switches[i].IsChecked = _node.Ports[i].GoodInput.HasFlag(ArtNetGoodInput.Disabled);
        var row = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        for (int i = 0; i < 4; i++)
            row.Children.Add(new HorizontalStackLayout { Margin = new Thickness(0, 0, 12, 0), Children = { switches[i], new Label { Text = $"Disable input {i + 1}", VerticalOptions = LayoutOptions.Center } } });
        return Ui.Card("Inputs (ArtInput)", row,
            Ui.Button("Send ArtInput", async () =>
            {
                var p = new ArtInputPacket { BindIndex = _node.BindIndex, NumPorts = (ushort)Math.Max(1, _node.Ports.Count) };
                for (int i = 0; i < 4; i++) p.SetDisabled(i, switches[i].IsChecked);
                status.Text = ReplyText(await Net.SendInputAsync(Ip, p));
            }, status),
            status);
    }

    // ------------------------------------------------------------------ ArtIpProg

    private View IpCard()
    {
        var status = Ui.Mono();
        var ip = Ui.Entry(_node.ReportedAddress.ToString(), "IP", 160);
        var mask = Ui.Entry("255.0.0.0", "mask", 160);
        var gw = Ui.Entry("", "gateway (optional)", 160);

        async Task Send(ArtIpProgPacket p)
        {
            var reply = await Net.SendIpProgAsync(Ip, p);
            status.Text = reply is null
                ? "No ArtIpProgReply – the node does not support remote IP programming."
                : ArtNetFormatter.Format(reply, includeHeader: false);
        }

        return Ui.Card("IP settings (ArtIpProg)",
            Ui.Row(Ui.Field("IP address", ip), Ui.Field("Subnet mask", mask), Ui.Field("Default gateway", gw)),
            Ui.Row(
                Ui.Button("Read (enquiry)", () => Send(ArtIpProgPacket.Enquiry()), status),
                Ui.Button("Program static", () => Send(ArtIpProgPacket.Program(IPAddress.Parse(ip.Text!), IPAddress.Parse(mask.Text!),
                    string.IsNullOrWhiteSpace(gw.Text) ? null : IPAddress.Parse(gw.Text))), status),
                Ui.Button("Enable DHCP", () => Send(ArtIpProgPacket.EnableDhcp()), status),
                Ui.Button("Reset to default", () => Send(ArtIpProgPacket.ResetToDefault()), status),
                Ui.Button("Art-Net default IP", () =>
                {
                    var mac = _node.Reply.Mac;
                    ip.Text = ArtNetNetworkInterface.DefaultArtNetAddress(mac, _node.Oem).ToString();
                    mask.Text = "255.0.0.0";
                    return Task.CompletedTask;
                }, status)),
            status);
    }

    // ------------------------------------------------------------------ ArtDataRequest

    private View DataCard()
    {
        var status = Ui.Mono();
        var codes = ArtNetOptionCatalog.DataRequestCodes.Where(c => c.Value != ArtNetDataRequestCode.ManufacturerSpecific).ToList();
        var picker = Ui.Picker(codes, 1, 260);
        var custom = Ui.Entry("", "or code e.g. 0x8001", 180);
        return Ui.Card("Data (ArtDataRequest)",
            Ui.Row(picker, custom,
                Ui.Button("Request", async () =>
                {
                    ushort code = !string.IsNullOrWhiteSpace(custom.Text)
                        ? (ushort)ArtNetText.Parse<ArtNetDataRequestCode>(custom.Text)
                        : (ushort)((ArtNetOption<ArtNetDataRequestCode>)picker.SelectedItem!).Value;
                    var reply = await Net.RequestDataAsync(Ip, code);
                    status.Text = reply is null ? "No ArtDataReply (not supported or no data)." : ArtNetFormatter.Format(reply, includeHeader: false);
                }, status),
                Ui.Button("Request all URLs", async () =>
                {
                    var lines = new List<string>();
                    foreach (var c in codes)
                    {
                        var r = await Net.RequestDataAsync(Ip, c.Value, TimeSpan.FromSeconds(1.5));
                        lines.Add($"{c.Name,-22} {(r is null ? "(no reply)" : r.Payload.Length == 0 ? "(supported)" : r.PayloadText)}");
                    }
                    status.Text = string.Join(Environment.NewLine, lines);
                }, status)),
            status);
    }

    // ------------------------------------------------------------------ RDM

    private View RdmCard()
    {
        var status = Ui.Mono();
        var universes = _node.Ports.Where(p => p.CanOutput).Select(p => p.OutputAddress).DefaultIfEmpty(new PortAddress(1)).ToList();
        var universe = Ui.Entry(universes[0].Value.ToString(), "universe", 120);
        var uid = Ui.Entry("", "UID MMMM:DDDDDDDD", 200);
        var uidPicker = new Picker { WidthRequest = 200, Title = "Discovered UIDs" };
        uidPicker.SelectedIndexChanged += (_, _) => { if (uidPicker.SelectedItem is RdmUid u) uid.Text = u.ToString(); };
        var pids = RdmText.Parameters.OrderBy(k => k.Key).Select(k => $"0x{k.Key:X4} {k.Value}").ToList();
        var pid = Ui.Picker(pids, pids.FindIndex(p => p.Contains("DEVICE_INFO")), 300);
        var cc = Ui.Picker(new[] { RdmCommandClass.Get, RdmCommandClass.Set }, 0, 100);
        var data = Ui.Entry("", "parameter data (hex)", 200);
        var sub = Ui.Entry("0", "sub-device", 100, Keyboard.Numeric);
        var todCommands = ArtNetOptionCatalog.TodControlCommands.Where(c => c.Value != ArtNetTodControlCommand.None).ToList();
        var todPicker = Ui.Picker(todCommands, 0, 200);

        return Ui.Card("RDM (ArtTodRequest / ArtTodControl / ArtRdm)",
            Ui.Row(Ui.Field("Universe", universe),
                Ui.Button("Get table of devices", async () =>
                {
                    var a = Ui.ParseUniverse(universe);
                    var packets = await Net.RequestTodAsync([a], TimeSpan.FromSeconds(2));
                    var table = ArtNetNode.MergeTod(packets.Where(p => p.PortAddress == a));
                    var uids = table.TryGetValue(a, out var list) ? list : [];
                    uidPicker.ItemsSource = uids.ToList();
                    status.Text = packets.Count == 0 ? "No ArtTodData received." :
                        packets.Any(p => p.CommandResponse == ArtNetTodDataCommand.TodNak) && uids.Count == 0 ? "TOD not available (discovery incomplete)." :
                        $"{uids.Count} device(s):{Environment.NewLine}{string.Join(Environment.NewLine, uids)}";
                }, status),
                todPicker,
                Ui.Button("Send TOD control", async () =>
                {
                    var c = ((ArtNetOption<ArtNetTodControlCommand>)todPicker.SelectedItem!).Value;
                    var r = await Net.SendTodControlAsync(Ip, Ui.ParseUniverse(universe), c, TimeSpan.FromSeconds(5));
                    status.Text = r is null ? "No ArtTodData within 5 s." : ArtNetFormatter.Format(r, includeHeader: false);
                }, status)),
            Ui.Row(uidPicker, Ui.Field("UID", uid), Ui.Field("Class", cc), Ui.Field("Parameter", pid), Ui.Field("Sub-device", sub), Ui.Field("Data", data)),
            Ui.Button("Send RDM request", async () =>
            {
                var target = RdmUid.Parse(uid.Text ?? "");
                var pidValue = Convert.ToUInt16(((string)pid.SelectedItem!)[2..6], 16);
                var msg = RdmMessage.Build(target, new RdmUid(ArtNetConstants.EstaPrototype, 0x00000001), (byte)Random.Shared.Next(256), 1,
                    ushort.Parse(sub.Text ?? "0"), (RdmCommandClass)cc.SelectedItem!, pidValue, Ui.ParseHex(data.Text));
                var reply = await Net.SendRdmAsync(Ip, Ui.ParseUniverse(universe), msg);
                status.Text = reply is null ? "No RDM response within the timeout." : ArtNetFormatter.Format(reply, includeHeader: false);
            }, status),
            status);
    }

    // ------------------------------------------------------------------ firmware

    private View FirmwareCard()
    {
        var status = Ui.Mono();
        var progress = new ProgressBar { Progress = 0 };
        ArtNetFirmwareFile? file = null;
        bool ubea = false;
        return Ui.Card("Firmware upload (ArtFirmwareMaster)",
            Ui.Caption("Uploads an Art-Net .alf firmware or .alu UBEA file block by block (30 s reply timeout per block). The file's OEM list is checked against this node."),
            Ui.Row(
                Ui.Button("Choose file…", async () =>
                {
                    var result = await FilePicker.Default.PickAsync();
                    if (result is null) return;
                    await using var stream = await result.OpenReadAsync();
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    file = ArtNetFirmwareFile.Parse(ms.ToArray());
                    ubea = result.FileName.EndsWith(".alu", StringComparison.OrdinalIgnoreCase);
                    status.Text = $"{result.FileName}: {file}" + (file.SupportsOem(_node.Oem) || file.OemCodes.Count == 0 ? "" : $"{Environment.NewLine}⚠ Not valid for OEM {_node.OemText}.");
                }, status),
                Ui.Button("Upload", async () =>
                {
                    if (file is null) { status.Text = "Choose a file first."; return; }
                    if (Application.Current?.Windows.FirstOrDefault()?.Page is { } page &&
                        !await page.DisplayAlertAsync("Firmware upload", $"Upload {file.UserName} to {_node.DisplayName}?", "Upload", "Cancel")) return;
                    var result = await Net.UploadFirmwareAsync(Ip, file, ubea, new Progress<ArtNetFirmwareProgress>(p =>
                    {
                        progress.Progress = p.Fraction;
                        status.Text = p.ToString();
                    }), _node);
                    status.Text = $"Result: {result.ToDisplayName()} – {result.ToDescription()}";
                }, status)),
            progress,
            status);
    }
}
