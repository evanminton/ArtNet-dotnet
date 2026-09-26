using System.Globalization;
using System.Net;
using ArtNet;
using ArtNet.Networking;

// artnet-monitor – human-readable Art-Net 4 utility built on the ArtNet library. Every packet can be sent and decoded.

Console.OutputEncoding = System.Text.Encoding.UTF8;
var argList = args.ToList();
if (argList.Count == 0 || argList[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

string command = argList[0].ToLowerInvariant();
var o = Options.Parse(argList.Skip(1).ToList());

try
{
    return command switch
    {
        "options" => ShowOptions(o),
        "interfaces" => ShowInterfaces(),
        "decode" => Decode(o),
        "defaultip" => DefaultIp(o),
        "firmware-info" => FirmwareInfo(o),
        "listen" => await Listen(o),
        "nodes" => await Nodes(o),
        "watch" => await Watch(o),
        "dmx" => await SendDmx(o),
        "sync" => await Simple(o, n => n.SendSyncAsync(), "ArtSync broadcast"),
        "poll" => await Poll(o),
        "address" => await Address(o),
        "input" => await Input(o),
        "ipprog" => await IpProg(o),
        "data" => await Data(o),
        "timecode" => await TimeCode(o),
        "trigger" => await Trigger(o),
        "command" => await CommandText(o),
        "diag" => await Diag(o),
        "tod" => await Tod(o),
        "todcontrol" => await TodControl(o),
        "rdm" => await Rdm(o),
        "rdmsub" => await RdmSub(o),
        "nzs" => await Nzs(o),
        "vlc" => await Vlc(o),
        "firmware" => await Firmware(o),
        "serve" => await Serve(o),
        "send" => await SendRaw(o),
        _ => Fail($"Unknown command '{command}'. Run 'artnet-monitor help'."),
    };
}
catch (FormatException ex) { return Fail(ex.Message); }
catch (ArgumentException ex) { return Fail(ex.Message); }
catch (InvalidOperationException ex) { return Fail(ex.Message); }
catch (System.Net.Sockets.SocketException ex) { return Fail($"Network error: {ex.Message} (is another Art-Net application holding UDP 6454 exclusively?)"); }

// ------------------------------------------------------------------ offline commands

static int ShowOptions(Options o)
{
    if (o.Positional.Count == 0)
    {
        Console.WriteLine(ArtNetOptionCatalog.Describe());
        return 0;
    }
    string wanted = string.Join(" ", o.Positional);
    string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    var group = ArtNetOptionCatalog.All.FirstOrDefault(g => Norm(g.Title) == Norm(wanted) || Norm(g.EnumType.Name).EndsWith(Norm(wanted)));
    if (group is null)
        return Fail($"Unknown option group '{wanted}'. Groups: {string.Join(", ", ArtNetOptionCatalog.All.Select(g => g.Title))}");
    Console.WriteLine($"{group.Title} — {group.Description}");
    foreach (var opt in group.Options) Console.WriteLine($"  {opt.Name} ({opt.RawText}) – {opt.Description}");
    return 0;
}

static int ShowInterfaces()
{
    foreach (var nic in ArtNetNetworkInterface.GetAll())
        Console.WriteLine($"{(nic.IsUp ? "UP  " : "DOWN")} {nic.Name,-24} {nic.Address,-16} mask {nic.Mask,-16} broadcast {nic.Broadcast,-16} MAC {nic.MacText}{(nic.IsArtNetNetwork ? "  (Art-Net 2.x/10.x network)" : "")}");
    return 0;
}

static int Decode(Options o)
{
    string hex = string.Concat(o.Positional).Replace(" ", "").Replace("-", "").Replace(":", "");
    if (hex.Length == 0) return Fail("Usage: artnet-monitor decode <hex bytes>");
    byte[] bytes = Convert.FromHexString(hex);
    Console.WriteLine(ArtNetFormatter.HexDump(bytes));
    Console.WriteLine();
    Console.WriteLine(ArtNetPacketParser.TryParse(bytes, out var packet)
        ? ArtNetFormatter.Format(packet)
        : ArtNetPacketParser.IsArtNet(bytes) ? "Art-Net packet shorter than the minimum length for its OpCode." : "Not an Art-Net packet.");
    return 0;
}

static int DefaultIp(Options o)
{
    if (o.Positional.Count < 1) return Fail("Usage: artnet-monitor defaultip <mac> [--oem 0x00FF] [--secondary]");
    var mac = Convert.FromHexString(o.Positional[0].Replace(":", "").Replace("-", ""));
    ushort oem = o.UShort("oem", ArtNetConstants.OemUnknown);
    Console.WriteLine($"{ArtNetNetworkInterface.DefaultArtNetAddress(mac, oem, o.Has("secondary"))} / 255.0.0.0");
    return 0;
}

static int FirmwareInfo(Options o)
{
    if (o.Positional.Count < 1) return Fail("Usage: artnet-monitor firmware-info <file.alf|file.alu>");
    var f = ArtNetFirmwareFile.Load(o.Positional[0]);
    Console.WriteLine($"User name:        {f.UserName}");
    Console.WriteLine($"Firmware version: {ArtNetText.FormatVersion(f.FirmwareVersion)} (0x{f.FirmwareVersion:X4})");
    Console.WriteLine($"Valid OEM codes:  {string.Join(", ", f.OemCodes.Select(x => $"0x{x:X4}"))}");
    Console.WriteLine($"Data:             {f.Data.Length} bytes ({f.DataWords} words), total {f.TotalWords} words");
    Console.WriteLine($"Checksum:         0x{f.Checksum:X4} ({(f.ChecksumValid ? "valid" : $"INVALID, expected 0x{ArtNetFirmwareFile.ComputeChecksum(f.Data):X4}")})");
    Console.WriteLine($"Packets:          {f.ToPackets().Count} × ArtFirmwareMaster");
    return 0;
}

// ------------------------------------------------------------------ monitoring

static async Task<int> Listen(Options o)
{
    await using var node = await StartNode(o, poll: !o.Has("no-poll"));
    var filter = o.List("filter").Select(ParseOpCode).ToHashSet();
    var lastDmx = new Dictionary<PortAddress, DateTime>();
    node.NodeDiscovered += (_, e) => Log(ConsoleColor.Green, $"+ Node: {e.Node}");
    node.NodeUpdated += (_, e) => { if (o.Verbose) Log(ConsoleColor.DarkGreen, $"* Node changed: {e.Node}"); };
    node.NodeLost += (_, e) => Log(ConsoleColor.Yellow, $"- Node lost: {e.Node.DisplayName} {e.Node.AddressText}");
    node.Error += (_, e) => Log(ConsoleColor.Red, $"! {e.Exception.Message}");
    node.PacketReceived += (_, e) =>
    {
        var p = e.Packet;
        if (filter.Count > 0 && !filter.Contains(p.OpCode)) return;
        if (filter.Count == 0 && !o.Verbose && p.OpCode is ArtNetOpCode.Poll or ArtNetOpCode.PollReply) return;
        if (p is ArtDmxPacket dmx && !o.Verbose)
        {
            // Throttle DMX lines per universe.
            lock (lastDmx)
            {
                if (lastDmx.TryGetValue(dmx.PortAddress, out var t) && DateTime.UtcNow - t < TimeSpan.FromMilliseconds(o.Int("interval", 1000))) return;
                lastDmx[dmx.PortAddress] = DateTime.UtcNow;
            }
        }
        if (o.Has("raw")) Console.WriteLine(ArtNetFormatter.HexDump(p.ToArray()));
        if (o.Verbose) Log(ConsoleColor.Gray, $"[{e.RemoteEndPoint}] {ArtNetFormatter.Format(p, includeHeader: o.Has("header"))}");
        else Log(ConsoleColor.Gray, $"[{e.RemoteEndPoint.Address}] {p.Summary}");
    };
    Log(ConsoleColor.Green, $"Listening on UDP {node.Settings.Port} (broadcast {node.BroadcastAddress}). Ctrl+C to stop.");
    await WaitForCancel(o.Seconds);
    return 0;
}

static async Task<int> Nodes(Options o)
{
    await using var node = await StartNode(o, poll: false);
    int seconds = o.Seconds > 0 ? o.Seconds : 3;
    Console.WriteLine($"Polling {node.BroadcastAddress} and waiting {seconds} s…");
    var found = await node.DiscoverAsync(TimeSpan.FromSeconds(seconds), BuildPoll(o));
    if (found.Count == 0) { Console.WriteLine("No nodes replied."); return 1; }
    foreach (var n in found.OrderBy(n => n.Address.ToString()).ThenBy(n => n.BindIndex))
    {
        Console.WriteLine($"{n.DisplayName}  {n.AddressText}  {n.StyleName}");
        Console.WriteLine($"    {n.LongName}");
        Console.WriteLine($"    {n.UniversesText} · report: {n.NodeReportText}");
        Console.WriteLine($"    MAC {n.MacAddress} · OEM {n.OemText} · ESTA {n.EstaText} · firmware {n.FirmwareText}{(n.IsLocal ? " · this machine" : "")}");
        if (o.Verbose) Console.WriteLine(Indent(ArtNetFormatter.Format(n.Reply, includeHeader: false), 4));
    }
    return 0;
}

static async Task<int> Watch(Options o)
{
    if (o.Positional.Count < 1) return Fail("Usage: artnet-monitor watch <universe> [--percent] [--subscribe]");
    var u = PortAddress.Parse(o.Positional[0]);
    await using var node = await StartNode(o, poll: o.Has("subscribe"), configure: s =>
    {
        // Announce an output port so spec-compliant controllers unicast this universe to us.
        if (o.Has("subscribe")) s.Ports.Add(ArtNetPortConfig.Output(u, "Monitor"));
    });
    byte[]? last = null;
    var lastPrint = DateTime.MinValue;
    string info = "";
    node.UniverseChanged += (_, e) =>
    {
        if (e.Address != u) return;
        last = e.Data;
        info = $"{string.Join(" + ", e.Sources)}{(e.Merging ? " (merging)" : "")}{(e.Synchronous ? " (sync)" : "")}";
    };
    Console.WriteLine($"Watching universe {u}. Ctrl+C to stop.");
    var until = o.Seconds > 0 ? DateTime.UtcNow.AddSeconds(o.Seconds) : DateTime.MaxValue;
    using var cts = CancelOnCtrlC();
    while (!cts.IsCancellationRequested && DateTime.UtcNow < until)
    {
        await Task.Delay(250);
        var data = last;
        if (data is null || DateTime.UtcNow - lastPrint < TimeSpan.FromMilliseconds(o.Int("interval", 500))) continue;
        lastPrint = DateTime.UtcNow;
        var uni = node.GetUniverse(u);
        Console.WriteLine($"── {DateTime.Now:HH:mm:ss.fff} · {info} · {uni?.PacketCount} packets, {uni?.DroppedCount} dropped, {uni?.LastLength} ch");
        Console.WriteLine(ArtNetFormatter.DmxGrid(data, o.Has("percent")));
    }
    return 0;
}

static async Task<int> Poll(Options o)
{
    await using var node = await StartNode(o, poll: false);
    var poll = BuildPoll(o);
    node.PollReplyReceived += (_, e) => Log(ConsoleColor.Gray, e.Packet.Summary);
    await node.PollAsync(poll);
    Console.WriteLine(ArtNetFormatter.Format(poll));
    await Task.Delay(TimeSpan.FromSeconds(o.Seconds > 0 ? o.Seconds : 3));
    return 0;
}

// ------------------------------------------------------------------ DMX

static async Task<int> SendDmx(Options o)
{
    if (o.Positional.Count < 1)
        return Fail("Usage: artnet-monitor dmx <universe> [ch=value ...] [--all v] [--seconds n] [--fps 30] [--sync] [--to ip] [--broadcast]");
    var u = PortAddress.Parse(o.Positional[0]);
    var data = new byte[512];
    if (o.Get("all") is { } all) Array.Fill(data, ParseLevel(all));
    foreach (var assignment in o.Positional.Skip(1))
    {
        var parts = assignment.Split('=', 2);
        if (parts.Length != 2) throw new FormatException($"'{assignment}' should look like 1=255, 1-10=50% or 5=FL.");
        byte level = ParseLevel(parts[1]);
        var range = parts[0].Split('-', 2);
        int first = int.Parse(range[0], CultureInfo.InvariantCulture);
        int last = range.Length == 2 ? int.Parse(range[1], CultureInfo.InvariantCulture) : first;
        if (first < 1 || last > 512 || last < first) throw new FormatException($"Channel range '{parts[0]}' must be within 1-512.");
        for (int c = first; c <= last; c++) data[c - 1] = level;
    }

    await using var node = await StartNode(o, poll: true, configure: s =>
    {
        foreach (var t in o.List("to")) s.StaticDmxTargets.Add(new IPEndPoint(IPAddress.Parse(t), s.Port));
        s.BroadcastDmxWithoutSubscribers = o.Has("broadcast");
    });
    await Task.Delay(1500); // let subscribers answer the first ArtPoll
    int seconds = Math.Max(0, o.Seconds);
    int fps = Math.Clamp(o.Int("fps", 30), 1, 44);
    var end = DateTime.UtcNow.AddSeconds(seconds);
    int sent = 0, targets;
    do
    {
        targets = await node.SendDmxAsync(u, data);
        if (o.Has("sync")) await node.SendSyncAsync();
        sent++;
        if (seconds > 0) await Task.Delay(1000 / fps);
    } while (DateTime.UtcNow < end);

    var subs = node.SubscribersOf(u);
    Console.WriteLine($"Sent {sent} ArtDmx frame(s) for universe {u} to {targets} destination(s).");
    foreach (var s in subs) Console.WriteLine($"  subscriber {s.DisplayName} {s.AddressText}");
    if (targets == 0)
        Console.WriteLine("  No node subscribes to this universe (Art-Net 4 forbids broadcast ArtDmx). Use --to <ip> or --broadcast for legacy devices.");
    Console.WriteLine(ArtNetFormatter.DmxGrid(data.AsSpan(0, Math.Max(16, Array.FindLastIndex(data, b => b != 0) + 1))));
    return 0;
}

static async Task<int> Nzs(Options o)
{
    if (o.Positional.Count < 3) return Fail("Usage: artnet-monitor nzs <universe> <start code> <hex data> [--to ip]");
    var packet = new ArtNzsPacket
    {
        PortAddress = PortAddress.Parse(o.Positional[0]),
        StartCode = ParseByte(o.Positional[1]),
        Data = Convert.FromHexString(o.Positional[2].Replace(" ", "")),
    };
    if (packet.StartCode is 0 or ArtNetConstants.RdmStartCode) return Fail("ArtNzs start code must not be 0 or 0xCC (RDM).");
    return await SendToSubscribersOrTarget(o, packet);
}

static async Task<int> Vlc(Options o)
{
    if (o.Positional.Count < 1) return Fail("Usage: artnet-monitor vlc <universe> (--url u | --text t | --location n) [--slot n] [--beacon hz] [--depth %] [--frequency hz] [--to ip]");
    var p = new ArtVlcPacket { PortAddress = PortAddress.Parse(o.Positional[0]), SlotAddress = o.UShort("slot", 0), Depth = (byte)o.Int("depth", 0), Frequency = o.UShort("frequency", 0) };
    if (o.Get("url") is { } url) { p.PayloadLanguage = ArtVlcPayloadLanguage.BeaconUrl; p.PayloadText = url; }
    else if (o.Get("text") is { } text) { p.PayloadLanguage = ArtVlcPayloadLanguage.BeaconText; p.PayloadText = text; }
    else if (o.Get("location") is { } loc) { ushort id = ushort.Parse(loc, CultureInfo.InvariantCulture); p.PayloadLanguage = ArtVlcPayloadLanguage.BeaconLocationId; p.Payload = [(byte)(id >> 8), (byte)id]; }
    if (o.Get("beacon") is { } rep) { p.Flags |= ArtVlcFlags.Beacon; p.BeaconRepeat = ushort.Parse(rep, CultureInfo.InvariantCulture); }
    return await SendToSubscribersOrTarget(o, p);
}

// ------------------------------------------------------------------ configuration

static async Task<int> Address(Options o)
{
    if (o.Positional.Count < 1)
        return Fail("Usage: artnet-monitor address <node> [--name s] [--long s] [--universe u | --net n --sub n --out n --in n] [--port 0-3] [--bind n] [--acn 0-200] [--command \"led locate\"]");
    await using var node = await StartNode(o, poll: false);
    var target = await ResolveNode(node, o.Positional[0], o);
    var p = new ArtAddressPacket
    {
        BindIndex = (byte)o.Int("bind", target.Bind),
        ShortName = o.Get("name") ?? string.Empty,
        LongName = o.Get("long") ?? string.Empty,
        Command = o.Get("command") is { } c ? ArtNetText.Parse<ArtNetAddressCommand>(c) : ArtNetAddressCommand.None,
    };
    int port = o.Int("port", 0);
    if (o.Get("universe") is { } uni)
    {
        var a = PortAddress.Parse(uni);
        if (o.Has("input")) p.SetInputAddress(a, port); else p.SetOutputAddress(a, port);
    }
    if (o.Get("net") is { } net) p.NetSwitch = ArtAddressPacket.Program(int.Parse(net, CultureInfo.InvariantCulture));
    if (o.Get("sub") is { } sub) p.SubSwitch = ArtAddressPacket.Program(int.Parse(sub, CultureInfo.InvariantCulture));
    if (o.Get("out") is { } sw) p.SwOut[port] = ArtAddressPacket.Program(int.Parse(sw, CultureInfo.InvariantCulture));
    if (o.Get("in") is { } si) p.SwIn[port] = ArtAddressPacket.Program(int.Parse(si, CultureInfo.InvariantCulture));
    if (o.Get("acn") is { } acn) p.AcnPriority = byte.Parse(acn, CultureInfo.InvariantCulture);
    if (o.Has("reset-switches")) { p.NetSwitch = 0; p.SubSwitch = 0; p.SwOut[port] = 0; p.SwIn[port] = 0; }

    Console.WriteLine(ArtNetFormatter.Format(p));
    var reply = await node.SendAddressAsync(target.Address, p);
    Console.WriteLine(reply is null ? "No ArtPollReply within the timeout." : "Node replied:\n" + ArtNetFormatter.Format(reply, includeHeader: false));
    return reply is null ? 1 : 0;
}

static async Task<int> Input(Options o)
{
    if (o.Positional.Count < 1) return Fail("Usage: artnet-monitor input <node> [--disable 1,3] [--bind n] [--ports 4]");
    await using var node = await StartNode(o, poll: false);
    var target = await ResolveNode(node, o.Positional[0], o);
    var p = new ArtInputPacket { BindIndex = (byte)o.Int("bind", target.Bind), NumPorts = (ushort)o.Int("ports", 1) };
    foreach (var d in o.List("disable")) p.SetDisabled(int.Parse(d, CultureInfo.InvariantCulture) - 1, true);
    Console.WriteLine(ArtNetFormatter.Format(p));
    var reply = await node.SendInputAsync(target.Address, p);
    Console.WriteLine(reply is null ? "No ArtPollReply within the timeout." : "Node replied:\n" + ArtNetFormatter.Format(reply, includeHeader: false));
    return 0;
}

static async Task<int> IpProg(Options o)
{
    if (o.Positional.Count < 1) return Fail("Usage: artnet-monitor ipprog <node> [--ip a.b.c.d --mask m [--gateway g] | --dhcp | --reset]");
    await using var node = await StartNode(o, poll: false);
    var target = await ResolveNode(node, o.Positional[0], o);
    ArtIpProgPacket p =
        o.Has("dhcp") ? ArtIpProgPacket.EnableDhcp()
        : o.Has("reset") ? ArtIpProgPacket.ResetToDefault()
        : o.Get("ip") is { } ip ? ArtIpProgPacket.Program(IPAddress.Parse(ip), IPAddress.Parse(o.Get("mask") ?? "255.0.0.0"), o.Get("gateway") is { } g ? IPAddress.Parse(g) : null)
        : ArtIpProgPacket.Enquiry();
    Console.WriteLine(ArtNetFormatter.Format(p));
    var reply = await node.SendIpProgAsync(target.Address, p);
    Console.WriteLine(reply is null ? "No ArtIpProgReply (the node may not support remote IP programming)." : ArtNetFormatter.Format(reply));
    return reply is null ? 1 : 0;
}

static async Task<int> Data(Options o)
{
    if (o.Positional.Count < 1) return Fail("Usage: artnet-monitor data <node> [poll|product url|user guide url|support url|udr personality url|gdtf personality url|0x8000…]");
    await using var node = await StartNode(o, poll: false);
    var target = await ResolveNode(node, o.Positional[0], o);
    var codes = o.Positional.Count > 1
        ? new[] { (ushort)ArtNetText.Parse<ArtNetDataRequestCode>(string.Join(" ", o.Positional.Skip(1))) }
        : Enum.GetValues<ArtNetDataRequestCode>().Where(c => c != ArtNetDataRequestCode.ManufacturerSpecific).Select(c => (ushort)c).ToArray();
    foreach (var code in codes)
    {
        var reply = await node.RequestDataAsync(target.Address, code, TimeSpan.FromSeconds(o.Int("timeout", 2)));
        Console.WriteLine($"{ArtNetText.FormatDataRequest(code),-24} {(reply is null ? "(no reply)" : reply.Payload.Length == 0 ? "(supported, empty)" : reply.PayloadText)}");
    }
    return 0;
}

// ------------------------------------------------------------------ show control

static async Task<int> TimeCode(Options o)
{
    var type = o.Get("type") is { } t ? ArtNetText.Parse<ArtNetTimeCodeType>(t) : ArtNetTimeCodeType.Smpte;
    var start = o.Positional.Count > 0 ? ParseTimecode(o.Positional[0]) : TimeSpan.Zero;
    var p = ArtTimeCodePacket.FromTimeSpan(start, type, (byte)o.Int("stream", 0));
    await using var node = await StartNode(o, poll: false);
    IPAddress? to = o.Get("to") is { } ip ? IPAddress.Parse(ip) : null;
    int seconds = o.Seconds;
    if (seconds <= 0)
    {
        await node.SendTimeCodeAsync(p, to);
        Console.WriteLine(p.Summary);
        return 0;
    }
    Console.WriteLine($"Running {type.ToDisplayName()} time code from {p.TimeText} for {seconds} s…");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    long frame = 0;
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        await node.SendTimeCodeAsync(p, to);
        if (frame % p.Type.NominalFrames() == 0) Console.Write($"\r{p.TimeText}   ");
        p.Increment();
        frame++;
        var due = TimeSpan.FromSeconds(frame / p.FrameRate) - sw.Elapsed;
        if (due > TimeSpan.Zero) await Task.Delay(due);
    }
    Console.WriteLine();
    return 0;
}

static async Task<int> Trigger(Options o)
{
    if (o.Positional.Count < 2) return Fail("Usage: artnet-monitor trigger <key: ascii|macro|soft|show|0-255> <sub-key> [--oem 0xFFFF] [--data hex] [--to ip]");
    ushort oem = o.UShort("oem", ArtNetConstants.Global);
    byte key = oem == ArtNetConstants.Global ? (byte)ArtNetText.Parse<ArtNetTriggerKey>(o.Positional[0]) : ParseByte(o.Positional[0]);
    byte sub = key == (byte)ArtNetTriggerKey.Ascii && o.Positional[1].Length == 1 && !char.IsDigit(o.Positional[1][0]) ? (byte)o.Positional[1][0] : ParseByte(o.Positional[1]);
    var p = new ArtTriggerPacket { Oem = oem, Key = key, SubKey = sub };
    if (o.Get("data") is { } hex) p.SetData(Convert.FromHexString(hex.Replace(" ", "")));
    await using var node = await StartNode(o, poll: false);
    await node.SendTriggerAsync(p, o.Get("to") is { } ip ? IPAddress.Parse(ip) : null);
    Console.WriteLine(p.Summary);
    return 0;
}

static async Task<int> CommandText(Options o)
{
    if (o.Positional.Count < 1) return Fail("Usage: artnet-monitor command \"SwoutText=Playback&\" [--esta 0xFFFF] [--to ip]");
    var p = new ArtCommandPacket { Text = string.Join(" ", o.Positional), EstaManufacturer = o.UShort("esta", ArtNetConstants.Global) };
    await using var node = await StartNode(o, poll: false);
    await node.SendCommandAsync(p, o.Get("to") is { } ip ? IPAddress.Parse(ip) : null);
    Console.WriteLine(ArtNetFormatter.Format(p));
    return 0;
}

static async Task<int> Diag(Options o)
{
    if (o.Positional.Count < 1) return Fail("Usage: artnet-monitor diag <text> [--priority low|medium|high|critical|volatile] [--port n] [--to ip]");
    var p = new ArtDiagDataPacket
    {
        Text = string.Join(" ", o.Positional),
        Priority = o.Get("priority") is { } pr ? ArtNetText.Parse<ArtNetDiagnosticPriority>(pr) : ArtNetDiagnosticPriority.Low,
        LogicalPort = (byte)o.Int("port", 0),
    };
    await using var node = await StartNode(o, poll: false);
    if (o.Get("to") is { } ip) await node.SendAsync(p, IPAddress.Parse(ip));
    else await node.BroadcastAsync(p);
    Console.WriteLine(p.Summary);
    return 0;
}

// ------------------------------------------------------------------ RDM

static async Task<int> Tod(Options o)
{
    if (o.Positional.Count < 1) return Fail("Usage: artnet-monitor tod <universe> [universe …] [--seconds 3]");
    var addresses = o.Positional.Select(PortAddress.Parse).ToArray();
    await using var node = await StartNode(o, poll: false);
    var packets = await node.RequestTodAsync(addresses, TimeSpan.FromSeconds(o.Seconds > 0 ? o.Seconds : 3));
    if (packets.Count == 0) { Console.WriteLine("No ArtTodData received."); return 1; }
    foreach (var p in packets.Where(p => p.CommandResponse == ArtNetTodDataCommand.TodNak))
        Console.WriteLine($"Universe {p.PortAddress}: TOD not available (discovery incomplete).");
    foreach (var (address, uids) in ArtNetNode.MergeTod(packets))
    {
        Console.WriteLine($"Universe {address}: {uids.Count} RDM device(s)");
        foreach (var uid in uids) Console.WriteLine($"  {uid}");
    }
    return 0;
}

static async Task<int> TodControl(Options o)
{
    if (o.Positional.Count < 3) return Fail("Usage: artnet-monitor todcontrol <node> <universe> <flush|end|incremental on|incremental off>");
    await using var node = await StartNode(o, poll: false);
    var target = await ResolveNode(node, o.Positional[0], o);
    var cmd = ArtNetText.Parse<ArtNetTodControlCommand>(string.Join(" ", o.Positional.Skip(2)));
    var reply = await node.SendTodControlAsync(target.Address, PortAddress.Parse(o.Positional[1]), cmd, TimeSpan.FromSeconds(o.Int("timeout", 5)));
    Console.WriteLine(reply is null ? "No ArtTodData within the timeout." : ArtNetFormatter.Format(reply));
    return 0;
}

static async Task<int> Rdm(Options o)
{
    if (o.Positional.Count < 5) return Fail("Usage: artnet-monitor rdm <node> <universe> <uid MMMM:DDDDDDDD> <get|set> <pid name|number> [hex data] [--sub n]");
    await using var node = await StartNode(o, poll: false);
    var target = await ResolveNode(node, o.Positional[0], o);
    var universe = PortAddress.Parse(o.Positional[1]);
    var uid = RdmUid.Parse(o.Positional[2]);
    var cc = ArtNetText.Parse<RdmCommandClass>(o.Positional[3]);
    if (!RdmText.TryParseParameter(o.Positional[4], out var pid)) return Fail($"Unknown parameter '{o.Positional[4]}'.");
    byte[] pd = o.Positional.Count > 5 ? Convert.FromHexString(o.Positional[5].Replace(" ", "")) : [];
    var source = new RdmUid(ArtNetConstants.EstaPrototype, (uint)Random.Shared.Next());
    var msg = RdmMessage.Build(uid, source, (byte)Random.Shared.Next(256), 1, o.UShort("sub", 0), cc, pid, pd);
    RdmMessage.TryParse(msg, out var request);
    Console.WriteLine($"→ {request.Summary}");
    var reply = await node.SendRdmAsync(target.Address, universe, msg, TimeSpan.FromSeconds(o.Int("timeout", 3)));
    Console.WriteLine(reply is null ? "No RDM response within the timeout." : ArtNetFormatter.Format(reply, includeHeader: false));
    return reply is null ? 1 : 0;
}

static async Task<int> RdmSub(Options o)
{
    if (o.Positional.Count < 5) return Fail("Usage: artnet-monitor rdmsub <node> <uid> <get|set> <pid> <first sub-device> [count] [values…]");
    await using var node = await StartNode(o, poll: false);
    var target = await ResolveNode(node, o.Positional[0], o);
    if (!RdmText.TryParseParameter(o.Positional[3], out var pid)) return Fail($"Unknown parameter '{o.Positional[3]}'.");
    var p = new ArtRdmSubPacket
    {
        Uid = RdmUid.Parse(o.Positional[1]),
        CommandClass = ArtNetText.Parse<RdmCommandClass>(o.Positional[2]),
        ParameterId = pid,
        SubDevice = ushort.Parse(o.Positional[4], CultureInfo.InvariantCulture),
        SubCount = o.Positional.Count > 5 ? ushort.Parse(o.Positional[5], CultureInfo.InvariantCulture) : (ushort)1,
    };
    p.Values.AddRange(o.Positional.Skip(6).Select(v => ushort.Parse(v, CultureInfo.InvariantCulture)));
    var tcs = new TaskCompletionSource<ArtRdmSubPacket>();
    node.RdmSubReceived += (_, e) => { if (e.RemoteEndPoint.Address.Equals(target.Address) && e.Packet.Uid == p.Uid) tcs.TrySetResult(e.Packet); };
    await node.SendRdmSubAsync(target.Address, p);
    Console.WriteLine(ArtNetFormatter.Format(p));
    try { Console.WriteLine(ArtNetFormatter.Format(await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3)))); }
    catch (TimeoutException) { Console.WriteLine("No ArtRdmSub response."); }
    return 0;
}

