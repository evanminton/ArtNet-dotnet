namespace ArtNet;

/// <summary>Table 1 – OpCodes (transmitted low byte first).</summary>
public enum ArtNetOpCode : ushort
{
    Poll = 0x2000,
    PollReply = 0x2100,
    DiagData = 0x2300,
    Command = 0x2400,
    DataRequest = 0x2700,
    DataReply = 0x2800,
    /// <summary>OpOutput / OpDmx.</summary>
    Dmx = 0x5000,
    Nzs = 0x5100,
    Sync = 0x5200,
    Address = 0x6000,
    Input = 0x7000,
    TodRequest = 0x8000,
    TodData = 0x8100,
    TodControl = 0x8200,
    Rdm = 0x8300,
    RdmSub = 0x8400,
    Media = 0x9000,
    MediaPatch = 0x9100,
    MediaControl = 0x9200,
    MediaControlReply = 0x9300,
    TimeCode = 0x9700,
    TimeSync = 0x9800,
    Trigger = 0x9900,
    Directory = 0x9A00,
    DirectoryReply = 0x9B00,
    VideoSetup = 0xA010,
    VideoPalette = 0xA020,
    VideoData = 0xA040,
    MacMaster = 0xF000,
    MacSlave = 0xF100,
    FirmwareMaster = 0xF200,
    FirmwareReply = 0xF300,
    FileTnMaster = 0xF400,
    FileFnMaster = 0xF500,
    FileFnReply = 0xF600,
    IpProg = 0xF800,
    IpProgReply = 0xF900,
}

/// <summary>Table 3 – NodeReport codes.</summary>
public enum ArtNetNodeReportCode : ushort
{
    Debug = 0x0000,
    PowerOk = 0x0001,
    PowerFail = 0x0002,
    SocketWr1 = 0x0003,
    ParseFail = 0x0004,
    UdpFail = 0x0005,
    ShNameOk = 0x0006,
    LoNameOk = 0x0007,
    DmxError = 0x0008,
    DmxUdpFull = 0x0009,
    DmxRxFull = 0x000A,
    SwitchErr = 0x000B,
    ConfigErr = 0x000C,
    DmxShort = 0x000D,
    FirmwareFail = 0x000E,
    UserFail = 0x000F,
    FactoryRes = 0x0010,
}

/// <summary>Table 4 – Style codes (general functionality of the device).</summary>
public enum ArtNetStyle : byte
{
    Node = 0x00,
    Controller = 0x01,
    Media = 0x02,
    Route = 0x03,
    Backup = 0x04,
    Config = 0x05,
    Visual = 0x06,
}

/// <summary>Table 5 – Diagnostics priority codes (ArtPoll, ArtDiagData).</summary>
public enum ArtNetDiagnosticPriority : byte
{
    Low = 0x10,
    Medium = 0x40,
    High = 0x80,
    Critical = 0xE0,
    Volatile = 0xF0,
}

/// <summary>Table 4a – DataRequest codes (ArtDataRequest / ArtDataReply). 0x8000-0xFFFF are manufacturer specific.</summary>
public enum ArtNetDataRequestCode : ushort
{
    Poll = 0x0000,
    UrlProduct = 0x0001,
    UrlUserGuide = 0x0002,
    UrlSupport = 0x0003,
    UrlPersonalityUdr = 0x0004,
    UrlPersonalityGdtf = 0x0005,
    ManufacturerSpecific = 0x8000,
}

/// <summary>ArtPoll → Flags.</summary>
[Flags]
public enum ArtPollFlags : byte
{
    None = 0,
    /// <summary>Bit 1: send ArtPollReply whenever node conditions change.</summary>
    ReplyOnChange = 0x02,
    /// <summary>Bit 2: send me diagnostics messages.</summary>
    Diagnostics = 0x04,
    /// <summary>Bit 3: diagnostics are unicast (else broadcast), if bit 2 is set.</summary>
    DiagnosticsUnicast = 0x08,
    /// <summary>Bit 4: disable VLC transmission.</summary>
    DisableVlc = 0x10,
    /// <summary>Bit 5: targeted mode – only nodes subscribed to the target Port-Address range reply.</summary>
    TargetedMode = 0x20,
}

