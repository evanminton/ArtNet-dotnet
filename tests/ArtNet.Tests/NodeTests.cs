using System.Net;
using ArtNet.Networking;

namespace ArtNet.Tests;

public class NodeTests
{
    private static readonly IPEndPoint A = new(IPAddress.Parse("2.0.0.10"), ArtNetConstants.Port);
    private static readonly IPEndPoint B = new(IPAddress.Parse("2.0.0.11"), ArtNetConstants.Port);

    private static byte[] Dmx(PortAddress u, byte seq, params byte[] levels) =>
        new ArtDmxPacket { PortAddress = u, Sequence = seq, Data = levels }.ToArray();

    [Fact]
    public void PollReply_AddsAndUpdatesNodes_PerBind()
    {
        var node = new ArtNetNode();
        var discovered = new List<ArtNetRemoteNode>();
        var updated = 0;
        node.NodeDiscovered += (_, e) => discovered.Add(e.Node);
        node.NodeUpdated += (_, _) => updated++;

        var r1 = new ArtPollReplyPacket { ShortName = "GW", BindIndex = 1, NetSwitch = 0, SubSwitch = 0 };
        r1.SetPort(0, ArtNetPortDirection.Output, ArtNetPortProtocol.Dmx512, 0, 1);
        var r2 = new ArtPollReplyPacket { ShortName = "GW", BindIndex = 2 };
        r2.SetPort(0, ArtNetPortDirection.Output, ArtNetPortProtocol.Dmx512, 0, 2);

        node.InjectDatagram(r1.ToArray(), A);
        node.InjectDatagram(r2.ToArray(), A);
        node.InjectDatagram(r1.ToArray(), A); // unchanged → no update
        Assert.Equal(2, discovered.Count);
        Assert.Equal(0, updated);

        r1.ShortName = "GW renamed";
        node.InjectDatagram(r1.ToArray(), A);
        Assert.Equal(1, updated);
        Assert.Equal(2, node.Nodes.Count);
        Assert.Equal("GW renamed", node.Nodes[0].ShortName);
        Assert.Single(node.SubscribersOf(new PortAddress(2)));
        Assert.Equal(2, node.Nodes[1].BindIndex);
    }

    [Fact]
    public void Dmx_SingleSource_RaisesUniverseChanged()
    {
        var node = new ArtNetNode();
        ArtNetUniverseEventArgs? last = null;
        node.UniverseChanged += (_, e) => last = e;
        node.InjectDatagram(Dmx(new PortAddress(1), 1, 10, 20, 30, 40), A);
        Assert.NotNull(last);
        Assert.Equal(new PortAddress(1), last!.Address);
        Assert.Equal(512, last.Data.Length);
        Assert.Equal(30, last.Data[2]);
        Assert.False(last.Merging);
        Assert.Equal(30, node.GetUniverse(new PortAddress(1))![3]);
    }

    [Fact]
    public void Dmx_OutOfOrderSequence_IsDropped()
    {
        var node = new ArtNetNode();
        int count = 0;
        node.UniverseChanged += (_, _) => count++;
        node.InjectDatagram(Dmx(new PortAddress(1), 10, 1, 1), A);
        node.InjectDatagram(Dmx(new PortAddress(1), 9, 2, 2), A);   // older
        node.InjectDatagram(Dmx(new PortAddress(1), 11, 3, 3), A);
        node.InjectDatagram(Dmx(new PortAddress(1), 0, 4, 4), A);   // sequence disabled
        Assert.Equal(3, count);
        Assert.Equal(1, node.GetUniverse(new PortAddress(1))!.DroppedCount);
        node.InjectDatagram(Dmx(new PortAddress(1), 255, 5, 5), A);
        node.InjectDatagram(Dmx(new PortAddress(1), 1, 6, 6), A);   // wrap 255 → 1 is newer
        Assert.Equal(6, node.GetUniverse(new PortAddress(1))![1]);
    }

