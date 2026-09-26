using System.Diagnostics.CodeAnalysis;

namespace ArtNet;

/// <summary>Decodes UDP payloads into typed Art-Net packets.</summary>
public static class ArtNetPacketParser
{
    private readonly record struct Entry(int MinSize, Func<ArtNetPacket> Create);

    private static readonly Dictionary<ArtNetOpCode, Entry> Table = new()
    {
        [ArtNetOpCode.Poll] = new(ArtPollPacket.MinSize, () => new ArtPollPacket()),
        [ArtNetOpCode.PollReply] = new(ArtPollReplyPacket.MinSize, () => new ArtPollReplyPacket()),
        [ArtNetOpCode.IpProg] = new(ArtIpProgPacket.MinSize, () => new ArtIpProgPacket()),
        [ArtNetOpCode.IpProgReply] = new(ArtIpProgReplyPacket.MinSize, () => new ArtIpProgReplyPacket()),
        [ArtNetOpCode.Address] = new(ArtAddressPacket.MinSize, () => new ArtAddressPacket()),
        [ArtNetOpCode.Input] = new(ArtInputPacket.MinSize, () => new ArtInputPacket()),
        [ArtNetOpCode.DataRequest] = new(ArtDataRequestPacket.MinSize, () => new ArtDataRequestPacket()),
        [ArtNetOpCode.DataReply] = new(ArtDataReplyPacket.MinSize, () => new ArtDataReplyPacket()),
        [ArtNetOpCode.DiagData] = new(ArtDiagDataPacket.MinSize, () => new ArtDiagDataPacket()),
        [ArtNetOpCode.TimeCode] = new(ArtTimeCodePacket.MinSize, () => new ArtTimeCodePacket()),
        [ArtNetOpCode.Command] = new(ArtCommandPacket.MinSize, () => new ArtCommandPacket()),
        [ArtNetOpCode.Trigger] = new(ArtTriggerPacket.MinSize, () => new ArtTriggerPacket()),
        [ArtNetOpCode.Dmx] = new(ArtDmxPacket.MinSize, () => new ArtDmxPacket()),
        [ArtNetOpCode.Sync] = new(ArtSyncPacket.MinSize, () => new ArtSyncPacket()),
        [ArtNetOpCode.Nzs] = new(ArtNzsPacket.MinSize, () => new ArtNzsPacket()),
        [ArtNetOpCode.FirmwareMaster] = new(ArtFirmwareMasterPacket.MinSize, () => new ArtFirmwareMasterPacket()),
        [ArtNetOpCode.FirmwareReply] = new(ArtFirmwareReplyPacket.MinSize, () => new ArtFirmwareReplyPacket()),
        [ArtNetOpCode.TodRequest] = new(ArtTodRequestPacket.MinSize, () => new ArtTodRequestPacket()),
        [ArtNetOpCode.TodData] = new(ArtTodDataPacket.MinSize, () => new ArtTodDataPacket()),
        [ArtNetOpCode.TodControl] = new(ArtTodControlPacket.MinSize, () => new ArtTodControlPacket()),
        [ArtNetOpCode.Rdm] = new(ArtRdmPacket.MinSize, () => new ArtRdmPacket()),
        [ArtNetOpCode.RdmSub] = new(ArtRdmSubPacket.MinSize, () => new ArtRdmSubPacket()),
    };

    /// <summary>OpCodes decoded into dedicated classes.</summary>
    public static IReadOnlyCollection<ArtNetOpCode> SupportedOpCodes => Table.Keys;

    /// <summary>Minimum accepted length for an OpCode (10 for OpCodes without a dedicated class).</summary>
    public static int MinimumLength(ArtNetOpCode opCode) =>
        Table.TryGetValue(opCode, out var e) ? e.MinSize : ArtNetConstants.IdAndOpCodeSize;

    /// <summary>True when the buffer starts with "Art-Net\0" and has room for the OpCode (the spec's data integrity check).</summary>
    public static bool IsArtNet(ReadOnlySpan<byte> data) =>
        data.Length >= ArtNetConstants.IdAndOpCodeSize && data[..8].SequenceEqual(ArtNetConstants.Id);

    /// <summary>Reads the OpCode without decoding the packet.</summary>
    public static bool TryGetOpCode(ReadOnlySpan<byte> data, out ArtNetOpCode opCode)
    {
        opCode = default;
        if (!IsArtNet(data)) return false;
        opCode = (ArtNetOpCode)Bin.U16LE(data, 8);
        return true;
    }

    /// <summary>
    /// Decodes a datagram. Returns false for non Art-Net data and packets shorter than the minimum length.
    /// OpCodes without a dedicated class become <see cref="ArtUnknownPacket"/> when <paramref name="includeUnknown"/> is true.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ArtNetPacket? packet, bool includeUnknown = true)
    {
        packet = null;
        if (!TryGetOpCode(data, out var op)) return false;

        if (!Table.TryGetValue(op, out var entry))
        {
            if (!includeUnknown) return false;
            var unknown = new ArtUnknownPacket(op);
            unknown.Read(data);
            packet = unknown;
            return true;
        }

        if (data.Length < entry.MinSize) return false;
        packet = op == ArtNetOpCode.Nzs && ArtVlcPacket.IsVlc(data) ? new ArtVlcPacket() : entry.Create();
        try
        {
            packet.Read(data);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or FormatException)
        {
            packet = null;
            return false;
        }
        return true;
    }

    /// <summary>Decodes a datagram or throws <see cref="FormatException"/>.</summary>
    public static ArtNetPacket Parse(ReadOnlySpan<byte> data) =>
        TryParse(data, out var p) ? p : throw new FormatException(IsArtNet(data)
            ? $"Art-Net packet too short for its OpCode ({data.Length} bytes)."
            : "Not an Art-Net packet (ID must be \"Art-Net\\0\").");

    /// <summary>Decodes and casts; returns null when the datagram is a different packet type.</summary>
    public static T? TryParse<T>(ReadOnlySpan<byte> data) where T : ArtNetPacket =>
        TryParse(data, out var p) ? p as T : null;
}
