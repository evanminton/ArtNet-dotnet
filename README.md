# Art-Net 4 for .NET 10 / .NET MAUI

A dependency-free .NET 10 implementation of **Art-Net 4** (protocol revision 14, specification document revision 1.4dp, 23/10/2025): every packet in the spec, controller and node networking, DMX merge (HTP/LTP) and ArtSync, RDM transport, firmware upload, and a human-readable layer over every protocol option. Ships with `artnet-monitor`, a command-line utility, and **Art-Net Monitor**, a .NET MAUI app. Both expose every packet type in readable form.

Art-Net™ Designed by and Copyright Artistic Licence.

```
ArtNet.slnx                   everything (the MAUI app needs the MAUI workload)
├─ src/ArtNet                 library (net10.0, AOT/trim compatible, no NuGet dependencies)
│  ├─ Protocol/               constants, enums (Tables 1-7), PortAddress
│  ├─ Packets/                codec for every packet, parser, formatter
│  ├─ Rdm/                    RdmUid, RDM message builder/decoder, PID names
│  ├─ Firmware/               .alf / .alu file format, checksum, block splitting
│  ├─ Text/                   readable names + descriptions for every value, option catalog
│  └─ Networking/             ArtNetNode (UDP 6454), settings, remote nodes, universes (merge/sync)
├─ tools/ArtNet.Monitor       artnet-monitor CLI (dotnet tool)
├─ tests/ArtNet.Tests         xUnit tests (byte offsets from the spec, round trips, merge, sync, programming)
├─ samples/ArtNet.Maui        Art-Net Monitor – .NET MAUI app (Windows, Android, iOS, Mac Catalyst)
└─ build.cmd                  Debug + Release build of everything, tests, log in artifacts\build-log.txt
```

## Build

```powershell
.\build.cmd                    # Debug and Release: library, CLI, tests, MAUI Windows app
.\build.cmd Release            # one configuration
.\build-android.cmd            # also the Android app (dotnet workload install maui-android first)
dotnet test  tests/ArtNet.Tests -c Release
dotnet run   --project tools/ArtNet.Monitor -- nodes
dotnet build samples/ArtNet.Maui -c Release -f net10.0-windows10.0.19041.0   # needs: dotnet workload install maui
dotnet build samples/ArtNet.Maui -c Release -f net10.0-android -p:IncludeAndroid=true
dotnet pack  src/ArtNet -c Release
```

## Packets covered

| OpCode | Packet | Class | Size |
|---|---|---|---|
| 0x2000 | ArtPoll (targeted mode, diagnostics flags) | `ArtPollPacket` | 22 (accepts ≥ 14) |
| 0x2100 | ArtPollReply (Status1/2/3, GoodInput/OutputA/B, binds) | `ArtPollReplyPacket` | 239 (accepts ≥ 207) |
| 0xF800 / 0xF900 | ArtIpProg / ArtIpProgReply | `ArtIpProgPacket`, `ArtIpProgReplyPacket` | 34 |
| 0x6000 | ArtAddress (names, switches, every command) | `ArtAddressPacket` | 107 |
| 0x7000 | ArtInput | `ArtInputPacket` | 20 |
| 0x2700 / 0x2800 | ArtDataRequest / ArtDataReply | `ArtDataRequestPacket`, `ArtDataReplyPacket` | 40 / 20 + n |
| 0x2300 | ArtDiagData | `ArtDiagDataPacket` | 18 + n |
| 0x9700 | ArtTimeCode | `ArtTimeCodePacket` | 19 |
| 0x2400 | ArtCommand | `ArtCommandPacket` | 16 + n |
| 0x9900 | ArtTrigger | `ArtTriggerPacket` | 530 |
| 0x5000 | ArtDmx | `ArtDmxPacket` | 18 + 2…512 |
| 0x5200 | ArtSync | `ArtSyncPacket` | 14 |
| 0x5100 | ArtNzs / ArtVlc (start code 0x91 + magic) | `ArtNzsPacket`, `ArtVlcPacket` | 18 + n |
| 0xF200 / 0xF300 | ArtFirmwareMaster / ArtFirmwareReply | `ArtFirmwareMasterPacket`, `ArtFirmwareReplyPacket` | 1064 / 36 |
| 0x8000 | ArtTodRequest | `ArtTodRequestPacket` | 56 |
| 0x8100 | ArtTodData (split at 200 UIDs) | `ArtTodDataPacket` | 28 + 6n |
| 0x8200 | ArtTodControl | `ArtTodControlPacket` | 24 |
| 0x8300 | ArtRdm (+ decoded E1.20 message) | `ArtRdmPacket`, `RdmMessage` | 24 + n |
| 0x8400 | ArtRdmSub | `ArtRdmSubPacket` | 32 + 2n |
| others | media, video, file, directory, time sync | `ArtUnknownPacket` (raw body kept) | – |