// ------------------------------------------------------------------ firmware

static async Task<int> Firmware(Options o)
{
    if (o.Positional.Count < 2) return Fail("Usage: artnet-monitor firmware <node> <file.alf|file.alu> [--ubea] [--force]");
    var file = ArtNetFirmwareFile.Load(o.Positional[1]);
    bool ubea = o.Has("ubea") || o.Positional[1].EndsWith(".alu", StringComparison.OrdinalIgnoreCase);
    await using var node = await StartNode(o, poll: false);
    var target = await ResolveNode(node, o.Positional[0], o);
    Console.WriteLine($"Uploading {file} to {target.Address}…");
    var progress = new Progress<ArtNetFirmwareProgress>(p => Console.Write($"\r{p}          "));
    var result = await node.UploadFirmwareAsync(target.Address, file, ubea, progress, o.Has("force") ? null : target.Node);
    Console.WriteLine();
    Console.WriteLine($"Result: {result.ToDisplayName()} – {result.ToDescription()}");
    return result == ArtNetFirmwareReplyType.Fail ? 1 : 0;
}

// ------------------------------------------------------------------ acting as a node

static async Task<int> Serve(Options o)
{
    await using var node = await StartNode(o, poll: !o.Has("no-poll"), configure: s =>
    {
        s.Style = o.Get("style") is { } st ? ArtNetText.Parse<ArtNetStyle>(st) : ArtNetStyle.Node;
        foreach (var u in o.List("out")) s.Ports.Add(ArtNetPortConfig.Output(PortAddress.Parse(u)));
        foreach (var u in o.List("in")) s.Ports.Add(ArtNetPortConfig.Input(PortAddress.Parse(u)));
        if (o.Get("url") is { } url) s.DataReplies[ArtNetDataRequestCode.UrlProduct] = url;
        if (o.Has("ltp")) s.DefaultMergeMode = ArtNetMergeMode.Ltp;
    });
    Console.WriteLine($"Serving as \"{node.Settings.ShortName}\" ({node.Settings.Style.ToDisplayName()}) with {node.Settings.Ports.Count} port(s):");
    foreach (var r in node.CreatePollReplies()) Console.WriteLine("  " + r.Summary);
    var lastPrint = new Dictionary<PortAddress, DateTime>();
    node.UniverseChanged += (_, e) =>
    {
        lock (lastPrint)
        {
            if (lastPrint.TryGetValue(e.Address, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(1)) return;
            lastPrint[e.Address] = DateTime.UtcNow;
        }
        int active = e.Data.Count(b => b != 0);
        Log(ConsoleColor.Cyan, $"Universe {e.Address} from {string.Join(" + ", e.Sources)}{(e.Merging ? " MERGING" : "")}{(e.Synchronous ? " SYNC" : "")}: {active} channels non-zero · ch1-8 {string.Join(" ", e.Data.Take(8))}");
    };
    node.AddressReceived += (_, e) => Log(ConsoleColor.Yellow, $"ArtAddress from {e.RemoteEndPoint.Address}: {e.Packet.Summary}");
    node.InputReceived += (_, e) => Log(ConsoleColor.Yellow, $"ArtInput from {e.RemoteEndPoint.Address}: {e.Packet.Summary}");
    node.ConfigurationChanged += (_, _) => Log(ConsoleColor.Yellow, $"Configuration now: {node.Settings.ShortName} / {node.Settings.LongName} · {string.Join(", ", node.Settings.Ports)}");
    node.PollReceived += (_, e) => { if (o.Verbose) Log(ConsoleColor.DarkGray, $"ArtPoll from {e.RemoteEndPoint.Address}: {e.Packet.Summary}"); };
    node.Error += (_, e) => Log(ConsoleColor.Red, $"! {e.Exception.Message}");
    await WaitForCancel(o.Seconds);
    return 0;
}

static async Task<int> SendRaw(Options o)
{
    if (o.Positional.Count < 2) return Fail("Usage: artnet-monitor send <ip|broadcast> <hex datagram>");
    var bytes = Convert.FromHexString(string.Concat(o.Positional.Skip(1)).Replace(" ", ""));
    await using var node = await StartNode(o, poll: false);
    var target = o.Positional[0] == "broadcast" ? node.BroadcastAddress : IPAddress.Parse(o.Positional[0]);
    await node.SendRawAsync(bytes, new IPEndPoint(target, node.Settings.Port));
    Console.WriteLine(ArtNetPacketParser.TryParse(bytes, out var p) ? $"Sent {p.Summary} to {target}" : $"Sent {bytes.Length} raw bytes to {target}");
    return 0;
}

static async Task<int> Simple(Options o, Func<ArtNetNode, ValueTask> action, string done)
{
    await using var node = await StartNode(o, poll: false);
    await action(node);
    Console.WriteLine(done);
    return 0;
}

static async Task<int> SendToSubscribersOrTarget(Options o, ArtNzsPacket packet)
{
    await using var node = await StartNode(o, poll: o.Get("to") is null);
    int n;
    if (o.Get("to") is { } ip) { await node.SendAsync(packet, IPAddress.Parse(ip)); n = 1; }
    else { await Task.Delay(1500); n = await node.SendNzsAsync(packet); }
    Console.WriteLine($"{packet.Summary} → {n} destination(s)");
    Console.WriteLine(ArtNetFormatter.Format(packet, includeHeader: false));
    return 0;
}

// ------------------------------------------------------------------ helpers

static async Task<ArtNetNode> StartNode(Options o, bool poll, Action<ArtNetNodeSettings>? configure = null)
{
    var s = new ArtNetNodeSettings
    {
        ShortName = o.Get("name") is { } n && configure is not null ? n : "artnet-monitor",
        LongName = "artnet-monitor (ArtNet.NET command line utility)",
        Style = ArtNetStyle.Config,
        SendPolls = poll,
        MaxReplyDelay = TimeSpan.FromMilliseconds(100),
    };
    if (o.Get("interface") is { } iface)
    {
        var local = IPAddress.Parse(iface);
        s.LocalAddress = local;
        s.BroadcastAddress = ArtNetNetworkInterface.Find(local)?.Broadcast;
    }
    if (o.Get("broadcast-address") is { } b) s.BroadcastAddress = IPAddress.Parse(b);
    configure?.Invoke(s);
    var node = new ArtNetNode(s);
    await node.StartAsync();
    return node;
}

static ArtPollPacket BuildPoll(Options o)
{
    var flags = ArtPollFlags.None;
    if (o.Has("diag")) flags |= ArtPollFlags.Diagnostics;
    if (o.Has("diag-unicast")) flags |= ArtPollFlags.Diagnostics | ArtPollFlags.DiagnosticsUnicast;
    if (o.Has("on-change")) flags |= ArtPollFlags.ReplyOnChange;
    if (o.Has("no-vlc")) flags |= ArtPollFlags.DisableVlc;
    var poll = o.Get("target") is { } range
        ? ArtPollPacket.Targeted(PortAddress.Parse(range.Split('-')[0]), PortAddress.Parse(range.Split('-').Last()), flags)
        : new ArtPollPacket { Flags = flags };
    if (o.Get("priority") is { } p) poll.DiagPriority = ArtNetText.Parse<ArtNetDiagnosticPriority>(p);
    poll.EstaManufacturer = ArtNetConstants.EstaPrototype;
    poll.Oem = ArtNetConstants.OemUnknown;
    return poll;
}

static async Task<(IPAddress Address, byte Bind, ArtNetRemoteNode? Node)> ResolveNode(ArtNetNode node, string text, Options o)
{
    byte bind = 1;
    var t = text;
    int hash = t.LastIndexOf('#');
    if (hash > 0) { bind = byte.Parse(t[(hash + 1)..], CultureInfo.InvariantCulture); t = t[..hash]; }
    if (IPAddress.TryParse(t, out var ip) && !o.Has("check")) return (ip, bind, null);
    var found = await node.DiscoverAsync(TimeSpan.FromSeconds(2));
    var match = found.FirstOrDefault(n => (ip is not null && n.Address.Equals(ip) && n.BindIndex == bind) ||
                                          n.ShortName.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                                          n.LongName.Equals(t, StringComparison.OrdinalIgnoreCase))
                ?? found.FirstOrDefault(n => n.ShortName.Contains(t, StringComparison.OrdinalIgnoreCase));
    if (match is null) throw new ArgumentException($"No node named or at '{text}' replied. Found: {string.Join(", ", found.Select(n => $"{n.DisplayName} ({n.AddressText})"))}");
    return (match.Address, hash > 0 ? bind : match.BindIndex, match);
}

static ArtNetOpCode ParseOpCode(string s)
{
    var name = s.Trim();
    if (!name.StartsWith("art", StringComparison.OrdinalIgnoreCase)) name = "Art" + name;
    return ArtNetText.Parse<ArtNetOpCode>(name);
}

static byte ParseLevel(string s)
{
    s = s.Trim();
    if (s.Equals("FL", StringComparison.OrdinalIgnoreCase) || s.Equals("full", StringComparison.OrdinalIgnoreCase)) return 255;
    if (s.EndsWith('%')) return (byte)Math.Round(Math.Clamp(double.Parse(s[..^1], CultureInfo.InvariantCulture), 0, 100) * 2.55);
    return ParseByte(s);
}

static byte ParseByte(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
    ? byte.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
    : byte.Parse(s, CultureInfo.InvariantCulture);

static TimeSpan ParseTimecode(string s)
{
    var p = s.Split(':', ';', '.');
    if (p.Length != 4) throw new FormatException("Time code must be HH:MM:SS:FF.");
    int[] v = p.Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
    return new TimeSpan(0, v[0], v[1], v[2]) + TimeSpan.FromSeconds(v[3] / 30.0);
}

static string Indent(string text, int n) => string.Join(Environment.NewLine, text.Split('\n').Select(l => new string(' ', n) + l.TrimEnd('\r')));

static CancellationTokenSource CancelOnCtrlC()
{
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    return cts;
}

static async Task WaitForCancel(int seconds)
{
    using var cts = CancelOnCtrlC();
    try { await Task.Delay(seconds > 0 ? TimeSpan.FromSeconds(seconds) : Timeout.InfiniteTimeSpan, cts.Token); }
    catch (OperationCanceledException) { }
}

static void Log(ConsoleColor color, string text)
{
    lock (Console.Out)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
        Console.ForegroundColor = old;
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static void PrintHelp() => Console.WriteLine("""
    artnet-monitor – Art-Net 4 utility (protocol 14). Every packet type can be sent and decoded in readable form.

    Offline
      options [group]                         every protocol option with names, raw values and descriptions
      interfaces                              IPv4 interfaces, broadcast addresses, MACs
      decode <hex>                            decode any Art-Net datagram
      defaultip <mac> [--oem 0x00FF] [--secondary]   factory default 2.x / 10.x address
      firmware-info <file.alf>                inspect a firmware / UBEA file

    Discovery and monitoring
      listen [--filter dmx,pollreply,…] [-v] [--raw] [--header] [--interval ms] [--no-poll]
      nodes [--seconds 3] [-v] [--target 1-20] [--diag] [--on-change]
      poll [--target 1-20] [--diag|--diag-unicast] [--priority high] [--on-change] [--no-vlc]
      watch <universe> [--percent] [--subscribe] [--interval ms]

    DMX
      dmx <universe> [ch=value …] [--all v] [--seconds n] [--fps 30] [--sync] [--to ip] [--broadcast]
            values: 0-255, 0x80, 50%, FL; ranges: 1-12=FL
      sync                                    broadcast ArtSync
      nzs <universe> <start code> <hex> [--to ip]
      vlc <universe> --url u | --text t | --location n [--slot n] [--beacon hz] [--to ip]

    Node configuration   (<node> = IP, IP#bind, or short/long name)
      address <node> [--name s] [--long s] [--universe u [--input]] [--net n] [--sub n] [--out n] [--in n]
                     [--port 0-3] [--bind n] [--acn 0-200] [--command "led locate"] [--reset-switches]
      input <node> [--disable 1,3] [--bind n] [--ports 4]
      ipprog <node> [--ip a.b.c.d --mask m [--gateway g] | --dhcp | --reset]
      data <node> [request]                   ArtDataRequest (all standard requests when omitted)
      firmware <node> <file.alf|.alu> [--ubea] [--force]

    Show control
      timecode [HH:MM:SS:FF] [--type smpte|ebu|film|df] [--seconds n] [--stream n] [--to ip]
      trigger <ascii|macro|soft|show|n> <sub-key> [--oem 0xFFFF] [--data hex] [--to ip]
      command "SwoutText=Playback&" [--esta 0xFFFF] [--to ip]
      diag <text> [--priority high] [--port n] [--to ip]

    RDM
      tod <universe …> [--seconds 3]          ArtTodRequest, prints the merged table of devices
      todcontrol <node> <universe> <flush|end|incremental on|incremental off>
      rdm <node> <universe> <uid> <get|set> <pid> [hex data] [--sub n]
      rdmsub <node> <uid> <get|set> <pid> <first sub-device> [count] [values …]

    Acting as a node
      serve [--out 1,2] [--in 3] [--name s] [--style node] [--url https://…] [--ltp] [-v]

    Raw
      send <ip|broadcast> <hex datagram>

    Common: --interface <local ip>, --broadcast-address <ip>, --seconds n, -v
    """);

sealed class Options
{
    public List<string> Positional { get; } = [];
    private readonly Dictionary<string, string?> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(List<string> args)
    {
        var o = new Options();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-v" or "--verbose") { o._flags["verbose"] = null; continue; }
            if (a.StartsWith("--"))
            {
                var key = a[2..];
                string? value = null;
                int eq = key.IndexOf('=');
                if (eq >= 0) { value = key[(eq + 1)..]; key = key[..eq]; }
                else if (i + 1 < args.Count && !args[i + 1].StartsWith("--") && !IsSwitch(key)) value = args[++i];
                o._flags[key] = value;
                continue;
            }
            o.Positional.Add(a);
        }
        return o;
    }

    private static readonly HashSet<string> Switches = new(StringComparer.OrdinalIgnoreCase)
    {
        "raw", "header", "no-poll", "percent", "subscribe", "sync", "broadcast", "input", "reset-switches", "dhcp", "reset",
        "ubea", "force", "ltp", "diag", "diag-unicast", "on-change", "no-vlc", "secondary", "check",
    };

    private static bool IsSwitch(string key) => Switches.Contains(key);

    public bool Has(string key) => _flags.ContainsKey(key);
    public string? Get(string key) => _flags.TryGetValue(key, out var v) ? v : null;
    public bool Verbose => Has("verbose");
    public int Seconds => Int("seconds", 0);
    public int Int(string key, int fallback) => Get(key) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    public ushort UShort(string key, ushort fallback) => Get(key) is { } v
        ? v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.Parse(v.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ushort.Parse(v, CultureInfo.InvariantCulture)
        : fallback;

    public IReadOnlyList<string> List(string key) =>
        Get(key)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}
