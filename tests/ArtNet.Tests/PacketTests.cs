using System.Net;
using System.Text;

namespace ArtNet.Tests;

public class PacketTests
{
    private static T RoundTrip<T>(T packet) where T : ArtNetPacket
    {
        var bytes = packet.ToArray();
        Assert.Equal(packet.Size, bytes.Length);
        Assert.Equal("Art-Net\0", Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.Equal((ushort)packet.OpCode, (ushort)(bytes[8] | (bytes[9] << 8)));
        if (packet.HasProtocolVersion)
        {
            Assert.Equal(0, bytes[10]);
            Assert.Equal(14, bytes[11]);
        }
        Assert.True(ArtNetPacketParser.TryParse(bytes, out var parsed));
        var typed = Assert.IsType<T>(parsed);
        Assert.Equal(bytes, typed.ToArray());
        Assert.NotEmpty(typed.Describe());
        Assert.False(string.IsNullOrWhiteSpace(typed.ToString()));
        Assert.False(string.IsNullOrWhiteSpace(typed.Summary));
        return typed;
    }

    [Fact]
    public void Header_OpCodeLowByteFirst_VersionHiLo()
    {
        var b = new ArtPollPacket().ToArray();
        Assert.Equal(0x00, b[8]);
        Assert.Equal(0x20, b[9]);
        Assert.Equal(0, b[10]);
        Assert.Equal(14, b[11]);
    }

    [Fact]
    public void Parser_RejectsWrongId_AndShortPackets()
    {
        var b = new ArtPollPacket().ToArray();
        b[0] = (byte)'X';
        Assert.False(ArtNetPacketParser.TryParse(b, out _));
        var reply = new ArtPollReplyPacket().ToArray();
        Assert.False(ArtNetPacketParser.TryParse(reply.AsSpan(0, 206), out _));
        Assert.True(ArtNetPacketParser.TryParse(reply.AsSpan(0, 207), out var p));
        Assert.IsType<ArtPollReplyPacket>(p);
    }

    [Fact]
    public void Poll_Accepts14Bytes_MissingFieldsZero()
    {
        var full = new ArtPollPacket { Flags = ArtPollFlags.ReplyOnChange | ArtPollFlags.Diagnostics, DiagPriority = ArtNetDiagnosticPriority.High, Oem = 0x1234 }.ToArray();
        Assert.Equal(22, full.Length);
        Assert.True(ArtNetPacketParser.TryParse(full.AsSpan(0, 14), out var p));
        var poll = Assert.IsType<ArtPollPacket>(p);
        Assert.Equal(ArtPollFlags.ReplyOnChange | ArtPollFlags.Diagnostics, poll.Flags);
        Assert.Equal(ArtNetDiagnosticPriority.High, poll.DiagPriority);
        Assert.Equal(0, poll.Oem);
        Assert.False(ArtNetPacketParser.TryParse(full.AsSpan(0, 13), out _));
    }

    [Fact]
    public void Poll_RoundTrip_Targeted()
    {
        var p = RoundTrip(ArtPollPacket.Targeted(new PortAddress(10), new PortAddress(20), ArtPollFlags.ReplyOnChange));
        Assert.True(p.TargetedMode);
        Assert.Equal(20, p.TargetPortAddressTop);
        Assert.Equal(10, p.TargetPortAddressBottom);
        Assert.True(p.Targets(new PortAddress(15)));
        Assert.False(p.Targets(new PortAddress(21)));
        Assert.True(new ArtPollPacket().Targets(new PortAddress(21)));
        var b = p.ToArray();
        Assert.Equal(0x22, b[12]);
        Assert.Equal(20, b[15]);
        Assert.Equal(10, b[17]);
    }

    [Fact]
    public void PollReply_Layout_MatchesSpec()
    {
        var r = new ArtPollReplyPacket
        {
            IpAddress = IPAddress.Parse("2.168.52.118"),
            FirmwareVersion = 0x0102,
            NetSwitch = 3,
            SubSwitch = 4,
            Oem = 0xABCD,
            EstaManufacturer = 0x4142,
            ShortName = "Short",
            LongName = "Long name",
            NodeReport = ArtPollReplyPacket.FormatNodeReport(ArtNetNodeReportCode.PowerOk, 12, "ok"),
            Style = ArtNetStyle.Node,
            BindIndex = 2,
            User = 0x0506,
            RefreshRate = 100,
        };
        r.SetPort(0, ArtNetPortDirection.Output, ArtNetPortProtocol.Dmx512, 0, 5);
        r.SetPort(1, ArtNetPortDirection.Input, ArtNetPortProtocol.Dmx512, 6, 0);
        r.Mac[0] = 0x12; r.Mac[5] = 0x76;
        r.IndicatorState = ArtNetIndicatorState.Locate;
        r.PortAddressAuthority = ArtNetPortAddressAuthority.Network;
        r.Status1Flags = ArtNetStatus1.RdmCapable;
        r.FailsafeState = ArtNetFailsafeState.Full;
        r.Status3Flags = ArtNetStatus3.Llrp;
        var b = r.ToArray();

        Assert.Equal(239, b.Length);
        Assert.Equal(0x00, b[8]); Assert.Equal(0x21, b[9]);
        Assert.Equal(new byte[] { 2, 168, 52, 118 }, b[10..14]);
        Assert.Equal(0x36, b[14]); Assert.Equal(0x19, b[15]); // port low byte first
        Assert.Equal(1, b[16]); Assert.Equal(2, b[17]);
        Assert.Equal(3, b[18]); Assert.Equal(4, b[19]);
        Assert.Equal(0xAB, b[20]); Assert.Equal(0xCD, b[21]);
        Assert.Equal(0b01_10_0_0_1_0, b[23]);
        Assert.Equal(0x42, b[24]); Assert.Equal(0x41, b[25]); // ESTA Lo then Hi
        Assert.Equal("Short", Encoding.ASCII.GetString(b, 26, 5));
        Assert.Equal(0, b[26 + 17]);
        Assert.Equal("Long name", Encoding.ASCII.GetString(b, 44, 9));
        Assert.StartsWith("#0001 [0012] ok", Encoding.ASCII.GetString(b, 108, 64));
        Assert.Equal(0, b[172]); Assert.Equal(2, b[173]);
        Assert.Equal(0x80, b[174]); Assert.Equal(0x40, b[175]);
        Assert.Equal(6, b[187]);
        Assert.Equal(5, b[190]);
        Assert.Equal((byte)ArtNetStyle.Node, b[200]);
        Assert.Equal(0x12, b[201]); Assert.Equal(0x76, b[206]);
        Assert.Equal(2, b[211]);
        Assert.Equal(0b10_010000, b[217]);
        Assert.Equal(5, b[224]); Assert.Equal(6, b[225]);
        Assert.Equal(0, b[226]); Assert.Equal(100, b[227]);

        var p = RoundTrip(r);
        Assert.Equal(ArtNetIndicatorState.Locate, p.IndicatorState);
        Assert.Equal(ArtNetPortAddressAuthority.Network, p.PortAddressAuthority);
        Assert.Equal(ArtNetFailsafeState.Full, p.FailsafeState);
        Assert.Equal(ArtNetStatus3.Llrp, p.Status3Flags);
        Assert.Equal(new PortAddress(3, 4, 5), p.GetOutputAddress(0));
        Assert.Equal(new PortAddress(3, 4, 6), p.GetInputAddress(1));
        Assert.True(p.IsSubscribedTo(new PortAddress(3, 4, 5)));
        Assert.True(p.IsSubscribedTo(new PortAddress(3, 4, 6)));
        Assert.False(p.IsSubscribedTo(new PortAddress(3, 4, 0)));
        Assert.True(p.OutputsAddress(new PortAddress(3, 4, 5)));
        Assert.Equal(2, p.Ports.Count);
        Assert.Equal("00:00:00:00:00:00".Length, p.MacAddress.Length);
    }

    [Fact]
    public void PollReply_NoProtocolVersionField()
    {
        var b = new ArtPollReplyPacket { IpAddress = IPAddress.Parse("10.0.0.1") }.ToArray();
        Assert.Equal(10, b[10]); // IP starts right after the OpCode
    }

    [Fact]
    public void NodeReport_FormatAndParse()
    {
        var s = ArtPollReplyPacket.FormatNodeReport(ArtNetNodeReportCode.DmxShort, 10023, "Short on port 1");
        Assert.Equal("#000d [0023] Short on port 1", s);
        Assert.True(ArtPollReplyPacket.TryParseNodeReport(s, out var code, out int counter, out string text));
        Assert.Equal(ArtNetNodeReportCode.DmxShort, code);
        Assert.Equal(23, counter);
        Assert.Equal("Short on port 1", text);
        Assert.False(ArtPollReplyPacket.TryParseNodeReport("hello", out _, out _, out _));
    }

    [Fact]
    public void IpProg_RoundTrip_Offsets()
    {
        var p = RoundTrip(ArtIpProgPacket.Program(IPAddress.Parse("2.1.2.3"), IPAddress.Parse("255.0.0.0"), IPAddress.Parse("2.0.0.1")));
        var b = p.ToArray();
        Assert.Equal(34, b.Length);
        Assert.Equal(0x80 | 0x04 | 0x02 | 0x10, b[14]);
        Assert.Equal(new byte[] { 2, 1, 2, 3 }, b[16..20]);
        Assert.Equal(new byte[] { 255, 0, 0, 0 }, b[20..24]);
        Assert.Equal(new byte[] { 2, 0, 0, 1 }, b[26..30]);
        Assert.True(ArtIpProgPacket.Enquiry().IsEnquiry);
    }

    [Fact]
    public void IpProgReply_RoundTrip_Offsets()
    {
        var p = RoundTrip(new ArtIpProgReplyPacket
        {
            ProgIp = IPAddress.Parse("10.1.2.3"), ProgSubnetMask = IPAddress.Parse("255.255.0.0"),
            ProgDefaultGateway = IPAddress.Parse("10.1.0.1"), Status = ArtIpProgStatus.DhcpEnabled,
        });
        var b = p.ToArray();
        Assert.Equal(10, b[16]);
        Assert.Equal(0x40, b[26]);
        Assert.Equal(new byte[] { 10, 1, 0, 1 }, b[28..32]);
        Assert.True(p.DhcpEnabled);
    }

    [Fact]
    public void Address_RoundTrip_Offsets()
    {
        var a = new ArtAddressPacket { ShortName = "Stage Left", LongName = "Stage left gateway", BindIndex = 3, Command = ArtNetAddressCommand.LedLocate, AcnPriority = 150 }
            .SetOutputAddress(new PortAddress(1, 2, 3));
        var p = RoundTrip(a);
        var b = p.ToArray();
        Assert.Equal(107, b.Length);
        Assert.Equal(0x81, b[12]);
        Assert.Equal(3, b[13]);
        Assert.Equal("Stage Left", Encoding.ASCII.GetString(b, 14, 10));
        Assert.Equal("Stage left gateway", Encoding.ASCII.GetString(b, 32, 18));
        Assert.Equal(0x7F, b[96]);
        Assert.Equal(0x83, b[100]);
        Assert.Equal(0x82, b[104]);
        Assert.Equal(150, b[105]);
        Assert.Equal(0x04, b[106]);
        Assert.Equal(0x87, ArtAddressPacket.Program(7));
    }

    [Fact]
    public void Input_RoundTrip()
    {
        var i = new ArtInputPacket { BindIndex = 2, NumPorts = 4 };
        i.SetDisabled(0, true);
        i.SetDisabled(2, true);
        var p = RoundTrip(i);
        var b = p.ToArray();
        Assert.Equal(20, b.Length);
        Assert.Equal(2, b[13]);
        Assert.Equal(4, b[15]);
        Assert.Equal(new byte[] { 1, 0, 1, 0 }, b[16..20]);
    }

    [Fact]
    public void DataRequest_And_Reply_RoundTrip()
    {
        var req = RoundTrip(new ArtDataRequestPacket { EstaManufacturer = 0x4142, Oem = 0x00FF, RequestCode = ArtNetDataRequestCode.UrlSupport });
        var b = req.ToArray();
        Assert.Equal(40, b.Length);
        Assert.Equal(0x41, b[12]); Assert.Equal(0x42, b[13]);
        Assert.Equal(0x00, b[16]); Assert.Equal(0x03, b[17]);

        var reply = RoundTrip(new ArtDataReplyPacket { RequestCode = ArtNetDataRequestCode.UrlProduct, PayloadText = "https://example.com" });
        var rb = reply.ToArray();
        Assert.Equal(20 + 20, rb.Length);
        Assert.Equal(20, rb[19]);
        Assert.Equal("https://example.com", reply.PayloadText);
    }

    [Fact]
    public void DiagData_RoundTrip()
    {
        var p = RoundTrip(new ArtDiagDataPacket { Priority = ArtNetDiagnosticPriority.Critical, LogicalPort = 2, Text = "Overheat" });
        var b = p.ToArray();
        Assert.Equal(0xE0, b[13]);
        Assert.Equal(2, b[14]);
        Assert.Equal(9, b[17]);
        Assert.Equal("Overheat", p.Text);
    }

    [Fact]
    public void TimeCode_RoundTrip_Offsets_AndIncrement()
    {
        var p = RoundTrip(new ArtTimeCodePacket { StreamId = 1, Hours = 1, Minutes = 2, Seconds = 3, Frames = 4, Type = ArtNetTimeCodeType.Ebu });
        var b = p.ToArray();
        Assert.Equal(19, b.Length);
        Assert.Equal(new byte[] { 1, 4, 3, 2, 1, 1 }, b[13..19]);
        Assert.Equal("01:02:03:04", p.TimeText);

        var t = new ArtTimeCodePacket { Minutes = 0, Seconds = 59, Frames = 29, Type = ArtNetTimeCodeType.DropFrame };
        t.Increment();
        Assert.Equal("00:01:00;02", t.TimeText);
        var e = new ArtTimeCodePacket { Hours = 23, Minutes = 59, Seconds = 59, Frames = 24, Type = ArtNetTimeCodeType.Ebu };
        e.Increment();
        Assert.Equal("00:00:00:00", e.TimeText);
    }

    [Fact]
    public void Command_RoundTrip_AndParse()
    {
        var p = RoundTrip(ArtCommandPacket.SwoutText("Playback"));
        var b = p.ToArray();
        Assert.Equal(0xFF, b[12]); Assert.Equal(0xFF, b[13]);
        Assert.Equal("SwoutText=Playback&", p.Text);
        var c = Assert.Single(p.ParseCommands());
        Assert.Equal("SwoutText", c.Key);
        Assert.Equal("Playback", c.Value);
        Assert.Equal(2, new ArtCommandPacket { Text = "A=1&b=two&" }.ParseCommands().Count);
    }

    [Fact]
    public void Trigger_RoundTrip()
    {
        var t = ArtTriggerPacket.Macro(7);
        t.SetData([1, 2, 3]);
        var p = RoundTrip(t);
        var b = p.ToArray();
        Assert.Equal(530, b.Length);
        Assert.Equal(0xFF, b[14]); Assert.Equal(0xFF, b[15]);
        Assert.Equal(1, b[16]); Assert.Equal(7, b[17]);
        Assert.Equal(new byte[] { 1, 2, 3 }, b[18..21]);
        Assert.Equal("Run macro 7", p.Meaning);
        Assert.Equal("Key press 'G'", ArtTriggerPacket.Ascii('G').Meaning);
    }

    [Fact]
    public void Dmx_RoundTrip_Offsets()
    {
        var data = new byte[512];
        data[0] = 255; data[511] = 7;
        var p = RoundTrip(new ArtDmxPacket { PortAddress = new PortAddress(0x12, 3, 4), Sequence = 9, Physical = 1, Data = data });
        var b = p.ToArray();
        Assert.Equal(530, b.Length);
        Assert.Equal(9, b[12]);
        Assert.Equal(1, b[13]);
        Assert.Equal(0x34, b[14]);
        Assert.Equal(0x12, b[15]);
        Assert.Equal(0x02, b[16]); Assert.Equal(0x00, b[17]);
        Assert.Equal(255, b[18]);
        Assert.Equal(7, b[529]);
        Assert.Equal(255, p[1]);
        Assert.Equal(7, p[512]);
    }

    [Fact]
    public void Dmx_LengthIsEvenAndAtLeastTwo()
    {
        Assert.Equal(2, new ArtDmxPacket { Data = [] }.Data.Length);
        Assert.Equal(4, new ArtDmxPacket { Data = [1, 2, 3] }.Data.Length);
        Assert.Equal(512, new ArtDmxPacket { Data = new byte[600] }.Data.Length);
    }

    [Fact]
    public void Dmx_TruncatedPacket_StillParses()
    {
        var b = new ArtDmxPacket { Data = new byte[512] }.ToArray();
        Assert.True(ArtNetPacketParser.TryParse(b.AsSpan(0, 100), out var p));
        Assert.Equal(82, Assert.IsType<ArtDmxPacket>(p).Data.Length);
    }

    [Fact]
    public void Sync_RoundTrip()
    {
        var p = RoundTrip(new ArtSyncPacket());
        Assert.Equal(14, p.Size);
    }

    [Fact]
    public void Nzs_RoundTrip()
    {
        var p = RoundTrip(new ArtNzsPacket { StartCode = 0x17, PortAddress = new PortAddress(300), Sequence = 2, Data = [1, 2, 3] });
        var b = p.ToArray();
        Assert.Equal(0x17, b[13]);
        Assert.Equal(21, b.Length);
        Assert.Equal(3, b[17]);
        Assert.IsNotType<ArtVlcPacket>(p);
    }

    [Fact]
    public void Vlc_RoundTrip_MagicAndChecksum()
    {
        var v = new ArtVlcPacket
        {
            PortAddress = new PortAddress(5), Flags = ArtVlcFlags.Beacon, Transaction = 0, SlotAddress = 12, Depth = 50,
            Frequency = 1000, PayloadLanguage = ArtVlcPayloadLanguage.BeaconUrl, BeaconRepeat = 10, PayloadText = "http://x.io",
        };
        var p = RoundTrip(v);
        var b = p.ToArray();
        Assert.Equal(0x91, b[13]);
        Assert.Equal(0x41, b[18]); Assert.Equal(0x4C, b[19]); Assert.Equal(0x45, b[20]);
        Assert.Equal(0x20, b[21]);
        Assert.Equal(22 + 11, (b[16] << 8) | b[17]);
        Assert.Equal(11, (b[26] << 8) | b[27]);
        Assert.True(p.ChecksumValid);
        Assert.Equal("http://x.io", p.PayloadText);
        Assert.Equal(12, p.SlotAddress);
    }

    [Fact]
    public void FirmwareMaster_And_Reply_RoundTrip()
    {
        var m = new ArtFirmwareMasterPacket { Type = ArtNetFirmwareMasterType.FirmCont, BlockId = 3, FirmwareLength = 0x00008212 };
        m.SetData([0xAA, 0xBB]);
        var p = RoundTrip(m);
        var b = p.ToArray();
        Assert.Equal(1064, b.Length);
        Assert.Equal(1, b[14]);
        Assert.Equal(3, b[15]);
        Assert.Equal(new byte[] { 0, 0, 0x82, 0x12 }, b[16..20]);
        Assert.Equal(0xAA, b[40]);

        var r = RoundTrip(new ArtFirmwareReplyPacket { Type = ArtNetFirmwareReplyType.AllGood });
        Assert.Equal(36, r.Size);
        Assert.Equal(1, r.ToArray()[14]);
    }

    [Fact]
    public void TodRequest_RoundTrip()
    {
        var p = RoundTrip(ArtTodRequestPacket.For(new PortAddress(2, 1, 0), new PortAddress(2, 1, 5)));
        var b = p.ToArray();
        Assert.Equal(56, b.Length);
        Assert.Equal(2, b[21]);
        Assert.Equal(2, b[23]);
        Assert.Equal(0x10, b[24]); Assert.Equal(0x15, b[25]);
        Assert.Equal(new[] { new PortAddress(2, 1, 0), new PortAddress(2, 1, 5) }, p.PortAddresses);
        Assert.Throws<ArgumentException>(() => ArtTodRequestPacket.For(new PortAddress(1), new PortAddress(300)));
    }

    [Fact]
    public void TodData_RoundTrip_AndSplit()
    {
        var uids = Enumerable.Range(1, 450).Select(i => new RdmUid(0x4142, (uint)i)).ToArray();
        var packets = ArtTodDataPacket.Split(new PortAddress(1, 0, 2), uids);
        Assert.Equal(3, packets.Count);
        Assert.Equal(new[] { 200, 200, 50 }, packets.Select(p => p.Uids.Count));
        var p = RoundTrip(packets[1]);
        var b = p.ToArray();
        Assert.Equal(28 + 1200, b.Length);
        Assert.Equal(1, b[21]);
        Assert.Equal(0x02, b[23]);
        Assert.Equal(450, (b[24] << 8) | b[25]);
        Assert.Equal(1, b[26]);
        Assert.Equal(200, b[27]);
        Assert.Equal(0x41, b[28]); Assert.Equal(0x42, b[29]);
        Assert.Equal(uids[200], p.Uids[0]);
        var merged = ArtNet.Networking.ArtNetNode.MergeTod(packets);
        Assert.Equal(450, merged[new PortAddress(1, 0, 2)].Count);
    }

    [Fact]
    public void TodControl_RoundTrip()
    {
        var p = RoundTrip(new ArtTodControlPacket { PortAddress = new PortAddress(3, 2, 1), Command = ArtNetTodControlCommand.Flush });
        var b = p.ToArray();
        Assert.Equal(24, b.Length);
        Assert.Equal(3, b[21]); Assert.Equal(1, b[22]); Assert.Equal(0x21, b[23]);
    }

    [Fact]
    public void Rdm_RoundTrip_WithMessage()
    {
        var msg = RdmMessage.Build(new RdmUid(0x4142, 1), new RdmUid(0x7FF0, 2), 5, 1, 0, RdmCommandClass.Get, 0x0082);
        var p = RoundTrip(new ArtRdmPacket { PortAddress = new PortAddress(1), RdmPacket = msg, FifoAvailable = 3, FifoMax = 8 });
        var b = p.ToArray();
        Assert.Equal(3, b[19]); Assert.Equal(8, b[20]);
        Assert.Equal(1, b[23]);
        Assert.Equal(0x01, b[24]);
        var m = Assert.IsType<RdmMessage>(p.Message);
        Assert.True(m.ChecksumValid);
        Assert.Equal(0x0082, m.ParameterId);
        Assert.Equal(new RdmUid(0x4142, 1), m.Destination);
        Assert.Equal(24, msg[1]);
        Assert.Equal("DEVICE_LABEL", RdmText.ParameterName(0x0082));
    }

    [Fact]
    public void RdmSub_RoundTrip()
    {
        var s = new ArtRdmSubPacket { Uid = new RdmUid(0x0102, 0x03040506), CommandClass = RdmCommandClass.Set, ParameterId = 0x00F0, SubDevice = 1, SubCount = 2 };
        s.Values.AddRange([10, 20]);
        var p = RoundTrip(s);
        var b = p.ToArray();
        Assert.Equal(36, b.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, b[14..20]);
        Assert.Equal(0x30, b[21]);
        Assert.Equal(0xF0, b[23]);
        Assert.Equal(1, b[25]);
        Assert.Equal(2, b[27]);
        Assert.Equal(20, b[35]);
        Assert.Equal(2, p.ExpectedValueCount);
    }

    [Fact]
    public void UnknownOpCode_KeepsBytes()
    {
        var b = new byte[] { (byte)'A', (byte)'r', (byte)'t', (byte)'-', (byte)'N', (byte)'e', (byte)'t', 0, 0x00, 0x90, 0, 14, 1, 2, 3 };
        Assert.True(ArtNetPacketParser.TryParse(b, out var p));
        var u = Assert.IsType<ArtUnknownPacket>(p);
        Assert.Equal(ArtNetOpCode.Media, u.OpCode);
        Assert.Equal(b, u.ToArray());
        Assert.Equal("ArtMedia", u.Title);
        Assert.False(ArtNetPacketParser.TryParse(b, out _, includeUnknown: false));
    }

    [Fact]
    public void EveryDecodedOpCode_HasNameAndDescription()
    {
        foreach (var op in Enum.GetValues<ArtNetOpCode>())
        {
            Assert.StartsWith("Art", op.ToPacketName());
            Assert.DoesNotContain("not defined", op.ToDescription());
        }
    }
}
