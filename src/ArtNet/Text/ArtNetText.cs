using System.Globalization;
using System.Text;

namespace ArtNet;

/// <summary>
/// Human-readable names and descriptions for every protocol value. Registered explicitly (no reflection), so it is
/// trimming and AOT safe.
/// </summary>
public static class ArtNetText
{
    private static readonly Dictionary<Type, Dictionary<ulong, (string Name, string Description)>> Map = new();
    private static readonly HashSet<Type> FlagTypes = new();

    private static void Add<T>(T value, string name, string description) where T : struct, Enum
    {
        if (!Map.TryGetValue(typeof(T), out var d)) Map[typeof(T)] = d = new();
        d[ToUInt64(value)] = (name, description);
    }

    private static void Flags<T>() where T : struct, Enum => FlagTypes.Add(typeof(T));

    private static ulong ToUInt64<T>(T value) where T : struct, Enum => Convert.ToUInt64(value, CultureInfo.InvariantCulture);

    /// <summary>Registered (value, name, description) entries of an enum, in value order.</summary>
    public static IReadOnlyList<(T Value, string Name, string Description)> Entries<T>() where T : struct, Enum
    {
        if (!Map.TryGetValue(typeof(T), out var d)) return [];
        return Enum.GetValues<T>()
            .Where(v => d.ContainsKey(ToUInt64(v)))
            .DistinctBy(v => ToUInt64(v))
            .Select(v => (v, d[ToUInt64(v)].Name, d[ToUInt64(v)].Description))
            .ToArray();
    }

    /// <summary>Readable name; flags are joined with ", "; unknown values show the number.</summary>
    public static string ToDisplayName<T>(this T value) where T : struct, Enum
    {
        ulong raw = ToUInt64(value);
        Map.TryGetValue(typeof(T), out var d);
        if (d is not null && d.TryGetValue(raw, out var e)) return e.Name;
        if (FlagTypes.Contains(typeof(T)) && d is not null)
        {
            if (raw == 0) return "None";
            var names = new List<string>();
            ulong rest = raw;
            foreach (var (bit, entry) in d.OrderBy(k => k.Key))
            {
                if (bit == 0 || (bit & (bit - 1)) != 0) continue;
                if ((raw & bit) != 0) { names.Add(entry.Name); rest &= ~bit; }
            }
            if (rest != 0) names.Add($"0x{rest:X}");
            return string.Join(", ", names);
        }
        return $"Unknown ({raw})";
    }

    /// <summary>Description; flags are joined with "; ".</summary>
    public static string ToDescription<T>(this T value) where T : struct, Enum
    {
        ulong raw = ToUInt64(value);
        Map.TryGetValue(typeof(T), out var d);
        if (d is not null && d.TryGetValue(raw, out var e)) return e.Description;
        if (FlagTypes.Contains(typeof(T)) && d is not null)
        {
            var parts = d.Where(k => k.Key != 0 && (k.Key & (k.Key - 1)) == 0 && (raw & k.Key) != 0).OrderBy(k => k.Key).Select(k => k.Value.Description);
            var s = string.Join("; ", parts);
            return s.Length > 0 ? s : "No flags set.";
        }
        return $"Value {raw} is not defined by the specification.";
    }

    /// <summary>"Name (raw)".</summary>
    public static string ToDisplayString<T>(this T value) where T : struct, Enum => $"{value.ToDisplayName()} ({ToUInt64(value)})";

    /// <summary>"ArtPoll", "ArtDmx", … for OpCodes.</summary>
    public static string ToPacketName(this ArtNetOpCode op) =>
        Map[typeof(ArtNetOpCode)].TryGetValue((ushort)op, out var e) ? e.Name : $"OpCode 0x{(ushort)op:X4}";

