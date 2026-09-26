namespace ArtNet;

/// <summary>
/// A decoded ANSI E1.20 RDM message as carried by ArtRdm (i.e. without the 0xCC start code; the checksum still
/// covers it). Layout: sub-start code, length, destination UID, source UID, transaction number, port ID / response
/// type, message count, sub-device, command class, parameter ID, parameter data length, parameter data, checksum.
/// </summary>
public sealed record RdmMessage(
    RdmUid Destination,
    RdmUid Source,
    byte TransactionNumber,
    byte PortIdOrResponseType,
    byte MessageCount,
    ushort SubDevice,
    RdmCommandClass CommandClass,
    ushort ParameterId,
    byte[] ParameterData,
    ushort Checksum,
    bool ChecksumValid)
{
    public const byte SubStartCode = 0x01;

    /// <summary>Bytes before the parameter data (excluding the 0xCC start code).</summary>
    public const int HeaderSize = 23;

    /// <summary>Maximum parameter data length (E1.20).</summary>
    public const int MaxParameterData = 231;

    public bool IsResponse => CommandClass is RdmCommandClass.GetResponse or RdmCommandClass.SetResponse or RdmCommandClass.DiscoveryResponse;

    public string ResponseTypeText => IsResponse ? RdmText.ResponseType(PortIdOrResponseType) : $"Port {PortIdOrResponseType}";

    public string Summary =>
        $"{CommandClass.ToDisplayName()} {RdmText.ParameterName(ParameterId)} {Source} → {Destination}" +
        (IsResponse ? $" ({RdmText.ResponseType(PortIdOrResponseType)})" : "") +
        (ParameterData.Length > 0 ? $" · {ParameterData.Length} bytes" : "");

    /// <summary>Parses bytes that start at the sub-start code.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out RdmMessage message)
    {
        message = null!;
        if (data.Length < HeaderSize || data[0] != SubStartCode) return false;
        int pdl = data[22];
        // Message length (data[1]) counts the 0xCC start code, the header and the parameter data (max 231).
        if (pdl > MaxParameterData || data[1] != HeaderSize + 1 + pdl || data.Length < HeaderSize + pdl) return false;
        ushort checksum = data.Length >= HeaderSize + pdl + 2 ? Bin.U16BE(data, HeaderSize + pdl) : (ushort)0;
        message = new RdmMessage(
            RdmUid.Read(data[2..8]),
            RdmUid.Read(data[8..14]),
            data[14],
            data[15],
            data[16],
            Bin.U16BE(data, 17),
            (RdmCommandClass)data[19],
            Bin.U16BE(data, 20),
            data.Slice(HeaderSize, pdl).ToArray(),
            checksum,
            checksum == ComputeChecksum(data[..(HeaderSize + pdl)]));
        return true;
    }

    /// <summary>Sum of all bytes from the 0xCC start code to the end of the parameter data.</summary>
    public static ushort ComputeChecksum(ReadOnlySpan<byte> withoutStartCode)
    {
        int sum = ArtNetConstants.RdmStartCode;
        foreach (byte b in withoutStartCode) sum += b;
        return (ushort)sum;
    }

    /// <summary>Encodes an RDM message for ArtRdm (no start code, checksum appended).</summary>
    public static byte[] Build(RdmUid destination, RdmUid source, byte transactionNumber, byte portId, ushort subDevice,
        RdmCommandClass commandClass, ushort parameterId, ReadOnlySpan<byte> parameterData = default, byte messageCount = 0)
    {
        if (parameterData.Length > MaxParameterData) throw new ArgumentException("RDM parameter data is limited to 231 bytes.", nameof(parameterData));
        var b = new byte[HeaderSize + parameterData.Length + 2];
        b[0] = SubStartCode;
        b[1] = (byte)(24 + parameterData.Length); // message length includes the start code, excludes the checksum
        destination.Write(b.AsSpan(2));
        source.Write(b.AsSpan(8));
        b[14] = transactionNumber;
        b[15] = portId;
        b[16] = messageCount;
        Bin.U16BE(b, 17, subDevice);
        b[19] = (byte)commandClass;
        Bin.U16BE(b, 20, parameterId);
        b[22] = (byte)parameterData.Length;
        parameterData.CopyTo(b.AsSpan(HeaderSize));
        Bin.U16BE(b, HeaderSize + parameterData.Length, ComputeChecksum(b.AsSpan(0, HeaderSize + parameterData.Length)));
        return b;
    }

    public byte[] ToArray() => Build(Destination, Source, TransactionNumber, PortIdOrResponseType, SubDevice, CommandClass, ParameterId, ParameterData, MessageCount);

    internal void Describe(List<ArtNetField> f)
    {
        const string s = "RDM Message";
        f.Add(new(s, "Command Class", CommandClass.ToDisplayName(), Bin.HexByte((byte)CommandClass)));
        f.Add(new(s, "Parameter", RdmText.ParameterName(ParameterId), Bin.HexWord(ParameterId)));
        f.Add(new(s, "Destination UID", Destination.ToString()));
        f.Add(new(s, "Source UID", Source.ToString()));
        f.Add(new(s, "Transaction", TransactionNumber.ToString()));
        f.Add(new(s, IsResponse ? "Response Type" : "Port ID", ResponseTypeText, PortIdOrResponseType.ToString()));
        f.Add(new(s, "Message Count", MessageCount.ToString()));
        f.Add(new(s, "Sub-Device", SubDevice == 0 ? "0 (root)" : SubDevice == 0xFFFF ? "All sub-devices" : SubDevice.ToString()));
        f.Add(new(s, "Parameter Data", ParameterData.Length == 0 ? "(none)" : RdmText.FormatParameterData(ParameterId, ParameterData)));
        f.Add(new(s, "Checksum", ChecksumValid ? "Valid" : "Invalid", Bin.HexWord(Checksum)));
    }
}