/// <summary>ArtPollReply → Status1 single-bit flags (bits 7-4 are <see cref="ArtNetIndicatorState"/> and <see cref="ArtNetPortAddressAuthority"/>).</summary>
[Flags]
public enum ArtNetStatus1 : byte
{
    None = 0,
    UbeaPresent = 0x01,
    RdmCapable = 0x02,
    BootedFromRom = 0x04,
}

/// <summary>ArtPollReply → Status1 bits 7-6.</summary>
public enum ArtNetIndicatorState : byte
{
    Unknown = 0,
    Locate = 1,
    Mute = 2,
    Normal = 3,
}

/// <summary>ArtPollReply → Status1 bits 5-4 (Port-Address programming authority).</summary>
public enum ArtNetPortAddressAuthority : byte
{
    Unknown = 0,
    FrontPanel = 1,
    Network = 2,
    NotUsed = 3,
}

/// <summary>ArtPollReply → Status2.</summary>
[Flags]
public enum ArtNetStatus2 : byte
{
    None = 0,
    WebConfiguration = 0x01,
    DhcpConfigured = 0x02,
    DhcpCapable = 0x04,
    PortAddress15Bit = 0x08,
    SacnSwitchable = 0x10,
    Squawking = 0x20,
    OutputStyleSwitchable = 0x40,
    RdmControllable = 0x80,
}

/// <summary>ArtPollReply → Status3 single-bit flags (bits 7-6 are <see cref="ArtNetFailsafeState"/>).</summary>
[Flags]
public enum ArtNetStatus3 : byte
{
    None = 0,
    BackgroundDiscoveryControllable = 0x01,
    BackgroundQueueSupported = 0x02,
    RdmNet = 0x04,
    PortDirectionSwitchable = 0x08,
    Llrp = 0x10,
    FailsafeProgrammable = 0x20,
}

/// <summary>ArtPollReply → Status3 bits 7-6: how outputs behave when network data is lost.</summary>
public enum ArtNetFailsafeState : byte
{
    Hold = 0,
    Zero = 1,
    Full = 2,
    Scene = 3,
}

/// <summary>ArtPollReply → PortTypes[] bits 7-6.</summary>
[Flags]
public enum ArtNetPortDirection : byte
{
    None = 0,
    /// <summary>Bit 6: this port can input onto the Art-Net network.</summary>
    Input = 0x40,
    /// <summary>Bit 7: this port can output data from the Art-Net network.</summary>
    Output = 0x80,
}

/// <summary>ArtPollReply → PortTypes[] bits 5-0.</summary>
public enum ArtNetPortProtocol : byte
{
    Dmx512 = 0,
    Midi = 1,
    Avab = 2,
    ColortranCmx = 3,
    Adb625 = 4,
    ArtNet = 5,
    Dali = 6,
}

/// <summary>ArtPollReply → GoodInput[].</summary>
[Flags]
public enum ArtNetGoodInput : byte
{
    None = 0,
    /// <summary>Bit 0: set = converts to sACN, clear = converts to Art-Net.</summary>
    ConvertToSacn = 0x01,
    ReceiveErrors = 0x04,
    Disabled = 0x08,
    TextPackets = 0x10,
    Sips = 0x20,
    TestPackets = 0x40,
    DataReceived = 0x80,
}

/// <summary>ArtPollReply → GoodOutputA[].</summary>
[Flags]
public enum ArtNetGoodOutputA : byte
{
    None = 0,
    /// <summary>Bit 0: set = converts from sACN, clear = converts from Art-Net.</summary>
    ConvertFromSacn = 0x01,
    MergeLtp = 0x02,
    OutputShort = 0x04,
    Merging = 0x08,
    TextPackets = 0x10,
    Sips = 0x20,
    TestPackets = 0x40,
    DataTransmitted = 0x80,
}

