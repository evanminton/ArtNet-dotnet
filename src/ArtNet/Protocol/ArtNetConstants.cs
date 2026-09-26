using System.Net;

namespace ArtNet;

/// <summary>Numbers defined by the Art-Net 4 specification (protocol 14, document revision 1.4dp).</summary>
public static class ArtNetConstants
{
    /// <summary>UDP port used as both source and destination (0x1936).</summary>
    public const int Port = 0x1936;

    /// <summary>Current protocol revision (ProtVerHi:ProtVerLo = 0:14).</summary>
    public const ushort ProtocolVersion = 14;

    /// <summary>Controllers should ignore nodes using a protocol version lower than this.</summary>
    public const ushort MinimumProtocolVersion = 14;

    /// <summary>"Art-Net" followed by a null (the ID[8] field of every packet).</summary>
    public static ReadOnlySpan<byte> Id => "Art-Net\0"u8;

    /// <summary>Length of ID[8] + OpCode.</summary>
    public const int IdAndOpCodeSize = 10;

    /// <summary>Length of ID[8] + OpCode + ProtVerHi/Lo (the header shared by most packets).</summary>
    public const int HeaderSize = 12;

    /// <summary>Number of channels in a DMX512 universe.</summary>
    public const int DmxChannels = 512;

    /// <summary>Largest Port-Address (15 bits).</summary>
    public const ushort MaxPortAddress = 0x7FFF;

    /// <summary>Ports encoded in one ArtPollReply / ArtAddress / ArtInput.</summary>
    public const int PortsPerBind = 4;

    /// <summary>Largest text payload of ArtDiagData / ArtCommand / ArtDataReply including the null.</summary>
    public const int MaxTextLength = 512;

    /// <summary>Largest ArtTodData UID count before the table is split over several packets.</summary>
    public const int MaxUidsPerTodData = 200;

    /// <summary>Largest ArtTodRequest address count.</summary>
    public const int MaxTodRequestAddresses = 32;

    /// <summary>Firmware words (Int16) carried by one ArtFirmwareMaster packet.</summary>
    public const int FirmwareBlockWords = 512;

    /// <summary>ArtVlc: DMX start code (0x91) of the ArtNzs carrying VLC data.</summary>
    public const byte VlcStartCode = 0x91;

    /// <summary>ArtVlc magic numbers (Vlc[0..2]).</summary>
    public const byte VlcManIdHi = 0x41, VlcManIdLo = 0x4C, VlcSubCode = 0x45;

    /// <summary>Largest ArtVlc payload.</summary>
    public const int MaxVlcPayload = 480;

    /// <summary>RDM start code (ArtNzs must not use it).</summary>
    public const byte RdmStartCode = 0xCC;

    /// <summary>OEM / ESTA code meaning "all" / Art-Net defined meaning (ArtTrigger, ArtCommand).</summary>
    public const ushort Global = 0xFFFF;

    /// <summary>OemUnknown from Art-NetOemCodes.h – used when no OEM code has been registered.</summary>
    public const ushort OemUnknown = 0x00FF;

    /// <summary>ESTA manufacturer ID reserved for prototypes / experimental use.</summary>
    public const ushort EstaPrototype = 0x7FF0;

    /// <summary>Primary directed broadcast address (2.x.x.x, mask 255.0.0.0).</summary>
    public static readonly IPAddress PrimaryBroadcast = IPAddress.Parse("2.255.255.255");

    /// <summary>Secondary directed broadcast address (10.x.x.x, mask 255.0.0.0).</summary>
    public static readonly IPAddress SecondaryBroadcast = IPAddress.Parse("10.255.255.255");

    /// <summary>Controllers broadcast ArtPoll every 2.5 to 3 seconds.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2.5);

    /// <summary>Maximum time a controller waits for all ArtPollReply packets.</summary>
    public static readonly TimeSpan PollReplyTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Nodes delay their ArtPollReply by a random time up to this value.</summary>
    public static readonly TimeSpan MaxPollReplyDelay = TimeSpan.FromSeconds(1);

    /// <summary>Recommended ArtDmx re-transmit time for an unchanging input (800-1000 ms).</summary>
    public static readonly TimeSpan DmxKeepAlive = TimeSpan.FromMilliseconds(900);

    /// <summary>A failed merge source is held for this long.</summary>
    public static readonly TimeSpan MergeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A node drops back to non-synchronous mode if no ArtSync arrives for this long.</summary>
    public static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Firmware transfers wait this long for ArtFirmwareReply.</summary>
    public static readonly TimeSpan FirmwareReplyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Status LEDs (Com, DMX output) time out after this long without data.</summary>
    public static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(6);

    /// <summary>DMX512 maximum refresh rate (ArtPollReply RefreshRate 0-44 all mean this).</summary>
    public const int DmxRefreshRate = 44;
}