/// <summary>Readable names for common RDM parameter IDs and response types (ANSI E1.20).</summary>
public static class RdmText
{
    private static readonly Dictionary<ushort, string> Pids = new()
    {
        [0x0001] = "DISC_UNIQUE_BRANCH", [0x0002] = "DISC_MUTE", [0x0003] = "DISC_UN_MUTE",
        [0x0010] = "PROXIED_DEVICES", [0x0011] = "PROXIED_DEVICE_COUNT", [0x0015] = "COMMS_STATUS",
        [0x0020] = "QUEUED_MESSAGE", [0x0030] = "STATUS_MESSAGES", [0x0031] = "STATUS_ID_DESCRIPTION",
        [0x0032] = "CLEAR_STATUS_ID", [0x0033] = "SUB_DEVICE_STATUS_REPORT_THRESHOLD",
        [0x0050] = "SUPPORTED_PARAMETERS", [0x0051] = "PARAMETER_DESCRIPTION", [0x0060] = "DEVICE_INFO",
        [0x0070] = "PRODUCT_DETAIL_ID_LIST", [0x0080] = "DEVICE_MODEL_DESCRIPTION", [0x0081] = "MANUFACTURER_LABEL",
        [0x0082] = "DEVICE_LABEL", [0x0090] = "FACTORY_DEFAULTS", [0x00A0] = "LANGUAGE_CAPABILITIES", [0x00B0] = "LANGUAGE",
        [0x00C0] = "SOFTWARE_VERSION_LABEL", [0x00C1] = "BOOT_SOFTWARE_VERSION_ID", [0x00C2] = "BOOT_SOFTWARE_VERSION_LABEL",
        [0x00E0] = "DMX_PERSONALITY", [0x00E1] = "DMX_PERSONALITY_DESCRIPTION", [0x00F0] = "DMX_START_ADDRESS",
        [0x0120] = "SLOT_INFO", [0x0121] = "SLOT_DESCRIPTION", [0x0122] = "DEFAULT_SLOT_VALUE",
        [0x0200] = "SENSOR_DEFINITION", [0x0201] = "SENSOR_VALUE", [0x0202] = "RECORD_SENSORS",
        [0x0400] = "DEVICE_HOURS", [0x0401] = "LAMP_HOURS", [0x0402] = "LAMP_STRIKES", [0x0403] = "LAMP_STATE",
        [0x0404] = "LAMP_ON_MODE", [0x0405] = "DEVICE_POWER_CYCLES", [0x0500] = "DISPLAY_INVERT", [0x0501] = "DISPLAY_LEVEL",
        [0x0600] = "PAN_INVERT", [0x0601] = "TILT_INVERT", [0x0602] = "PAN_TILT_SWAP", [0x0603] = "REAL_TIME_CLOCK",
        [0x1000] = "IDENTIFY_DEVICE", [0x1001] = "RESET_DEVICE", [0x1010] = "POWER_STATE", [0x1020] = "PERFORM_SELFTEST",
        [0x1021] = "SELF_TEST_DESCRIPTION", [0x1030] = "CAPTURE_PRESET", [0x1031] = "PRESET_PLAYBACK",
    };

