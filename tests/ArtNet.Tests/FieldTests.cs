using System.Net;
using ArtNet.Networking;

namespace ArtNet.Tests;

public class FieldTests
{
    [Fact]
    public void PortAddress_Bits()
    {
        var a = new PortAddress(0x12, 3, 4);
        Assert.Equal(0x1234, a.Value);
        Assert.Equal(0x12, a.Net);
        Assert.Equal(3, a.SubNet);
        Assert.Equal(4, a.Universe);
        Assert.Equal(0x34, a.SubUni);
        Assert.Equal(a, PortAddress.FromBytes(0x12, 0x34));
        Assert.Equal(a, PortAddress.FromSwitches(0x92, 0xF3, 0xF4)); // upper bits ignored
        Assert.Equal("4660 (18:3:4)", a.ToString());
        Assert.Equal("18:3:4", a.ToString("N", null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortAddress(32768));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortAddress(128, 0, 0));
        Assert.True(new PortAddress(0).IsDeprecatedZero);
        Assert.Equal(4, new PortAddress(4096).Kiloverse);
    }

    [Theory]
    [InlineData("21", 21)]
    [InlineData("0x15", 21)]
    [InlineData("0:1:5", 21)]
    [InlineData("0.1.5", 21)]
    [InlineData("1/5", 21)]
    [InlineData("127:15:15", 32767)]
    public void PortAddress_Parse(string text, int expected)
    {
        Assert.True(PortAddress.TryParse(text, out var a));
        Assert.Equal(expected, a.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("32768")]
    [InlineData("128:0:0")]
    [InlineData("0:16:0")]
    [InlineData("x")]
    public void PortAddress_ParseRejects(string text) => Assert.False(PortAddress.TryParse(text, out _));

    [Fact]
    public void RdmUid_Parse_Format()
    {
        var u = RdmUid.Parse("4142:00000102");
        Assert.Equal(0x4142, u.ManufacturerId);
        Assert.Equal(0x102u, u.DeviceId);
        Assert.Equal("4142:00000102", u.ToString());
        Assert.Equal(new byte[] { 0x41, 0x42, 0, 0, 1, 2 }, u.ToArray());
        Assert.Equal(u, RdmUid.Read(u.ToArray()));
        Assert.True(RdmUid.Broadcast.IsBroadcast);
        Assert.False(RdmUid.TryParse("12", out _));
    }

    [Fact]
    public void DefaultIp_MatchesSpecExample()
    {
        // MAC 12:45:78:98:34:76, OEM 0x0010, network switch off → 2.168.52.118
        byte[] mac = [0x12, 0x45, 0x78, 0x98, 0x34, 0x76];
        Assert.Equal(IPAddress.Parse("2.168.52.118"), ArtNetNetworkInterface.DefaultArtNetAddress(mac, 0x0010));
        Assert.Equal(IPAddress.Parse("10.168.52.118"), ArtNetNetworkInterface.DefaultArtNetAddress(mac, 0x0010, networkSwitch: true));
    }

    [Fact]
    public void Broadcast_And_Mask_Helpers()
    {
        Assert.Equal(IPAddress.Parse("192.168.1.255"), ArtNetNetworkInterface.BroadcastOf(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0")));
        Assert.Equal(IPAddress.Parse("2.255.255.255"), ArtNetNetworkInterface.BroadcastOf(IPAddress.Parse("2.1.2.3"), IPAddress.Parse("255.0.0.0")));
        Assert.Equal(IPAddress.Parse("255.255.240.0"), ArtNetNetworkInterface.MaskFromPrefix(20));
        Assert.True(ArtNetNetworkInterface.SameNetwork(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.200.0.1"), IPAddress.Parse("255.0.0.0")));
    }

    [Fact]
    public void Text_NamesDescriptionsAndFlags()
    {
        Assert.Equal("LED Locate", ArtNetAddressCommand.LedLocate.ToDisplayName());
        Assert.Contains("flashing", ArtNetAddressCommand.LedLocate.ToDescription());
        Assert.Equal("Reply On Change, Send Diagnostics", (ArtPollFlags.ReplyOnChange | ArtPollFlags.Diagnostics).ToDisplayName());
        Assert.Equal("None", ArtPollFlags.None.ToDisplayName());
        Assert.Equal("Merging, Outputting", (ArtNetGoodOutputA.DataTransmitted | ArtNetGoodOutputA.Merging).ToDisplayName());
        Assert.Equal("ArtDmx", ArtNetOpCode.Dmx.ToPacketName());
        Assert.Equal("Controller (1)", ArtNetStyle.Controller.ToDisplayString());
        Assert.StartsWith("Unknown", ((ArtNetStyle)99).ToDisplayName());
    }

    [Theory]
    [InlineData("led locate", ArtNetAddressCommand.LedLocate)]
    [InlineData("LedLocate", ArtNetAddressCommand.LedLocate)]
    [InlineData("4", ArtNetAddressCommand.LedLocate)]
    [InlineData("0x04", ArtNetAddressCommand.LedLocate)]
    [InlineData("cancel-merge", ArtNetAddressCommand.CancelMerge)]
    [InlineData("Background Queue Policy 2", ArtNetAddressCommand.Bqp2)]
    public void Text_Parse(string text, ArtNetAddressCommand expected) => Assert.Equal(expected, ArtNetText.Parse<ArtNetAddressCommand>(text));

    [Fact]
    public void Text_ParseFailureListsOptions()
    {
        var ex = Assert.Throws<FormatException>(() => ArtNetText.Parse<ArtNetStyle>("banana"));
        Assert.Contains("Media Server", ex.Message);
    }

    [Fact]
    public void Catalog_CoversEveryEnumValue()
    {
        Assert.NotEmpty(ArtNetOptionCatalog.All);
        Assert.Equal(Enum.GetValues<ArtNetAddressCommand>().Length, ArtNetOptionCatalog.AddressCommands.Count);
        Assert.Equal(Enum.GetValues<ArtNetOpCode>().Length, ArtNetOptionCatalog.OpCodes.Count);
        Assert.Equal(Enum.GetValues<ArtNetNodeReportCode>().Length, ArtNetOptionCatalog.NodeReportCodes.Count);
        foreach (var g in ArtNetOptionCatalog.All)
            foreach (var o in g.Options)
            {
                Assert.False(string.IsNullOrWhiteSpace(o.Name), g.Title);
                Assert.False(string.IsNullOrWhiteSpace(o.Description), $"{g.Title}/{o.Name}");
            }
        var text = ArtNetOptionCatalog.Describe();
        Assert.Contains("ArtPollReply", text);
        Assert.Contains("DMX_START_ADDRESS", text);
    }

    [Fact]
    public void EstaFormatting()
    {
        Assert.Equal("\"AL\"", ArtNetText.FormatEsta(0x414C));
        Assert.Equal("0xFFFF (Art-Net defined)", ArtNetText.FormatEsta(0xFFFF));
        Assert.Equal("0x0102", ArtNetText.FormatEsta(0x0102));
    }

    [Fact]
    public void Formatter_HexDumpAndGrid()
    {
        var dump = ArtNetFormatter.HexDump(new ArtPollPacket().ToArray());
        Assert.StartsWith("0000  41 72 74 2D", dump);
        Assert.Contains("Art-Net", dump);
        var grid = ArtNetFormatter.DmxGrid(new byte[32], percent: true);
        Assert.Equal(2, grid.Split('\n').Length);
    }

    [Fact]
    public void FirmwareFile_RoundTrip_Checksum_Packets()
    {
        var f = new ArtNetFirmwareFile { FirmwareVersion = 0x0203, UserName = "Test firmware v2.3", Data = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray() };
        f.OemCodes.Add(0x00FF);
        f.OemCodes.Add(0x1234);
        var bytes = f.ToArray();
        Assert.Equal(1060 + 3000, bytes.Length);
        Assert.Equal(1500u, (uint)((bytes[1056] << 24) | (bytes[1057] << 16) | (bytes[1058] << 8) | bytes[1059]));
        var g = ArtNetFirmwareFile.Parse(bytes);
        Assert.Equal("Test firmware v2.3", g.UserName);
        Assert.Equal(0x0203, g.FirmwareVersion);
        Assert.Equal(new ushort[] { 0x00FF, 0x1234 }, g.OemCodes);
        Assert.True(g.ChecksumValid);
        Assert.True(g.SupportsOem(0x1234));
        Assert.Equal(530u + 1500u, g.TotalWords);

        var packets = g.ToPackets();
        Assert.Equal(4, packets.Count); // 4060 bytes / 1024
        Assert.Equal(ArtNetFirmwareMasterType.FirmFirst, packets[0].Type);
        Assert.Equal(ArtNetFirmwareMasterType.FirmCont, packets[1].Type);
        Assert.Equal(ArtNetFirmwareMasterType.FirmLast, packets[3].Type);
        Assert.All(packets, p => Assert.Equal(2030u, p.FirmwareLength));
        Assert.Equal(ArtNetFirmwareMasterType.UbeaFirst, g.ToPackets(ubea: true)[0].Type);
    }

    [Fact]
    public void FirmwareChecksum_IsOnesComplement()
    {
        Assert.Equal((ushort)0xFFFF, ArtNetFirmwareFile.ComputeChecksum([]));
        Assert.Equal(unchecked((ushort)~0x0102), ArtNetFirmwareFile.ComputeChecksum([0x01, 0x02]));
        // end-around carry: 0xFFFF + 0x0001 = 0x10000 → 0x0001
        Assert.Equal(unchecked((ushort)~0x0001), ArtNetFirmwareFile.ComputeChecksum([0xFF, 0xFF, 0x00, 0x01]));
    }

    [Fact]
    public void RdmMessage_BuildParse_WithData()
    {
        var bytes = RdmMessage.Build(new RdmUid(1, 2), new RdmUid(3, 4), 9, 1, 0, RdmCommandClass.Set, 0x00F0, [0x00, 0x21]);
        Assert.True(RdmMessage.TryParse(bytes, out var m));
        Assert.True(m.ChecksumValid);
        Assert.Equal(RdmCommandClass.Set, m.CommandClass);
        Assert.Equal(new byte[] { 0, 0x21 }, m.ParameterData);
        Assert.Equal("DMX start address 33", RdmText.FormatParameterData(0x00F0, m.ParameterData));
        Assert.Equal(bytes, m.ToArray());
        Assert.True(RdmText.TryParseParameter("dmx start address", out var pid));
        Assert.Equal(0x00F0, pid);
    }
}
