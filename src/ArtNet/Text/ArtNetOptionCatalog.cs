using System.Text;

namespace ArtNet;

/// <summary>Non-generic view of an option (for grouped UI lists).</summary>
public interface IArtNetOption
{
    string Name { get; }
    string Description { get; }
    ulong RawValue { get; }
    string RawText { get; }
}

/// <summary>One protocol option: value, readable name, description and raw number.</summary>
public sealed record ArtNetOption<T>(T Value, string Name, string Description, ulong RawValue) : IArtNetOption where T : struct, Enum
{
    public string RawText => RawValue > 255 ? $"0x{RawValue:X4}" : $"0x{RawValue:X2}";
    public override string ToString() => $"{Name} ({RawText}) – {Description}";
}

/// <summary>A titled list of options for one enum.</summary>
public sealed record ArtNetOptionGroup(string Title, string Description, Type EnumType, IReadOnlyList<IArtNetOption> Options)
{
    public override string ToString() => Title;
}

/// <summary>Every option list of the protocol, ready for pickers and reference pages.</summary>
public static class ArtNetOptionCatalog
{
    private static IReadOnlyList<ArtNetOption<T>> Build<T>() where T : struct, Enum =>
        ArtNetText.Entries<T>().Select(e => new ArtNetOption<T>(e.Value, e.Name, e.Description, Convert.ToUInt64(e.Value))).ToArray();

    public static IReadOnlyList<ArtNetOption<ArtNetOpCode>> OpCodes { get; } = Build<ArtNetOpCode>();
    public static IReadOnlyList<ArtNetOption<ArtNetStyle>> Styles { get; } = Build<ArtNetStyle>();
    public static IReadOnlyList<ArtNetOption<ArtNetNodeReportCode>> NodeReportCodes { get; } = Build<ArtNetNodeReportCode>();
    public static IReadOnlyList<ArtNetOption<ArtNetDiagnosticPriority>> DiagnosticPriorities { get; } = Build<ArtNetDiagnosticPriority>();
    public static IReadOnlyList<ArtNetOption<ArtNetDataRequestCode>> DataRequestCodes { get; } = Build<ArtNetDataRequestCode>();
    public static IReadOnlyList<ArtNetOption<ArtPollFlags>> PollFlags { get; } = Build<ArtPollFlags>();
    public static IReadOnlyList<ArtNetOption<ArtNetStatus1>> Status1 { get; } = Build<ArtNetStatus1>();
    public static IReadOnlyList<ArtNetOption<ArtNetIndicatorState>> IndicatorStates { get; } = Build<ArtNetIndicatorState>();
    public static IReadOnlyList<ArtNetOption<ArtNetPortAddressAuthority>> PortAddressAuthorities { get; } = Build<ArtNetPortAddressAuthority>();
    public static IReadOnlyList<ArtNetOption<ArtNetStatus2>> Status2 { get; } = Build<ArtNetStatus2>();
    public static IReadOnlyList<ArtNetOption<ArtNetStatus3>> Status3 { get; } = Build<ArtNetStatus3>();
    public static IReadOnlyList<ArtNetOption<ArtNetFailsafeState>> FailsafeStates { get; } = Build<ArtNetFailsafeState>();
    public static IReadOnlyList<ArtNetOption<ArtNetPortDirection>> PortDirections { get; } = Build<ArtNetPortDirection>();
    public static IReadOnlyList<ArtNetOption<ArtNetPortProtocol>> PortProtocols { get; } = Build<ArtNetPortProtocol>();
    public static IReadOnlyList<ArtNetOption<ArtNetGoodInput>> GoodInput { get; } = Build<ArtNetGoodInput>();
    public static IReadOnlyList<ArtNetOption<ArtNetGoodOutputA>> GoodOutputA { get; } = Build<ArtNetGoodOutputA>();
    public static IReadOnlyList<ArtNetOption<ArtNetGoodOutputB>> GoodOutputB { get; } = Build<ArtNetGoodOutputB>();
    public static IReadOnlyList<ArtNetOption<ArtNetBackgroundQueuePolicy>> BackgroundQueuePolicies { get; } = Build<ArtNetBackgroundQueuePolicy>();
    public static IReadOnlyList<ArtNetOption<ArtNetAddressCommand>> AddressCommands { get; } = Build<ArtNetAddressCommand>();
    public static IReadOnlyList<ArtNetOption<ArtIpProgCommand>> IpProgCommands { get; } = Build<ArtIpProgCommand>();
    public static IReadOnlyList<ArtNetOption<ArtNetTimeCodeType>> TimeCodeTypes { get; } = Build<ArtNetTimeCodeType>();
    public static IReadOnlyList<ArtNetOption<ArtNetTriggerKey>> TriggerKeys { get; } = Build<ArtNetTriggerKey>();
    public static IReadOnlyList<ArtNetOption<ArtNetFirmwareMasterType>> FirmwareMasterTypes { get; } = Build<ArtNetFirmwareMasterType>();
    public static IReadOnlyList<ArtNetOption<ArtNetFirmwareReplyType>> FirmwareReplyTypes { get; } = Build<ArtNetFirmwareReplyType>();
    public static IReadOnlyList<ArtNetOption<ArtNetTodDataCommand>> TodDataCommands { get; } = Build<ArtNetTodDataCommand>();
    public static IReadOnlyList<ArtNetOption<ArtNetTodControlCommand>> TodControlCommands { get; } = Build<ArtNetTodControlCommand>();
    public static IReadOnlyList<ArtNetOption<ArtNetRdmVersion>> RdmVersions { get; } = Build<ArtNetRdmVersion>();
    public static IReadOnlyList<ArtNetOption<RdmCommandClass>> RdmCommandClasses { get; } = Build<RdmCommandClass>();
    public static IReadOnlyList<ArtNetOption<ArtVlcFlags>> VlcFlags { get; } = Build<ArtVlcFlags>();
    public static IReadOnlyList<ArtNetOption<ArtVlcPayloadLanguage>> VlcPayloadLanguages { get; } = Build<ArtVlcPayloadLanguage>();
    public static IReadOnlyList<ArtNetOption<ArtNetMergeMode>> MergeModes { get; } = Build<ArtNetMergeMode>();
    public static IReadOnlyList<ArtNetOption<ArtNetPortKind>> PortKinds { get; } = Build<ArtNetPortKind>();