```csharp
byte[] bytes = packet.ToArray();                         // encode
if (ArtNetPacketParser.TryParse(datagram, out var p))    // decode (min length per OpCode, missing fields = 0)
    Console.WriteLine(p);                                // readable multi-line dump
```

## Human-readable everything

```csharp
ArtNetAddressCommand.LedLocate.ToDisplayName();          // "LED Locate"
ArtNetAddressCommand.LedLocate.ToDescription();          // "Rapid flashing of the front panel indicators to identify the node."
(ArtPollFlags.ReplyOnChange | ArtPollFlags.Diagnostics).ToDisplayName();   // "Reply On Change, Send Diagnostics"
ArtNetText.Parse<ArtNetAddressCommand>("led locate");    // display name, identifier, decimal or 0x-hex
new PortAddress(1, 2, 3).ToString();                     // "291 (1:2:3)";  PortAddress.Parse("1:2:3")
packet.Describe();                                       // ArtNetField(Section, Name, Value, Raw) for every field
ArtNetOptionCatalog.All;                                 // every option group, ready for pickers
ArtNetOptionCatalog.Describe();                          // plain-text reference incl. packet table and RDM PIDs
```

## Networking

```csharp
await using var node = new ArtNetNode(new ArtNetNodeSettings
{
    ShortName = "My App",
    Style = ArtNetStyle.Controller,
    Ports = [ArtNetPortConfig.Output(new PortAddress(1))],   // subscribe: controllers unicast universe 1 to us
});
node.NodeDiscovered  += (_, e) => Console.WriteLine(e.Node);
node.UniverseChanged += (_, e) => Console.WriteLine($"{e.Address}: ch1 = {e.Data[0]}");
await node.StartAsync();

await node.SendDmxAsync(new PortAddress(1), levels);      // unicast to subscribers, kept alive every 900 ms
await node.SendSyncAsync();
var reply = await node.SendAddressAsync(ip, ArtAddressPacket.ForCommand(ArtNetAddressCommand.LedLocate));
var ipcfg = await node.SendIpProgAsync(ip, ArtIpProgPacket.Enquiry());
var url   = await node.RequestDataAsync(ip, ArtNetDataRequestCode.UrlProduct);
var tod   = ArtNetNode.MergeTod(await node.RequestTodAsync([new PortAddress(1)]));
var rdm   = await node.SendRdmAsync(ip, new PortAddress(1), RdmMessage.Build(uid, myUid, 1, 1, 0, RdmCommandClass.Get, 0x0060));
await node.UploadFirmwareAsync(ip, ArtNetFirmwareFile.Load("fw.alf"), progress: new Progress<ArtNetFirmwareProgress>(Console.WriteLine));
```

What `ArtNetNode` does for you, per the spec:

- binds UDP 6454 (address reuse) and polls the directed broadcast address every 2.5 s; tracks devices per (IP, BindIndex), drops silent ones;
- answers ArtPoll with one ArtPollReply per configured port (bind index 1, 2, …) after a random delay, honours targeted mode, "reply on change" and diagnostics subscriptions (`SendDiagnosticAsync` unicasts or broadcasts as the spec requires);
- NodeReport `#xxxx [yyyy] text` with the rolling counter;
- ArtDmx: unicast to subscribers only (optional non-compliant broadcast fallback and static targets), sequence 1-255, keep-alive re-transmit;
- receive: per-universe merge of two sources (HTP / LTP, keyed by IP + Physical, third source ignored, 10 s hold), out-of-order sequence rejection, AcCancelMerge takeover, ArtSync synchronous mode with the 4 s timeout and source/merge checks;
- as a node: applies ArtAddress (names, Net/Sub/Sw, sACN priority, LED, failsafe, merge, direction, sACN/Art-Net select, clear output, output style, RDM enable) and ArtInput, answers ArtDataRequest;
- request/response helpers with timeouts for ArtAddress, ArtInput, ArtIpProg, ArtDataRequest, ArtTodControl, ArtRdm and firmware blocks (30 s).