    /// <summary>Well-known parameter IDs and names.</summary>
    public static IReadOnlyDictionary<ushort, string> Parameters => Pids;

    public static string ParameterName(ushort pid) =>
        Pids.TryGetValue(pid, out var n) ? n : pid >= 0x8000 && pid <= 0xFFDF ? $"Manufacturer PID 0x{pid:X4}" : $"PID 0x{pid:X4}";

    /// <summary>Looks up a PID by name (case insensitive, '_' optional) or number.</summary>
    public static bool TryParseParameter(string text, out ushort pid)
    {
        pid = 0;
        var t = text.Trim();
        var norm = t.Replace("_", "").Replace(" ", "");
        foreach (var kv in Pids)
            if (kv.Value.Replace("_", "").Equals(norm, StringComparison.OrdinalIgnoreCase)) { pid = kv.Key; return true; }
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out pid)
            : ushort.TryParse(t, out pid);
    }

    public static string ResponseType(byte type) => type switch
    {
        0x00 => "ACK",
        0x01 => "ACK_TIMER",
        0x02 => "NACK_REASON",
        0x03 => "ACK_OVERFLOW",
        _ => $"Response type {type}",
    };

    /// <summary>Formats parameter data: text PIDs as text, 16-bit PIDs as numbers, anything else as hex.</summary>
    public static string FormatParameterData(ushort pid, ReadOnlySpan<byte> data)
    {
        switch (pid)
        {
            case 0x0080 or 0x0081 or 0x0082 or 0x00C0 or 0x00C2 or 0x0021 or 0x1021:
                return $"\"{System.Text.Encoding.ASCII.GetString(data).TrimEnd('\0')}\"";
            case 0x00F0 when data.Length >= 2:
                return $"DMX start address {(data[0] << 8) | data[1]}";
            case 0x1000 when data.Length >= 1:
                return data[0] != 0 ? "Identify on" : "Identify off";
            case 0x0060 when data.Length >= 19:
                return $"RDM {data[0]}.{data[1]}, model 0x{(data[2] << 8) | data[3]:X4}, category 0x{(data[4] << 8) | data[5]:X4}, " +
                       $"software 0x{Bin.U32BE(data, 6):X8}, footprint {(data[10] << 8) | data[11]}, personality {data[12]}/{data[13]}, " +
                       $"start {(data[14] << 8) | data[15]}, sub-devices {(data[16] << 8) | data[17]}, sensors {data[18]}";
            case 0x0050:
            {
                var list = new List<string>();
                for (int i = 0; i + 1 < data.Length; i += 2) list.Add(ParameterName((ushort)((data[i] << 8) | data[i + 1])));
                return string.Join(", ", list);
            }
            default:
                return Bin.Hex(data, 64);
        }
    }
}