    private static ArtNetOptionGroup G<T>(string title, string description, IReadOnlyList<ArtNetOption<T>> options) where T : struct, Enum =>
        new(title, description, typeof(T), options.Cast<IArtNetOption>().ToArray());

    /// <summary>Every group, in specification order.</summary>
    public static IReadOnlyList<ArtNetOptionGroup> All { get; } =
    [
        G("OpCodes", "Table 1: packet types (transmitted low byte first).", OpCodes),
        G("Style", "Table 4: general functionality of a device (ArtPollReply).", Styles),
        G("Node Report", "Table 3: status codes in ArtPollReply → NodeReport \"#xxxx [yyyy] text\".", NodeReportCodes),
        G("Diagnostics Priority", "Table 5: ArtPoll → DiagPriority and ArtDiagData → Priority.", DiagnosticPriorities),
        G("Data Request", "Table 4a: ArtDataRequest / ArtDataReply contents.", DataRequestCodes),
        G("Poll Flags", "ArtPoll → Flags.", PollFlags),
        G("Status1", "ArtPollReply → Status1 bits 2-0.", Status1),
        G("Indicator State", "ArtPollReply → Status1 bits 7-6.", IndicatorStates),
        G("Port-Address Authority", "ArtPollReply → Status1 bits 5-4.", PortAddressAuthorities),
        G("Status2", "ArtPollReply → Status2.", Status2),
        G("Status3", "ArtPollReply → Status3 bits 5-0.", Status3),
        G("Failsafe State", "ArtPollReply → Status3 bits 7-6.", FailsafeStates),
        G("Port Direction", "ArtPollReply → PortTypes bits 7-6.", PortDirections),
        G("Port Protocol", "ArtPollReply → PortTypes bits 5-0.", PortProtocols),
        G("Good Input", "ArtPollReply → GoodInput[].", GoodInput),
        G("Good Output A", "ArtPollReply → GoodOutputA[].", GoodOutputA),
        G("Good Output B", "ArtPollReply → GoodOutputB[].", GoodOutputB),
        G("Background Queue Policy", "ArtPollReply → BackgroundQueuePolicy (5-250 manufacturer defined).", BackgroundQueuePolicies),
        G("Address Command", "ArtAddress → Command.", AddressCommands),
        G("IP Programming Command", "ArtIpProg → Command.", IpProgCommands),
        G("Time Code Type", "ArtTimeCode → Type.", TimeCodeTypes),
        G("Trigger Key", "Table 7: ArtTrigger → Key when OEM = 0xFFFF.", TriggerKeys),
        G("Firmware Master Type", "ArtFirmwareMaster → Type.", FirmwareMasterTypes),
        G("Firmware Reply Type", "ArtFirmwareReply → Type.", FirmwareReplyTypes),
        G("TOD Data Response", "ArtTodData → CommandResponse.", TodDataCommands),
        G("TOD Control Command", "ArtTodControl → Command.", TodControlCommands),
        G("RDM Version", "ArtTodData / ArtRdm / ArtRdmSub → RdmVer.", RdmVersions),
        G("RDM Command Class", "ANSI E1.20 command classes (ArtRdmSub, ArtRdm payload).", RdmCommandClasses),
        G("VLC Flags", "ArtVlc → Flags.", VlcFlags),
        G("VLC Payload Language", "ArtVlc → PayLanguage.", VlcPayloadLanguages),
        G("Merge Mode", "How two ArtDmx sources for one Port-Address are merged.", MergeModes),
        G("Port Kind", "Direction of a port on this node.", PortKinds),
    ];

    /// <summary>Plain-text dump of every group plus the packet table and RDM parameter names.</summary>
    public static string Describe()
    {
        var sb = new StringBuilder();
        foreach (var g in All)
        {
            sb.Append(g.Title).Append(" — ").AppendLine(g.Description);
            foreach (var o in g.Options) sb.Append("  ").Append(o.Name).Append(" (").Append(o.RawText).Append(") – ").AppendLine(o.Description);
            sb.AppendLine();
        }
        sb.AppendLine("Packets — decoded classes and minimum accepted lengths");
        foreach (var op in ArtNetPacketParser.SupportedOpCodes.OrderBy(o => (ushort)o))
            sb.Append("  ").Append(op.ToPacketName()).Append(" (0x").Append(((ushort)op).ToString("X4")).Append(") min ")
              .Append(ArtNetPacketParser.MinimumLength(op)).AppendLine(" bytes");
        sb.AppendLine();
        sb.AppendLine("RDM parameters (E1.20)");
        foreach (var kv in RdmText.Parameters.OrderBy(k => k.Key)) sb.Append("  0x").Append(kv.Key.ToString("X4")).Append(' ').AppendLine(kv.Value);
        return sb.ToString().TrimEnd();
    }
}