`ArtNetNetworkInterface.DefaultArtNetAddress(mac, oem)` computes the factory 2.x / 10.x address (spec example: MAC 12:45:78:98:34:76, OEM 0x0010 → 2.168.52.118).

## artnet-monitor

```
artnet-monitor options ["Address Command"]     every option, human readable
artnet-monitor nodes -v                        poll and print every ArtPollReply field
artnet-monitor listen --filter dmx,timecode -v
artnet-monitor watch 1 --subscribe --percent   live 512-channel grid of a universe
artnet-monitor dmx 1 1-12=FL 13=50% --seconds 10 --sync
artnet-monitor address "Stage Left" --universe 0:1:2 --name "SL" --command "led locate"
artnet-monitor ipprog 2.1.2.3 --ip 2.1.2.10 --mask 255.0.0.0
artnet-monitor data 2.1.2.3
artnet-monitor tod 1 2 3
artnet-monitor rdm 2.1.2.3 1 4142:00000102 get device_info
artnet-monitor timecode 01:00:00:00 --type ebu --seconds 30
artnet-monitor trigger macro 5
artnet-monitor serve --out 1,2 --in 3 --url https://example.com
artnet-monitor decode 4172742D4E6574000020000E...
```

Run `artnet-monitor help` for every command and option.

## Art-Net Monitor (MAUI app)

| Tab | What it shows |
|---|---|
| **Nodes** | Every device answering ArtPoll (name, style, IP/bind, universes, node report); locate / normal LEDs for all. Tap a node for every ArtPollReply field and: LED / merge / every ArtAddress command, program names and universes, ArtInput, ArtIpProg (read / static / DHCP / reset), ArtDataRequest, RDM (table of devices, TOD control, get/set any PID), firmware upload. |
| **DMX** | Monitor any universe (merged output drawn as a 512-cell grid, sources, merge/sync state, counters; optional subscription) and send DMX (channel ranges, slider, full/50 %/blackout, ArtSync). |
| **Show** | Received show control; ArtTimeCode generator, ArtTrigger, ArtCommand, ArtDiagData, ArtNzs, ArtVlc, raw datagram decode/send. |
| **Packets** | Live packet log with OpCode filter; tap for every field and the hex dump. |
| **Reference** | Searchable catalog of every protocol option and RDM parameter. |
| **Settings** | Names, style, interface/broadcast, announced output/input universes, merge mode, poll and keep-alive intervals, sync, programming, auto start. |

Platform setup is in the project: Android network + multicast permissions and a Wi-Fi multicast lock, `NSLocalNetworkUsageDescription` on iOS / Mac Catalyst, sandbox network entitlements, private-network capabilities on Windows. On Windows allow the app through the firewall for UDP 6454.

## Spec notes and choices

- ArtPollReply has no protocol version field; its Port and EstaMan (Lo, Hi) are low byte first; everything else Hi/Lo.
- Port-Address 0 is deprecated but representable; `PortAddress.IsDeprecatedZero` flags it.
- ArtDmx lengths are padded to even 2-512 when sending; received odd or truncated lengths are accepted.
- The node reports one port per ArtPollReply (the Art-Net 4 recommendation), so every universe is independent.
- Per-port ArtAddress commands (…0-…3) apply to port 0 of the addressed bind; ports 1-3 are deprecated in Art-Net 4.
- `ArtUnknownPacket` keeps OpCodes the spec lists without defining (media, video, file, directory, time sync).
- Controllers must register an OEM code with Artistic Licence; the default is OemUnknown (0x00FF) and the ESTA prototype ID (0x7FF0).