/// <summary>ArtPollReply → GoodOutputB[].</summary>
[Flags]
public enum ArtNetGoodOutputB : byte
{
    None = 0,
    BackgroundDiscoveryDisabled = 0x10,
    DiscoveryNotRunning = 0x20,
    /// <summary>Set = continuous output style, clear = delta.</summary>
    ContinuousOutput = 0x40,
    RdmDisabled = 0x80,
}

/// <summary>ArtPollReply → BackgroundQueuePolicy (5-250 manufacturer defined).</summary>
public enum ArtNetBackgroundQueuePolicy : byte
{
    StatusNone = 0,
    StatusAdvisory = 1,
    StatusWarning = 2,
    StatusError = 3,
    Disabled = 4,
}

/// <summary>ArtAddress → Command.</summary>
public enum ArtNetAddressCommand : byte
{
    None = 0x00,
    CancelMerge = 0x01,
    LedNormal = 0x02,
    LedMute = 0x03,
    LedLocate = 0x04,
    ResetRxFlags = 0x05,
    AnalysisOn = 0x06,
    AnalysisOff = 0x07,
    FailHold = 0x08,
    FailZero = 0x09,
    FailFull = 0x0A,
    FailScene = 0x0B,
    FailRecord = 0x0C,
    MergeLtp0 = 0x10, MergeLtp1 = 0x11, MergeLtp2 = 0x12, MergeLtp3 = 0x13,
    DirectionTx0 = 0x20, DirectionTx1 = 0x21, DirectionTx2 = 0x22, DirectionTx3 = 0x23,
    DirectionRx0 = 0x30, DirectionRx1 = 0x31, DirectionRx2 = 0x32, DirectionRx3 = 0x33,
    MergeHtp0 = 0x50, MergeHtp1 = 0x51, MergeHtp2 = 0x52, MergeHtp3 = 0x53,
    ArtNetSel0 = 0x60, ArtNetSel1 = 0x61, ArtNetSel2 = 0x62, ArtNetSel3 = 0x63,
    AcnSel0 = 0x70, AcnSel1 = 0x71, AcnSel2 = 0x72, AcnSel3 = 0x73,
    ClearOp0 = 0x90, ClearOp1 = 0x91, ClearOp2 = 0x92, ClearOp3 = 0x93,
    StyleDelta0 = 0xA0, StyleDelta1 = 0xA1, StyleDelta2 = 0xA2, StyleDelta3 = 0xA3,
    StyleConst0 = 0xB0, StyleConst1 = 0xB1, StyleConst2 = 0xB2, StyleConst3 = 0xB3,
    RdmEnable0 = 0xC0, RdmEnable1 = 0xC1, RdmEnable2 = 0xC2, RdmEnable3 = 0xC3,
    RdmDisable0 = 0xD0, RdmDisable1 = 0xD1, RdmDisable2 = 0xD2, RdmDisable3 = 0xD3,
    Bqp0 = 0xE0, Bqp1 = 0xE1, Bqp2 = 0xE2, Bqp3 = 0xE3, Bqp4 = 0xE4, Bqp5 = 0xE5, Bqp6 = 0xE6, Bqp7 = 0xE7,
    Bqp8 = 0xE8, Bqp9 = 0xE9, Bqp10 = 0xEA, Bqp11 = 0xEB, Bqp12 = 0xEC, Bqp13 = 0xED, Bqp14 = 0xEE, Bqp15 = 0xEF,
}