    [Fact]
    public void Dmx_TwoSources_HtpMerge_ThirdIgnored()
    {
        var node = new ArtNetNode();
        ArtNetUniverseEventArgs? last = null;
        node.UniverseChanged += (_, e) => last = e;
        var u = new PortAddress(5);
        node.InjectDatagram(Dmx(u, 1, 100, 0, 50, 0), A);
        node.InjectDatagram(Dmx(u, 1, 0, 200, 60, 0), B);
        Assert.True(last!.Merging);
        Assert.Equal(new byte[] { 100, 200, 60, 0 }, last.Data[..4]);
        node.InjectDatagram(Dmx(u, 1, 255, 255, 255, 255), new IPEndPoint(IPAddress.Parse("2.0.0.12"), 6454));
        Assert.Equal(100, node.GetUniverse(u)![1]);
        Assert.Equal(2, node.GetUniverse(u)!.Sources.Count);
    }

    [Fact]
    public void Dmx_LtpMerge_LatestWins()
    {
        var node = new ArtNetNode(new ArtNetNodeSettings { DefaultMergeMode = ArtNetMergeMode.Ltp });
        var u = new PortAddress(5);
        node.InjectDatagram(Dmx(u, 1, 100, 100), A);
        node.InjectDatagram(Dmx(u, 1, 10, 10), B);
        Assert.Equal(10, node.GetUniverse(u)![1]);
        node.InjectDatagram(Dmx(u, 2, 50, 50), A);
        Assert.Equal(50, node.GetUniverse(u)![1]);
    }

    [Fact]
    public void Dmx_SamePhysicalFromSameIp_IsOneSource_DifferentPhysicalMerges()
    {
        var node = new ArtNetNode();
        var u = new PortAddress(7);
        node.InjectDatagram(new ArtDmxPacket { PortAddress = u, Physical = 0, Data = [10, 0] }.ToArray(), A);
        node.InjectDatagram(new ArtDmxPacket { PortAddress = u, Physical = 1, Data = [0, 20] }.ToArray(), A);
        Assert.True(node.GetUniverse(u)!.IsMerging);
        Assert.Equal(10, node.GetUniverse(u)![1]);
        Assert.Equal(20, node.GetUniverse(u)![2]);
    }

    [Fact]
    public void CancelMerge_NextSourceTakesOver()
    {
        var node = new ArtNetNode(new ArtNetNodeSettings { Ports = [ArtNetPortConfig.Output(new PortAddress(5))] });
        var u = new PortAddress(5);
        node.InjectDatagram(Dmx(u, 0, 100), A);
        node.InjectDatagram(Dmx(u, 0, 50), B);
        node.InjectDatagram(ArtAddressPacket.ForCommand(ArtNetAddressCommand.CancelMerge).ToArray(), B);
        node.InjectDatagram(Dmx(u, 0, 20), B);   // B takes over
        node.InjectDatagram(Dmx(u, 0, 200), A);  // A ignored now
        Assert.Equal(20, node.GetUniverse(u)![1]);
        Assert.False(node.GetUniverse(u)!.IsMerging);
    }

    [Fact]
    public void Sync_BuffersUntilNextSync()
    {
        var node = new ArtNetNode();
        var events = new List<ArtNetUniverseEventArgs>();
        node.UniverseChanged += (_, e) => events.Add(e);
        var u = new PortAddress(1);
        node.InjectDatagram(Dmx(u, 0, 1), A);
        Assert.Single(events);
        node.InjectDatagram(new ArtSyncPacket().ToArray(), A);   // enter synchronous mode
        Assert.True(node.IsSynchronous);
        node.InjectDatagram(Dmx(u, 0, 2), A);
        Assert.Single(events);                                   // held
        node.InjectDatagram(new ArtSyncPacket().ToArray(), B);   // from another controller: ignored
        Assert.Single(events);
        node.InjectDatagram(new ArtSyncPacket().ToArray(), A);
        Assert.Equal(2, events.Count);
        Assert.True(events[1].Synchronous);
        Assert.Equal(2, events[1].Data[0]);
    }

