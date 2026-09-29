using System.Net;

namespace ArtNet.Tests;

/// <summary>Malformed input, spec edge cases and argument checks.</summary>
public class RobustnessTests
{
    /// <summary>A datagram with a valid Art-Net header for <paramref name="op"/>, <paramref name="length"/> bytes long.</summary>
    private static byte[] Datagram(ushort op, int length, Func<int, byte> body)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = body(i);
        "Art-Net\0"u8.CopyTo(b);
        b[8] = (byte)op;
        b[9] = (byte)(op >> 8);
        if (length > 11) { b[10] = 0; b[11] = 14; }
        return b;
    }

    private static void DecodeEverything(byte[] datagram)
    {
        if (!ArtNetPacketParser.TryParse(datagram, out var packet)) return;
        Assert.NotNull(packet.Describe());
        Assert.NotNull(packet.Summary);
        Assert.NotNull(packet.ToString());
        Assert.NotNull(ArtNetFormatter.Format(packet));
        Assert.NotNull(packet.ToArray());
    }

    public static TheoryData<ushort> OpCodes()
    {
        var data = new TheoryData<ushort>();
        foreach (var op in Enum.GetValues<ArtNetOpCode>()) data.Add((ushort)op);
        data.Add(0x1234); // unknown
        return data;
    }

    [Theory]
    [MemberData(nameof(OpCodes))]
    public void EveryOpCode_AnyLength_AnyContent_DecodesWithoutThrowing(ushort op)
    {
        var random = new Random(op);
        for (int length = 10; length <= 1100; length++)
        {
            DecodeEverything(Datagram(op, length, _ => 0x00));
            DecodeEverything(Datagram(op, length, _ => 0xFF));
            DecodeEverything(Datagram(op, length, _ => (byte)random.Next(256)));
        }
    }

    [Fact]
    public void PollReply_PortTypesDescribeMorePortsThanNumPorts_AllPortsCount()
    {
        var reply = new ArtPollReplyPacket { IpAddress = IPAddress.Parse("2.0.0.10"), NetSwitch = 0, SubSwitch = 0 };
        for (int i = 0; i < 4; i++) reply.SetPort(i, ArtNetPortDirection.Output, ArtNetPortProtocol.Dmx512, 0, (byte)(i + 1));
        reply.NumPorts = 1; // under-reported, as some nodes do

        var parsed = Assert.IsType<ArtPollReplyPacket>(ArtNetPacketParser.Parse(reply.ToArray()));

        Assert.Equal(4, parsed.PortCount);
        Assert.Equal([new PortAddress(1), new PortAddress(2), new PortAddress(3), new PortAddress(4)], parsed.SubscribedAddresses);
        Assert.True(parsed.IsSubscribedTo(new PortAddress(4)));
    }

    [Fact]
    public void PollReply_NumPortsWithoutPortTypes_StillCounts()
    {
        var reply = new ArtPollReplyPacket { NumPorts = 2 };
        Assert.Equal(2, reply.PortCount);
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0xCC)]
    public void Nzs_ForbiddenStartCode_FailsValidation_ButStillDecodesAndReserialises(byte startCode)
    {
        var packet = new ArtNzsPacket { StartCode = startCode, Data = [1, 2] };
        Assert.Throws<InvalidOperationException>(packet.Validate);

        // Received packets keep their bytes, so logs and replay still work.
        var bytes = packet.ToArray();
        var parsed = Assert.IsType<ArtNzsPacket>(ArtNetPacketParser.Parse(bytes));
        Assert.Equal(startCode, parsed.StartCode);
        Assert.Equal(bytes, parsed.ToArray());
    }

    [Fact]
    public void Nzs_EmptyData_FailsValidation()
    {
        Assert.Throws<InvalidOperationException>(new ArtNzsPacket { Data = [] }.Validate);
        new ArtNzsPacket { Data = [1] }.Validate();
    }

    [Fact]
    public void TodDataSplit_TooManyUids_Throws()
    {
        var uids = Enumerable.Range(0, ushort.MaxValue + 1).Select(i => new RdmUid(0x7FF0, (uint)i)).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => ArtTodDataPacket.Split(new PortAddress(1), uids));
    }

    [Fact]
    public void TodDataSplit_BlockIndexesAreSequential()
    {
        var uids = Enumerable.Range(0, 450).Select(i => new RdmUid(0x7FF0, (uint)i)).ToArray();
        var blocks = ArtTodDataPacket.Split(new PortAddress(1), uids);
        Assert.Equal([0, 1, 2], blocks.Select(b => (int)b.BlockCount));
        Assert.All(blocks, b => Assert.Equal(450, b.UidTotal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void ArtAddress_PortOutOfRange_ThrowsArgumentOutOfRange(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArtAddressPacket().SetOutputAddress(new PortAddress(1), port));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArtAddressPacket().SetInputAddress(new PortAddress(1), port));
    }

    [Fact]
    public void FirmwareFile_MoreThan256OemCodes_Throws()
    {
        var file = new ArtNetFirmwareFile { Data = new byte[4] };
        for (int i = 1; i <= ArtNetFirmwareFile.OemCount + 1; i++) file.OemCodes.Add((ushort)i);
        Assert.Throws<InvalidOperationException>(() => file.ToArray());
    }

    [Fact]
    public void Rdm_HeaderOnly_HasNoMessage()
    {
        var packet = new ArtRdmPacket { PortAddress = new PortAddress(1), RdmPacket = [] };
        var parsed = Assert.IsType<ArtRdmPacket>(ArtNetPacketParser.Parse(packet.ToArray()));
        Assert.Null(parsed.Message);
        Assert.NotEmpty(parsed.Describe());
    }

    [Theory]
    [InlineData((byte)4)]
    [InlineData((byte)255)]
    public void TimeCode_UndefinedType_DescribesAndIncrements(byte type)
    {
        var packet = new ArtTimeCodePacket { Type = (ArtNetTimeCodeType)type, Frames = 0 };
        Assert.NotEmpty(packet.Describe());
        Assert.False(string.IsNullOrWhiteSpace(packet.Summary));
        packet.Increment();
        Assert.Equal(1, packet.Frames);
    }
}