/// <summary>ArtIpProg → Command. All bits clear = enquiry only.</summary>
[Flags]
public enum ArtIpProgCommand : byte
{
    None = 0,
    /// <summary>Bit 0: program port (deprecated).</summary>
    ProgramPort = 0x01,
    ProgramSubnetMask = 0x02,
    ProgramIp = 0x04,
    ResetToDefault = 0x08,
    ProgramGateway = 0x10,
    /// <summary>Bit 6: enable DHCP (lower bits ignored).</summary>
    EnableDhcp = 0x40,
    /// <summary>Bit 7: enable any programming.</summary>
    EnableProgramming = 0x80,
}

/// <summary>ArtIpProgReply → Status.</summary>
[Flags]
public enum ArtIpProgStatus : byte
{
    None = 0,
    DhcpEnabled = 0x40,
}

/// <summary>ArtTimeCode → Type.</summary>
public enum ArtNetTimeCodeType : byte
{
    Film = 0,
    Ebu = 1,
    DropFrame = 2,
    Smpte = 3,
}

/// <summary>Table 7 – ArtTrigger Key values (when OEM = 0xFFFF).</summary>
public enum ArtNetTriggerKey : byte
{
    Ascii = 0,
    Macro = 1,
    Soft = 2,
    Show = 3,
}

/// <summary>ArtFirmwareMaster → Type.</summary>
public enum ArtNetFirmwareMasterType : byte
{
    FirmFirst = 0x00,
    FirmCont = 0x01,
    FirmLast = 0x02,
    UbeaFirst = 0x03,
    UbeaCont = 0x04,
    UbeaLast = 0x05,
}

/// <summary>ArtFirmwareReply → Type.</summary>
public enum ArtNetFirmwareReplyType : byte
{
    BlockGood = 0x00,
    AllGood = 0x01,
    Fail = 0xFF,
}

/// <summary>ArtTodRequest → Command.</summary>
public enum ArtNetTodRequestCommand : byte
{
    TodFull = 0x00,
}

/// <summary>ArtTodData → CommandResponse.</summary>
public enum ArtNetTodDataCommand : byte
{
    TodFull = 0x00,
    TodNak = 0xFF,
}

/// <summary>ArtTodControl → Command.</summary>
public enum ArtNetTodControlCommand : byte
{
    None = 0x00,
    Flush = 0x01,
    End = 0x02,
    IncrementalOn = 0x03,
    IncrementalOff = 0x04,
}

/// <summary>ArtRdm → Command.</summary>
public enum ArtNetRdmCommand : byte
{
    Process = 0x00,
}

/// <summary>ArtTodData / ArtRdm / ArtRdmSub → RdmVer.</summary>
public enum ArtNetRdmVersion : byte
{
    Draft = 0x00,
    Standard = 0x01,
}

/// <summary>RDM command class (ANSI E1.20) used by ArtRdmSub.</summary>
public enum RdmCommandClass : byte
{
    Discovery = 0x10,
    DiscoveryResponse = 0x11,
    Get = 0x20,
    GetResponse = 0x21,
    Set = 0x30,
    SetResponse = 0x31,
}

/// <summary>ArtVlc → Flags.</summary>
[Flags]
public enum ArtVlcFlags : byte
{
    None = 0,
    Beacon = 0x20,
    Reply = 0x40,
    Ieee = 0x80,
}

/// <summary>ArtVlc → PayLanguage.</summary>
public enum ArtVlcPayloadLanguage : ushort
{
    BeaconUrl = 0x0000,
    BeaconText = 0x0001,
    BeaconLocationId = 0x0002,
}

/// <summary>How two ArtDmx sources for the same Port-Address are combined.</summary>
public enum ArtNetMergeMode : byte
{
    /// <summary>Highest takes precedence (default).</summary>
    Htp = 0,
    /// <summary>Latest takes precedence.</summary>
    Ltp = 1,
}

/// <summary>Direction of a port on this node.</summary>
public enum ArtNetPortKind : byte
{
    /// <summary>Outputs DMX512 from the network (receives ArtDmx).</summary>
    Output = 0,
    /// <summary>Inputs DMX512 onto the network (transmits ArtDmx).</summary>
    Input = 1,
}