    /// <summary>
    /// Parses a display name ("Led Locate", "led-locate"), an enum identifier ("LedLocate"), a decimal or 0x-hex number.
    /// </summary>
    public static T Parse<T>(string text) where T : struct, Enum =>
        TryParse<T>(text, out var v) ? v : throw new FormatException(
            $"'{text}' is not a valid {typeof(T).Name}. Options: {string.Join(", ", Entries<T>().Select(e => e.Name))}");

    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        string norm = Normalize(t);
        if (Map.TryGetValue(typeof(T), out var d))
        {
            foreach (var (raw, e) in d)
                if (Normalize(e.Name) == norm) { value = (T)Enum.ToObject(typeof(T), raw); return true; }
        }
        foreach (var v in Enum.GetValues<T>())
            if (Normalize(v.ToString()) == norm) { value = v; return true; }
        ulong n;
        bool ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)
            : ulong.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
        if (!ok) return false;
        value = (T)Enum.ToObject(typeof(T), n);
        return true;
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ formatting helpers

    /// <summary>ESTA manufacturer code: shown as two ASCII initials when printable (the spec's interpretation).</summary>
    public static string FormatEsta(ushort code)
    {
        if (code == ArtNetConstants.Global) return "0xFFFF (Art-Net defined)";
        if (code == 0) return "0x0000 (not set)";
        if (code == ArtNetConstants.EstaPrototype) return "0x7FF0 (ESTA prototype)";
        char hi = (char)(code >> 8), lo = (char)(code & 0xFF);
        return hi is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' && lo is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
            ? $"\"{hi}{lo}\""
            : $"0x{code:X4}";
    }

    /// <summary>Firmware version word as "hi.lo".</summary>
    public static string FormatVersion(ushort v) => $"{v >> 8}.{v & 0xFF}";

    public static string JoinFlags(params string[] parts) =>
        string.Join(", ", parts.Where(p => !string.IsNullOrEmpty(p) && p != "None"));

    /// <summary>"Macro 1, Macro 3 active" style list of set bits.</summary>
    public static string FormatBits(byte value, string prefix)
    {
        if (value == 0) return "None active";
        var on = Enumerable.Range(0, 8).Where(b => (value & (1 << b)) != 0).Select(b => $"{prefix} {b + 1}");
        return string.Join(", ", on) + " active";
    }

    public static string FormatBackgroundQueuePolicy(byte value) => value switch
    {
        <= 4 => ((ArtNetBackgroundQueuePolicy)value).ToDisplayName(),
        <= 250 => $"Manufacturer defined ({value})",
        _ => $"Reserved ({value})",
    };

    public static string FormatDataRequest(ushort code) => code >= 0x8000
        ? $"Manufacturer specific 0x{code:X4}"
        : ((ArtNetDataRequestCode)code).ToDisplayName();

    /// <summary>Known DMX512 alternate start codes.</summary>
    public static string FormatStartCode(byte code) => code switch
    {
        0x00 => "Null start code (dimmer data)",
        0x17 => "Text packet (ASC)",
        0x55 => "Test packet",
        0x91 => "VLC / UTF-8 manufacturer ID packet",
        0xCC => "RDM",
        0xCF => "System Information Packet (SIP)",
        0xFE => "ESTA reserved (future expansion)",
        _ => $"Alternate start code 0x{code:X2}",
    };

    /// <summary>Meaning of an Art-Net defined ArtCommand command.</summary>
    public static string? DescribeCommand(string command) => command.ToLowerInvariant() switch
    {
        "swouttext" => "Re-programs the label of the ArtPollReply SwOut fields",
        "swintext" => "Re-programs the label of the ArtPollReply SwIn fields",
        _ => null,
    };

    /// <summary>DMX level as percent text ("FL" for full, like a console).</summary>
    public static string Percent(byte level) => level == 255 ? "FL" : ((int)Math.Round(level * 100 / 255.0)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Nominal (integer) frames per second.</summary>
    public static int NominalFrames(this ArtNetTimeCodeType type) => type switch
    {
        ArtNetTimeCodeType.Film => 24,
        ArtNetTimeCodeType.Ebu => 25,
        _ => 30,
    };

    /// <summary>Actual frame rate (29.97 for drop frame).</summary>
    public static double FrameRate(this ArtNetTimeCodeType type) => type switch
    {
        ArtNetTimeCodeType.Film => 24,
        ArtNetTimeCodeType.Ebu => 25,
        ArtNetTimeCodeType.DropFrame => 30000.0 / 1001.0,
        _ => 30,
    };

    // ------------------------------------------------------------------ registrations

    static ArtNetText()
    {
        Add(ArtNetOpCode.Poll, "ArtPoll", "Discovers controllers, nodes and media servers; every device replies with ArtPollReply.");
        Add(ArtNetOpCode.PollReply, "ArtPollReply", "Device status: names, ports, Port-Addresses, status flags. Unicast in reply to ArtPoll.");
        Add(ArtNetOpCode.DiagData, "ArtDiagData", "Diagnostics and data logging text.");
        Add(ArtNetOpCode.Command, "ArtCommand", "Text based parameter commands (\"Command=Data&\").");
        Add(ArtNetOpCode.DataRequest, "ArtDataRequest", "Requests data such as product URLs.");
        Add(ArtNetOpCode.DataReply, "ArtDataReply", "Reply to ArtDataRequest.");
        Add(ArtNetOpCode.Dmx, "ArtDmx", "Zero start code DMX512 data for a single universe.");
        Add(ArtNetOpCode.Nzs, "ArtNzs", "Non-zero start code DMX512 data (except RDM) for a single universe.");
        Add(ArtNetOpCode.Sync, "ArtSync", "Forces synchronous output of previously received ArtDmx.");
        Add(ArtNetOpCode.Address, "ArtAddress", "Remote programming of names, Port-Addresses and node commands.");
        Add(ArtNetOpCode.Input, "ArtInput", "Enables or disables DMX512 inputs.");
        Add(ArtNetOpCode.TodRequest, "ArtTodRequest", "Requests the RDM Table of Devices.");
        Add(ArtNetOpCode.TodData, "ArtTodData", "RDM Table of Devices.");
        Add(ArtNetOpCode.TodControl, "ArtTodControl", "RDM discovery control.");
        Add(ArtNetOpCode.Rdm, "ArtRdm", "Non-discovery RDM message.");
        Add(ArtNetOpCode.RdmSub, "ArtRdmSub", "Compressed RDM sub-device data.");
        Add(ArtNetOpCode.Media, "ArtMedia", "Unicast by a media server, acted upon by a controller.");
        Add(ArtNetOpCode.MediaPatch, "ArtMediaPatch", "Unicast by a controller, acted upon by a media server.");
        Add(ArtNetOpCode.MediaControl, "ArtMediaControl", "Unicast by a controller, acted upon by a media server.");
        Add(ArtNetOpCode.MediaControlReply, "ArtMediaControlReply", "Unicast by a media server, acted upon by a controller.");
        Add(ArtNetOpCode.TimeCode, "ArtTimeCode", "Transports time code over the network.");
        Add(ArtNetOpCode.TimeSync, "ArtTimeSync", "Synchronises real time date and clock.");
        Add(ArtNetOpCode.Trigger, "ArtTrigger", "Sends trigger macros.");
        Add(ArtNetOpCode.Directory, "ArtDirectory", "Requests a node's file list.");
        Add(ArtNetOpCode.DirectoryReply, "ArtDirectoryReply", "Replies to ArtDirectory with a file list.");
        Add(ArtNetOpCode.VideoSetup, "ArtVideoSetup", "Video screen setup for nodes with extended video features.");
        Add(ArtNetOpCode.VideoPalette, "ArtVideoPalette", "Colour palette setup for nodes with extended video features.");
        Add(ArtNetOpCode.VideoData, "ArtVideoData", "Display data for nodes with extended video features.");
        Add(ArtNetOpCode.MacMaster, "ArtMacMaster", "Deprecated.");
        Add(ArtNetOpCode.MacSlave, "ArtMacSlave", "Deprecated.");
        Add(ArtNetOpCode.FirmwareMaster, "ArtFirmwareMaster", "Uploads new firmware or firmware extensions to a node.");
        Add(ArtNetOpCode.FirmwareReply, "ArtFirmwareReply", "Acknowledges ArtFirmwareMaster / ArtFileTnMaster.");
        Add(ArtNetOpCode.FileTnMaster, "ArtFileTnMaster", "Uploads a user file to a node.");
        Add(ArtNetOpCode.FileFnMaster, "ArtFileFnMaster", "Downloads a user file from a node.");
        Add(ArtNetOpCode.FileFnReply, "ArtFileFnReply", "Server to node acknowledge for download packets.");
        Add(ArtNetOpCode.IpProg, "ArtIpProg", "Reprograms the IP address, mask and gateway of a node.");
        Add(ArtNetOpCode.IpProgReply, "ArtIpProgReply", "Acknowledges ArtIpProg with the node's IP settings.");

        Add(ArtNetNodeReportCode.Debug, "Debug", "Booted in debug mode (only used in development).");
        Add(ArtNetNodeReportCode.PowerOk, "Power OK", "Power on tests successful.");
        Add(ArtNetNodeReportCode.PowerFail, "Power Fail", "Hardware tests failed at power on.");
        Add(ArtNetNodeReportCode.SocketWr1, "Socket Write Fail", "Last UDP from node failed due to truncated length, most likely caused by a collision.");
        Add(ArtNetNodeReportCode.ParseFail, "Parse Fail", "Unable to identify last UDP transmission. Check OpCode and packet length.");
        Add(ArtNetNodeReportCode.UdpFail, "UDP Fail", "Unable to open UDP socket in last transmission attempt.");
        Add(ArtNetNodeReportCode.ShNameOk, "Short Name OK", "Port name programming via ArtAddress was successful.");
        Add(ArtNetNodeReportCode.LoNameOk, "Long Name OK", "Long name programming via ArtAddress was successful.");
        Add(ArtNetNodeReportCode.DmxError, "DMX Error", "DMX512 receive errors detected.");
        Add(ArtNetNodeReportCode.DmxUdpFull, "DMX Tx Buffers Full", "Ran out of internal DMX transmit buffers.");
        Add(ArtNetNodeReportCode.DmxRxFull, "DMX Rx Buffers Full", "Ran out of internal DMX receive buffers.");
        Add(ArtNetNodeReportCode.SwitchErr, "Switch Conflict", "Rx universe switches conflict.");
        Add(ArtNetNodeReportCode.ConfigErr, "Config Error", "Product configuration does not match firmware.");
        Add(ArtNetNodeReportCode.DmxShort, "DMX Short", "DMX output short detected. See the GoodOutput field.");
        Add(ArtNetNodeReportCode.FirmwareFail, "Firmware Fail", "Last attempt to upload new firmware failed.");
        Add(ArtNetNodeReportCode.UserFail, "User Change Ignored", "User changed switch settings when address locked by remote programming. User changes ignored.");
        Add(ArtNetNodeReportCode.FactoryRes, "Factory Reset", "Factory reset has occurred.");

        Add(ArtNetStyle.Node, "Node", "A DMX to / from Art-Net device.");
        Add(ArtNetStyle.Controller, "Controller", "A lighting console.");
        Add(ArtNetStyle.Media, "Media Server", "A media server.");
        Add(ArtNetStyle.Route, "Router", "A network routing device.");
        Add(ArtNetStyle.Backup, "Backup", "A backup device.");
        Add(ArtNetStyle.Config, "Config Tool", "A configuration or diagnostic tool.");
        Add(ArtNetStyle.Visual, "Visualiser", "A visualiser.");

        Add(ArtNetDiagnosticPriority.Low, "Low", "Low priority message.");
        Add(ArtNetDiagnosticPriority.Medium, "Medium", "Medium priority message.");
        Add(ArtNetDiagnosticPriority.High, "High", "High priority message.");
        Add(ArtNetDiagnosticPriority.Critical, "Critical", "Critical priority message.");
        Add(ArtNetDiagnosticPriority.Volatile, "Volatile", "Volatile message: displayed on a single line rather than in a list.");

        Add(ArtNetDataRequestCode.Poll, "Poll", "Controller is polling to establish whether ArtDataRequest is supported.");
        Add(ArtNetDataRequestCode.UrlProduct, "Product URL", "URL to manufacturer product page.");
        Add(ArtNetDataRequestCode.UrlUserGuide, "User Guide URL", "URL to manufacturer user guide.");
        Add(ArtNetDataRequestCode.UrlSupport, "Support URL", "URL to manufacturer support page.");
        Add(ArtNetDataRequestCode.UrlPersonalityUdr, "UDR Personality URL", "URL to manufacturer UDR personality.");
        Add(ArtNetDataRequestCode.UrlPersonalityGdtf, "GDTF Personality URL", "URL to manufacturer GDTF personality.");
        Add(ArtNetDataRequestCode.ManufacturerSpecific, "Manufacturer Specific", "0x8000-0xFFFF: manufacturer specific use.");

        Flags<ArtPollFlags>();
        Add(ArtPollFlags.None, "None", "Only reply to ArtPoll / ArtAddress; no diagnostics; VLC enabled; not targeted.");
        Add(ArtPollFlags.ReplyOnChange, "Reply On Change", "Send ArtPollReply whenever node conditions change, so the controller need not poll continuously.");
        Add(ArtPollFlags.Diagnostics, "Send Diagnostics", "Send diagnostics messages to this controller.");
        Add(ArtPollFlags.DiagnosticsUnicast, "Unicast Diagnostics", "Diagnostics messages are unicast (otherwise broadcast).");
        Add(ArtPollFlags.DisableVlc, "Disable VLC", "Disable VLC transmission.");
        Add(ArtPollFlags.TargetedMode, "Targeted Mode", "Only nodes subscribed to a Port-Address in the target range reply.");

        Flags<ArtNetStatus1>();
        Add(ArtNetStatus1.None, "None", "No UBEA, not RDM capable, normal boot.");
        Add(ArtNetStatus1.UbeaPresent, "UBEA Present", "User BIOS extension area is present.");
        Add(ArtNetStatus1.RdmCapable, "RDM Capable", "Capable of Remote Device Management.");
        Add(ArtNetStatus1.BootedFromRom, "Booted From ROM", "Booted from ROM (dual boot recovery).");

        Add(ArtNetIndicatorState.Unknown, "Unknown", "Indicator state unknown.");
        Add(ArtNetIndicatorState.Locate, "Locate", "Indicators in locate / identify mode.");
        Add(ArtNetIndicatorState.Mute, "Mute", "Indicators in mute mode.");
        Add(ArtNetIndicatorState.Normal, "Normal", "Indicators in normal mode.");

        Add(ArtNetPortAddressAuthority.Unknown, "Unknown", "Port-Address programming authority unknown.");
        Add(ArtNetPortAddressAuthority.FrontPanel, "Front Panel", "All Port-Addresses set by front panel controls.");
        Add(ArtNetPortAddressAuthority.Network, "Network", "All or part of the Port-Address programmed by network or web browser.");
        Add(ArtNetPortAddressAuthority.NotUsed, "Not Used", "Not used.");

        Flags<ArtNetStatus2>();
        Add(ArtNetStatus2.None, "None", "No optional features.");
        Add(ArtNetStatus2.WebConfiguration, "Web Configuration", "Product supports web browser configuration.");
        Add(ArtNetStatus2.DhcpConfigured, "DHCP Configured", "IP is DHCP configured (otherwise manually configured).");
        Add(ArtNetStatus2.DhcpCapable, "DHCP Capable", "Node is DHCP capable.");
        Add(ArtNetStatus2.PortAddress15Bit, "15-bit Port-Address", "Supports 15-bit Port-Address (Art-Net 3 or 4); otherwise 8-bit (Art-Net II).");
        Add(ArtNetStatus2.SacnSwitchable, "sACN Switchable", "Able to switch between Art-Net and sACN.");
        Add(ArtNetStatus2.Squawking, "Squawking", "Node is squawking.");
        Add(ArtNetStatus2.OutputStyleSwitchable, "Output Style Switchable", "Supports switching of output style (delta / continuous) using ArtAddress.");
        Add(ArtNetStatus2.RdmControllable, "RDM Controllable", "Supports control of RDM using ArtAddress.");

        Flags<ArtNetStatus3>();
        Add(ArtNetStatus3.None, "None", "No optional features.");
        Add(ArtNetStatus3.BackgroundDiscoveryControllable, "Background Discovery Control", "Background discovery can be enabled or disabled by ArtAddress.");
        Add(ArtNetStatus3.BackgroundQueueSupported, "Background Queue", "BackgroundQueue is supported.");
        Add(ArtNetStatus3.RdmNet, "RDMnet", "Node supports RDMnet.");
        Add(ArtNetStatus3.PortDirectionSwitchable, "Switchable Port Direction", "Ports can be switched between input and output (PortTypes shows the current direction).");
        Add(ArtNetStatus3.Llrp, "LLRP", "Node supports LLRP.");
        Add(ArtNetStatus3.FailsafeProgrammable, "Programmable Failsafe", "Node supports programmable failsafe.");

        Add(ArtNetFailsafeState.Hold, "Hold Last State", "Outputs hold the last state when network data is lost.");
        Add(ArtNetFailsafeState.Zero, "All Zero", "All outputs go to zero when network data is lost.");
        Add(ArtNetFailsafeState.Full, "All Full", "All outputs go to full when network data is lost.");
        Add(ArtNetFailsafeState.Scene, "Failsafe Scene", "The failsafe scene is played back when network data is lost.");

        Flags<ArtNetPortDirection>();
        Add(ArtNetPortDirection.None, "None", "Port not implemented.");
        Add(ArtNetPortDirection.Input, "Input", "Can input onto the Art-Net network.");
        Add(ArtNetPortDirection.Output, "Output", "Can output data from the Art-Net network.");

        Add(ArtNetPortProtocol.Dmx512, "DMX512", "DMX512.");
        Add(ArtNetPortProtocol.Midi, "MIDI", "MIDI.");
        Add(ArtNetPortProtocol.Avab, "Avab", "Avab.");
        Add(ArtNetPortProtocol.ColortranCmx, "Colortran CMX", "Colortran CMX.");
        Add(ArtNetPortProtocol.Adb625, "ADB 62.5", "ADB 62.5.");
        Add(ArtNetPortProtocol.ArtNet, "Art-Net", "Art-Net.");
        Add(ArtNetPortProtocol.Dali, "DALI", "DALI.");

        Flags<ArtNetGoodInput>();
        Add(ArtNetGoodInput.None, "OK", "No input data, converting to Art-Net.");
        Add(ArtNetGoodInput.ConvertToSacn, "Converts To sACN", "Input is selected to convert to sACN (clear = Art-Net).");
        Add(ArtNetGoodInput.ReceiveErrors, "Receive Errors", "Receive errors detected.");
        Add(ArtNetGoodInput.Disabled, "Disabled", "Input is disabled.");
        Add(ArtNetGoodInput.TextPackets, "Text Packets", "Channel includes DMX512 text packets.");
        Add(ArtNetGoodInput.Sips, "SIPs", "Channel includes DMX512 SIPs.");
        Add(ArtNetGoodInput.TestPackets, "Test Packets", "Channel includes DMX512 test packets.");
        Add(ArtNetGoodInput.DataReceived, "Data Received", "Data received.");

        Flags<ArtNetGoodOutputA>();
        Add(ArtNetGoodOutputA.None, "Idle", "No data output, HTP, converting from Art-Net.");
        Add(ArtNetGoodOutputA.ConvertFromSacn, "From sACN", "Output is selected to convert from sACN (clear = Art-Net).");
        Add(ArtNetGoodOutputA.MergeLtp, "LTP", "Merge mode is LTP (clear = HTP).");
        Add(ArtNetGoodOutputA.OutputShort, "Short Detected", "DMX output short detected on power up.");
        Add(ArtNetGoodOutputA.Merging, "Merging", "Output is merging Art-Net data.");
        Add(ArtNetGoodOutputA.TextPackets, "Text Packets", "Channel includes DMX512 text packets.");
        Add(ArtNetGoodOutputA.Sips, "SIPs", "Channel includes DMX512 SIPs.");
        Add(ArtNetGoodOutputA.TestPackets, "Test Packets", "Channel includes DMX512 test packets.");
        Add(ArtNetGoodOutputA.DataTransmitted, "Outputting", "ArtDmx or sACN data is being output as DMX512.");

        Flags<ArtNetGoodOutputB>();
        Add(ArtNetGoodOutputB.None, "RDM On, Delta, Discovering", "RDM enabled, delta output style, discovery running, background discovery enabled.");
        Add(ArtNetGoodOutputB.BackgroundDiscoveryDisabled, "Background Discovery Off", "Background discovery is disabled.");
        Add(ArtNetGoodOutputB.DiscoveryNotRunning, "Discovery Idle", "Discovery is currently not running.");
        Add(ArtNetGoodOutputB.ContinuousOutput, "Continuous", "Output style is continuous (clear = delta).");
        Add(ArtNetGoodOutputB.RdmDisabled, "RDM Off", "RDM is disabled.");

        Add(ArtNetBackgroundQueuePolicy.StatusNone, "STATUS_NONE", "Collect using STATUS_NONE.");
        Add(ArtNetBackgroundQueuePolicy.StatusAdvisory, "STATUS_ADVISORY", "Collect using STATUS_ADVISORY.");
        Add(ArtNetBackgroundQueuePolicy.StatusWarning, "STATUS_WARNING", "Collect using STATUS_WARNING.");
        Add(ArtNetBackgroundQueuePolicy.StatusError, "STATUS_ERROR", "Collect using STATUS_ERROR.");
        Add(ArtNetBackgroundQueuePolicy.Disabled, "Disabled", "Collection disabled.");

        RegisterAddressCommands();

        Flags<ArtIpProgCommand>();
        Add(ArtIpProgCommand.None, "Enquiry", "No programming: the node returns its current settings.");
        Add(ArtIpProgCommand.ProgramPort, "Program Port", "Program the UDP port (deprecated).");
        Add(ArtIpProgCommand.ProgramSubnetMask, "Program Mask", "Program the subnet mask.");
        Add(ArtIpProgCommand.ProgramIp, "Program IP", "Program the IP address.");
        Add(ArtIpProgCommand.ResetToDefault, "Reset To Default", "Return all three parameters to default.");
        Add(ArtIpProgCommand.ProgramGateway, "Program Gateway", "Program the default gateway.");
        Add(ArtIpProgCommand.EnableDhcp, "Enable DHCP", "Enable DHCP (lower bits ignored).");
        Add(ArtIpProgCommand.EnableProgramming, "Enable Programming", "Enable any programming.");

        Flags<ArtIpProgStatus>();
        Add(ArtIpProgStatus.None, "Static", "DHCP disabled.");
        Add(ArtIpProgStatus.DhcpEnabled, "DHCP Enabled", "DHCP enabled.");

        Add(ArtNetTimeCodeType.Film, "Film (24 fps)", "Film, 24 frames per second.");
        Add(ArtNetTimeCodeType.Ebu, "EBU (25 fps)", "EBU, 25 frames per second.");
        Add(ArtNetTimeCodeType.DropFrame, "DF (29.97 fps)", "Drop frame, 29.97 frames per second.");
        Add(ArtNetTimeCodeType.Smpte, "SMPTE (30 fps)", "SMPTE, 30 frames per second.");

        Add(ArtNetTriggerKey.Ascii, "ASCII Key", "SubKey is an ASCII character processed as a keyboard press.");
        Add(ArtNetTriggerKey.Macro, "Macro", "SubKey is the number of a macro to execute.");
        Add(ArtNetTriggerKey.Soft, "Soft Key", "SubKey is a soft-key number processed as a soft-key press.");
        Add(ArtNetTriggerKey.Show, "Show", "SubKey is the number of a show to run.");

        Add(ArtNetFirmwareMasterType.FirmFirst, "Firmware First", "The first packet of a firmware upload.");
        Add(ArtNetFirmwareMasterType.FirmCont, "Firmware Continue", "A consecutive continuation packet of a firmware upload.");
        Add(ArtNetFirmwareMasterType.FirmLast, "Firmware Last", "The last packet of a firmware upload.");
        Add(ArtNetFirmwareMasterType.UbeaFirst, "UBEA First", "The first packet of a UBEA upload.");
        Add(ArtNetFirmwareMasterType.UbeaCont, "UBEA Continue", "A consecutive continuation packet of a UBEA upload.");
        Add(ArtNetFirmwareMasterType.UbeaLast, "UBEA Last", "The last packet of a UBEA upload.");

        Add(ArtNetFirmwareReplyType.BlockGood, "Block Good", "Last packet received successfully.");
        Add(ArtNetFirmwareReplyType.AllGood, "All Good", "All firmware received successfully.");
        Add(ArtNetFirmwareReplyType.Fail, "Fail", "Firmware upload failed (all error conditions).");

        Add(ArtNetTodRequestCommand.TodFull, "TOD Full", "Send the entire TOD.");
        Add(ArtNetTodDataCommand.TodFull, "TOD Full", "The packet contains the entire TOD or is the first of a sequence that does.");
        Add(ArtNetTodDataCommand.TodNak, "TOD NAK", "The TOD is not available or discovery is incomplete.");

        Add(ArtNetTodControlCommand.None, "None", "No action.");
        Add(ArtNetTodControlCommand.Flush, "Flush", "The port flushes its TOD and instigates full discovery.");
        Add(ArtNetTodControlCommand.End, "End Discovery", "The port ends current discovery but does not flush the TOD.");
        Add(ArtNetTodControlCommand.IncrementalOn, "Incremental On", "The port enables incremental discovery.");
        Add(ArtNetTodControlCommand.IncrementalOff, "Incremental Off", "The port disables incremental discovery.");

        Add(ArtNetRdmCommand.Process, "Process", "Process RDM packet.");
        Add(ArtNetRdmVersion.Draft, "RDM Draft 1.0", "Device only supports RDM DRAFT V1.0.");
        Add(ArtNetRdmVersion.Standard, "RDM Standard 1.0", "Device supports RDM STANDARD V1.0.");

        Add(RdmCommandClass.Discovery, "Discovery", "DISCOVERY_COMMAND.");
        Add(RdmCommandClass.DiscoveryResponse, "Discovery Response", "DISCOVERY_COMMAND_RESPONSE.");
        Add(RdmCommandClass.Get, "Get", "GET_COMMAND.");
        Add(RdmCommandClass.GetResponse, "Get Response", "GET_COMMAND_RESPONSE.");
        Add(RdmCommandClass.Set, "Set", "SET_COMMAND.");
        Add(RdmCommandClass.SetResponse, "Set Response", "SET_COMMAND_RESPONSE.");

        Flags<ArtVlcFlags>();
        Add(ArtVlcFlags.None, "None", "Single transmission, not a reply, payload defined by PayLanguage.");
        Add(ArtVlcFlags.Beacon, "Beacon", "Transmitter repeats this packet until another is received.");
        Add(ArtVlcFlags.Reply, "Reply", "Reply to the request with the matching transaction number.");
        Add(ArtVlcFlags.Ieee, "IEEE", "Payload is IEEE VLC data.");

        Add(ArtVlcPayloadLanguage.BeaconUrl, "Beacon URL", "Payload is a text URL.");
        Add(ArtVlcPayloadLanguage.BeaconText, "Beacon Text", "Payload is an ASCII text message.");
        Add(ArtVlcPayloadLanguage.BeaconLocationId, "Beacon Location ID", "Payload is a big-endian 16-bit number.");

        Add(ArtNetMergeMode.Htp, "HTP", "Highest takes precedence (default).");
        Add(ArtNetMergeMode.Ltp, "LTP", "Latest takes precedence.");

        Add(ArtNetPortKind.Output, "Output", "Outputs DMX512 from the network: receives ArtDmx for its Port-Address.");
        Add(ArtNetPortKind.Input, "Input", "Inputs DMX512 onto the network: transmits ArtDmx for its Port-Address.");
    }

    private static void RegisterAddressCommands()
    {
        Add(ArtNetAddressCommand.None, "None", "No action.");
        Add(ArtNetAddressCommand.CancelMerge, "Cancel Merge", "If the node is merging, cancel merge mode upon receipt of the next ArtDmx.");
        Add(ArtNetAddressCommand.LedNormal, "LED Normal", "Front panel indicators operate normally.");
        Add(ArtNetAddressCommand.LedMute, "LED Mute", "Front panel indicators are disabled and switched off.");
        Add(ArtNetAddressCommand.LedLocate, "LED Locate", "Rapid flashing of the front panel indicators to identify the node.");
        Add(ArtNetAddressCommand.ResetRxFlags, "Reset Rx Flags", "Resets SIP, text, test and data error flags; re-runs an output short test.");
        Add(ArtNetAddressCommand.AnalysisOn, "Analysis On", "Enable analysis and debugging mode.");
        Add(ArtNetAddressCommand.AnalysisOff, "Analysis Off", "Disable analysis and debugging mode.");
        Add(ArtNetAddressCommand.FailHold, "Failsafe Hold", "Hold last state on loss of network data.");
        Add(ArtNetAddressCommand.FailZero, "Failsafe Zero", "Outputs to zero on loss of network data.");
        Add(ArtNetAddressCommand.FailFull, "Failsafe Full", "Outputs to full on loss of network data.");
        Add(ArtNetAddressCommand.FailScene, "Failsafe Scene", "Play the failsafe scene on loss of network data.");
        Add(ArtNetAddressCommand.FailRecord, "Failsafe Record", "Record the current output state as the failsafe scene.");

        string[] ordinal = ["0", "1", "2", "3"];
        for (int i = 0; i < 4; i++)
        {
            string dep = i == 0 ? "" : " (deprecated in Art-Net 4)";
            Add((ArtNetAddressCommand)(0x10 + i), $"Merge LTP {i}", $"Set DMX port {ordinal[i]} to merge in LTP mode{dep}.");
            Add((ArtNetAddressCommand)(0x20 + i), $"Direction Tx {i}", $"Set port {i} direction to output{dep}.");
            Add((ArtNetAddressCommand)(0x30 + i), $"Direction Rx {i}", $"Set port {i} direction to input and flush its subscriber list{dep}.");
            Add((ArtNetAddressCommand)(0x50 + i), $"Merge HTP {i}", $"Set DMX port {i} to merge in HTP (default) mode{dep}.");
            Add((ArtNetAddressCommand)(0x60 + i), $"Art-Net Select {i}", $"Set DMX port {i} to Art-Net for DMX512 and RDM (default){dep}.");
            Add((ArtNetAddressCommand)(0x70 + i), $"sACN Select {i}", $"Set DMX port {i} to sACN for DMX512 and Art-Net for RDM{dep}.");
            Add((ArtNetAddressCommand)(0x90 + i), $"Clear Output {i}", $"Clear the DMX output buffer for port {i}{dep}.");
            Add((ArtNetAddressCommand)(0xA0 + i), $"Style Delta {i}", $"Output style delta (DMX frame triggered by ArtDmx) for port {i}{dep}.");
            Add((ArtNetAddressCommand)(0xB0 + i), $"Style Constant {i}", $"Output style constant (continuous DMX) for port {i}{dep}.");
            Add((ArtNetAddressCommand)(0xC0 + i), $"RDM Enable {i}", $"Enable RDM for port {i}{dep}.");
            Add((ArtNetAddressCommand)(0xD0 + i), $"RDM Disable {i}", $"Disable RDM for port {i}{dep}.");
        }
        string[] bqp = ["STATUS_NONE", "STATUS_ADVISORY", "STATUS_WARNING", "STATUS_ERROR", "disabled"];
        for (int i = 0; i < 16; i++)
            Add((ArtNetAddressCommand)(0xE0 + i), $"Background Queue Policy {i}",
                i < bqp.Length ? $"Set BackgroundQueuePolicy to {i} ({bqp[i]})." : $"Set BackgroundQueuePolicy to {i} (user defined).");
    }
}