    [Fact]
    public void Address_ProgramsOwnPortAndNames()
    {
        var settings = new ArtNetNodeSettings { Ports = [ArtNetPortConfig.Output(new PortAddress(1))] };
        var node = new ArtNetNode(settings);
        bool changed = false;
        node.ConfigurationChanged += (_, _) => changed = true;
        var a = new ArtAddressPacket { ShortName = "Renamed", LongName = "Renamed long", Command = ArtNetAddressCommand.LedLocate }
            .SetOutputAddress(new PortAddress(2, 3, 4));
        node.InjectDatagram(a.ToArray(), A);
        Assert.True(changed);
        Assert.Equal("Renamed", settings.ShortName);
        Assert.Equal("Renamed long", settings.LongName);
        Assert.Equal(new PortAddress(2, 3, 4), settings.Ports[0].Address);
        Assert.Equal(ArtNetIndicatorState.Locate, node.IndicatorState);

        var reply = Assert.Single(node.CreatePollReplies());
        Assert.Equal(ArtNetIndicatorState.Locate, reply.IndicatorState);
        Assert.Equal(ArtNetPortAddressAuthority.Network, reply.PortAddressAuthority);
        Assert.True(reply.OutputsAddress(new PortAddress(2, 3, 4)));
        Assert.StartsWith("#0007", reply.NodeReport); // LoNameOk was the last report
    }

    [Fact]
    public void PollReplies_OnePerPort_WithBindIndex()
    {
        var node = new ArtNetNode(new ArtNetNodeSettings
        {
            ShortName = "Node",
            Ports = [ArtNetPortConfig.Output(new PortAddress(1)), ArtNetPortConfig.Input(new PortAddress(300), "In A")],
        });
        var replies = node.CreatePollReplies();
        Assert.Equal(2, replies.Count);
        Assert.Equal(1, replies[0].BindIndex);
        Assert.Equal(2, replies[1].BindIndex);
        Assert.True(replies[0].OutputsAddress(new PortAddress(1)));
        Assert.Equal(new PortAddress(300), replies[1].GetInputAddress(0));
        Assert.Equal("In A", replies[1].ShortName);
        Assert.Single(new ArtNetNode().CreatePollReplies());
        // counter increments per reply
        Assert.True(ArtPollReplyPacket.TryParseNodeReport(replies[1].NodeReport, out _, out int c2, out _));
        Assert.True(ArtPollReplyPacket.TryParseNodeReport(replies[0].NodeReport, out _, out int c1, out _));
        Assert.Equal(c1 + 1, c2);
    }

    [Fact]
    public void Input_DisablesOwnInputPort()
    {
        var settings = new ArtNetNodeSettings { Ports = [ArtNetPortConfig.Input(new PortAddress(1))] };
        var node = new ArtNetNode(settings);
        var input = new ArtInputPacket { BindIndex = 1 };
        input.SetDisabled(0, true);
        node.InjectDatagram(input.ToArray(), A);
        Assert.True(settings.Ports[0].InputDisabled);
        Assert.True(((ArtNetGoodInput)node.CreatePollReplies()[0].GoodInput[0]).HasFlag(ArtNetGoodInput.Disabled));
    }

    [Fact]
    public void TypedEvents_AreRaised()
    {
        var node = new ArtNetNode();
        var seen = new List<string>();
        node.TimeCodeReceived += (_, e) => seen.Add(e.Packet.TimeText);
        node.TriggerReceived += (_, e) => seen.Add(e.Packet.Meaning);
        node.CommandReceived += (_, e) => seen.Add(e.Packet.Text);
        node.VlcReceived += (_, _) => seen.Add("vlc");
        node.NzsReceived += (_, _) => seen.Add("nzs");
        node.UnknownReceived += (_, e) => seen.Add(e.Packet.Title);
        int all = 0;
        node.PacketReceived += (_, _) => all++;

        node.InjectDatagram(new ArtTimeCodePacket { Hours = 1 }.ToArray(), A);
        node.InjectDatagram(ArtTriggerPacket.Show(2).ToArray(), A);
        node.InjectDatagram(ArtCommandPacket.SwinText("Rec").ToArray(), A);
        node.InjectDatagram(new ArtVlcPacket { PayloadText = "x" }.ToArray(), A);
        node.InjectDatagram(new ArtNzsPacket { Data = [1] }.ToArray(), A);
        node.InjectDatagram(new ArtUnknownPacket(ArtNetOpCode.VideoData) { Body = [0, 14] }.ToArray(), A);
        Assert.Equal(new[] { "01:00:00:00", "Run show 2", "SwinText=Rec&", "vlc", "nzs", "ArtVideoData" }, seen);
        Assert.Equal(6, all);
    }

    [Fact]
    public void Output_GetOutputDefaultsToZero()
    {
        var node = new ArtNetNode();
        Assert.Equal(512, node.GetOutput(new PortAddress(1)).Length);
        Assert.Empty(node.OutputUniverses);
    }
}
