using System.Net;
using System.Net.Sockets;
using System.Reflection;
using ArtNet.Networking;

namespace ArtNet.Tests;

/// <summary>Regression tests for issues found in code review.</summary>
public class RegressionTests
{
    private static readonly IPEndPoint A = new(IPAddress.Parse("2.0.0.10"), ArtNetConstants.Port);
    private static readonly IPEndPoint B = new(IPAddress.Parse("2.0.0.11"), ArtNetConstants.Port);

    private static byte[] Dmx(PortAddress u, byte seq, params byte[] levels) =>
        new ArtDmxPacket { PortAddress = u, Sequence = seq, Data = levels }.ToArray();

    private static T Parse<T>(byte[] bytes) where T : ArtNetPacket
    {
        Assert.True(ArtNetPacketParser.TryParse(bytes, out var p));
        return Assert.IsType<T>(p);
    }

    // ------------------------------------------------------------------ packets

    [Fact]
    public void Vlc_BadChecksum_StaysInvalidAfterReadingData_AndIsCorrectedWhenWritten()
    {
        var bytes = new ArtVlcPacket { PayloadText = "hello" }.ToArray();
        bytes[ArtNzsPacket.HeaderSize + 11] ^= 0xFF; // corrupt the payload checksum (low byte)
        var p = Parse<ArtVlcPacket>(bytes);
        Assert.False(p.ChecksumValid);
        _ = p.Data;
        _ = p.ToArray();
        Assert.False(p.ChecksumValid);

        Assert.True(Parse<ArtVlcPacket>(p.ToArray()).ChecksumValid); // what we send is always valid
        p.PayloadText = "changed";
        Assert.True(p.ChecksumValid);
    }

    [Fact]
    public void Rdm_InconsistentMessageLength_OrOversizedData_IsRejected()
    {
        var ok = RdmMessage.Build(new RdmUid(1, 2), new RdmUid(3, 4), 1, 1, 0, RdmCommandClass.Get, 0x0060);
        Assert.True(RdmMessage.TryParse(ok, out _));

        var badLength = (byte[])ok.Clone();
        badLength[1]++;
        Assert.False(RdmMessage.TryParse(badLength, out _));

        var tooLong = new byte[RdmMessage.HeaderSize + 240 + 2];
        tooLong[0] = RdmMessage.SubStartCode;
        tooLong[1] = unchecked((byte)(RdmMessage.HeaderSize + 1 + 240)); // wraps: cannot be consistent
        tooLong[22] = 240;
        Assert.False(RdmMessage.TryParse(tooLong, out _));
    }

    [Fact]
    public void FirmwareMaster_TruncatedBlock_IsRejected()
    {
        var full = new ArtFirmwareMasterPacket { Type = ArtNetFirmwareMasterType.FirmCont }.ToArray();
        Assert.True(ArtNetPacketParser.TryParse(full, out _));
        Assert.False(ArtNetPacketParser.TryParse(full.AsSpan(0, 100), out var p) && p is ArtFirmwareMasterPacket);
    }

    [Fact]
    public void Dmx_ReceivedZeroLength_IsSentWithValidLength()
    {
        var bytes = new ArtDmxPacket { PortAddress = new PortAddress(1) }.ToArray().AsSpan(0, ArtDmxPacket.HeaderSize).ToArray();
        bytes[16] = 0; bytes[17] = 0;
        var p = Parse<ArtDmxPacket>(bytes);
        var again = p.ToArray();
        Assert.Equal(ArtDmxPacket.HeaderSize + 2, again.Length);
        Assert.Equal(2, again[17]);
    }

    [Theory]
    [InlineData(ArtNetTimeCodeType.Smpte, 1, 1)]
    [InlineData(ArtNetTimeCodeType.Smpte, 29, 29)]
    [InlineData(ArtNetTimeCodeType.Ebu, 24, 24)]
    [InlineData(ArtNetTimeCodeType.Film, 23, 23)]
    public void TimeCode_FromTimeSpan_KeepsFrameNumber(ArtNetTimeCodeType type, int frame, int expected)
    {
        var t = new TimeSpan(0, 1, 2, 3) + TimeSpan.FromSeconds(frame / type.FrameRate());
        Assert.Equal(expected, ArtTimeCodePacket.FromTimeSpan(t, type).Frames);
    }

    [Fact]
    public void TimeCode_DropFrameLabels_AndInvalidInput()
    {
        Assert.Equal(2, ArtTimeCodePacket.FromTimeSpan(new TimeSpan(0, 0, 1, 0), ArtNetTimeCodeType.DropFrame).Frames);
        Assert.Equal(0, ArtTimeCodePacket.FromTimeSpan(new TimeSpan(0, 0, 10, 0), ArtNetTimeCodeType.DropFrame).Frames);
        Assert.Throws<ArgumentOutOfRangeException>(() => ArtTimeCodePacket.FromTimeSpan(TimeSpan.FromSeconds(-1), ArtNetTimeCodeType.Smpte));

        var p = new ArtTimeCodePacket { Frames = 255, Seconds = 5, Type = ArtNetTimeCodeType.Smpte };
        p.Increment();
        Assert.Equal(0, p.Frames);
        Assert.Equal(6, p.Seconds);
    }

    [Fact]
    public void Text_TryParse_RejectsNumbersOutsideTheEnumRange()
    {
        Assert.False(ArtNetText.TryParse<ArtNetDiagnosticPriority>("300", out _));
        Assert.True(ArtNetText.TryParse<ArtNetDiagnosticPriority>("0x80", out var v));
        Assert.Equal(0x80, (byte)v);
    }

    // ------------------------------------------------------------------ universe

    [Fact]
    public void Universe_RestartedController_IsAcceptedAfterABigSequenceJump()
    {
        var node = new ArtNetNode();
        var u = new PortAddress(1);
        node.InjectDatagram(Dmx(u, 128, 1, 1), A);
        node.InjectDatagram(Dmx(u, 1, 2, 2), A);   // controller restarted
        Assert.Equal(2, node.GetUniverse(u)![1]);
        node.InjectDatagram(Dmx(u, 250, 3, 3), A); // 7 behind (wrapping): reordered, dropped
        Assert.Equal(2, node.GetUniverse(u)![1]);
    }

    [Fact]
    public void Universe_CancelMerge_WhenNotMerging_IsIgnored()
    {
        var u = new ArtNetUniverse(new PortAddress(1));
        u.Apply(A.Address, new ArtDmxPacket { Data = [1, 1] }, out _);
        u.CancelMerge();
        u.Apply(A.Address, new ArtDmxPacket { Data = [2, 2] }, out _);
        Assert.NotNull(u.Apply(B.Address, new ArtDmxPacket { Data = [3, 3] }, out bool merging)); // B may still merge
        Assert.True(merging);
    }

    // ------------------------------------------------------------------ node

    [Fact]
    public void Sync_Timeout_NeverReleasesAnOlderFrameAfterANewerOne()
    {
        var node = new ArtNetNode();
        var events = new List<ArtNetUniverseEventArgs>();
        node.UniverseChanged += (_, e) => events.Add(e);
        var u = new PortAddress(1);
        node.InjectDatagram(new ArtSyncPacket().ToArray(), A);
        node.InjectDatagram(Dmx(u, 0, 1), A);
        Assert.Empty(events); // buffered

        // Let the 4 s sync timeout pass.
        typeof(ArtNetNode).GetField("_lastSync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(node, DateTime.UtcNow - ArtNetConstants.SyncTimeout - TimeSpan.FromSeconds(1));
        Assert.False(node.IsSynchronous);

        node.InjectDatagram(Dmx(u, 0, 2), A);
        Assert.Equal([1, 2], events.Select(e => e.Data[0]));
        Assert.All(events, e => Assert.False(e.Synchronous));
    }

    [Fact]
    public void Address_ShortNameForBind1_ShowsInPollReply_EvenWithPortName()
    {
        var settings = new ArtNetNodeSettings { Ports = [ArtNetPortConfig.Output(new PortAddress(1), "Port name")] };
        var node = new ArtNetNode(settings);
        node.InjectDatagram(new ArtAddressPacket { BindIndex = 1, ShortName = "New" }.ToArray(), A);
        Assert.Equal("New", settings.ShortName);
        Assert.Equal("New", node.CreatePollReplies()[0].ShortName);
    }

    [Fact]
    public void Address_ForUnknownBind_ChangesNothing()
    {
        var settings = new ArtNetNodeSettings { ShortName = "Keep", Ports = [ArtNetPortConfig.Output(new PortAddress(1))] };
        var node = new ArtNetNode(settings);
        bool changed = false;
        node.ConfigurationChanged += (_, _) => changed = true;
        node.InjectDatagram(new ArtAddressPacket { BindIndex = 5, ShortName = "Other", Command = ArtNetAddressCommand.FailZero }.ToArray(), A);
        Assert.False(changed);
        Assert.Equal("Keep", settings.ShortName);
        Assert.Equal(ArtNetFailsafeState.Hold, node.FailsafeState);
    }

    [Fact]
    public void Address_UnchangedAcnPriority_IsNotAChange()
    {
        var node = new ArtNetNode(new ArtNetNodeSettings { Ports = [ArtNetPortConfig.Output(new PortAddress(1))] });
        int changes = 0;
        node.ConfigurationChanged += (_, _) => changes++;
        node.InjectDatagram(new ArtAddressPacket { AcnPriority = 100 }.ToArray(), A); // 100 is the default
        Assert.Equal(0, changes);
        node.InjectDatagram(new ArtAddressPacket { AcnPriority = 150 }.ToArray(), A);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Address_ClearOp_RaisesUniverseChanged()
    {
        var u = new PortAddress(1);
        var node = new ArtNetNode(new ArtNetNodeSettings { Ports = [ArtNetPortConfig.Output(u)] });
        var events = new List<ArtNetUniverseEventArgs>();
        node.UniverseChanged += (_, e) => events.Add(e);
        node.InjectDatagram(Dmx(u, 0, 9, 9), A);
        node.InjectDatagram(ArtAddressPacket.ForCommand(ArtNetAddressCommand.ClearOp0).ToArray(), A);
        Assert.Equal(2, events.Count);
        Assert.All(events[1].Data, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Dispose_FromEventHandlerOnReceiveThread_DoesNotDeadlock()
    {
        int port = 20000 + Random.Shared.Next(20000);
        var node = new ArtNetNode(new ArtNetNodeSettings { Port = port, SendPolls = false, ReplyToPolls = false, IgnoreOwnDmx = false });
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.TimeCodeReceived += (_, _) =>
        {
            node.Dispose();
            disposed.TrySetResult();
        };
        await node.StartAsync();
        using var udp = new UdpClient();
        await udp.SendAsync(new ArtTimeCodePacket().ToArray(), new IPEndPoint(IPAddress.Loopback, port));
        var done = await Task.WhenAny(disposed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(disposed.Task, done);
        Assert.False(node.IsRunning);
    }

    [Fact]
    public async Task Dispose_FromTaskStartedByHandler_WaitsForTheLoops()
    {
        int port = 20000 + Random.Shared.Next(20000);
        var node = new ArtNetNode(new ArtNetNodeSettings { Port = port, SendPolls = false, ReplyToPolls = false });
        await node.StartAsync();
        var loops = ((List<Task>)typeof(ArtNetNode).GetField("_loops", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node)!).ToArray();
        Task? disposing = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.TimeCodeReceived += (_, _) =>
        {
            disposing = Task.Run(async () => await node.DisposeAsync()); // not the loop thread: must wait
            started.TrySetResult();
            Thread.Sleep(300); // keep the receive loop busy so returning early is observable
        };
        using var udp = new UdpClient();
        await udp.SendAsync(new ArtTimeCodePacket().ToArray(), new IPEndPoint(IPAddress.Loopback, port));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await disposing!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(loops, t => Assert.True(t.IsCompleted));
    }

    [Fact]
    public async Task Restart_FromEventHandler_Works()
    {
        int port = 20000 + Random.Shared.Next(20000);
        var node = new ArtNetNode(new ArtNetNodeSettings { Port = port, SendPolls = false, ReplyToPolls = false });
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.TimeCodeReceived += (_, _) =>
        {
            node.StopAsync().GetAwaiter().GetResult();
            node.StartAsync().GetAwaiter().GetResult();
            restarted.TrySetResult();
        };
        await node.StartAsync();
        using var udp = new UdpClient();
        await udp.SendAsync(new ArtTimeCodePacket().ToArray(), new IPEndPoint(IPAddress.Loopback, port));
        await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(node.IsRunning);
        await node.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(node.IsRunning);
    }

    [Fact]
    public void UpdatePorts_IsAtomicWithRemoteProgramming()
    {
        var settings = new ArtNetNodeSettings { Ports = [ArtNetPortConfig.Output(new PortAddress(1)), ArtNetPortConfig.Output(new PortAddress(2), "Monitor")] };
        var node = new ArtNetNode(settings);
        var errors = new List<Exception>();
        node.Error += (_, e) => { lock (errors) errors.Add(e.Exception); };
        var rename = new ArtAddressPacket { BindIndex = 2, ShortName = "Remote" }.ToArray();
        var from = new IPEndPoint(IPAddress.Parse("2.0.0.10"), ArtNetConstants.Port);

        var remote = Task.Run(() => { for (int i = 0; i < 2000; i++) node.InjectDatagram(rename, from); });
        for (int i = 0; i < 2000; i++)
        {
            bool add = i % 2 == 0;
            node.UpdatePorts(ports =>
            {
                var list = ports.Where(p => p.Address != new PortAddress(2)).ToList();
                if (add) list.Add(ArtNetPortConfig.Output(new PortAddress(2), "Monitor"));
                return list;
            });
        }
        remote.Wait();
        Assert.Empty(errors);
        Assert.Single(settings.Ports); // last update removed the second port
    }
    // ------------------------------------------------------------------ full review 2026-09-29

    [Fact]
    public void PollReply_OnlyReportCounterChanged_IsNotAnUpdate()
    {
        var node = new ArtNetNode();
        int updated = 0;
        node.NodeUpdated += (_, _) => updated++;
        for (int i = 1; i <= 5; i++)
        {
            var r = new ArtPollReplyPacket { ShortName = "GW", NodeReport = ArtPollReplyPacket.FormatNodeReport(ArtNetNodeReportCode.PowerOk, i) };
            node.InjectDatagram(r.ToArray(), A);
        }
        Assert.Equal(0, updated);
        Assert.Equal(5, node.Nodes[0].ReplyCount);
        Assert.Contains("[0005]", node.Nodes[0].NodeReport); // the latest report is still stored

        var changedCode = new ArtPollReplyPacket { ShortName = "GW", NodeReport = ArtPollReplyPacket.FormatNodeReport(ArtNetNodeReportCode.DmxError, 6) };
        node.InjectDatagram(changedCode.ToArray(), A);
        Assert.Equal(1, updated);
    }

    [Fact]
    public async Task Stop_ForgetsMergeSources_KeepsLevels()
    {
        int port = 20000 + Random.Shared.Next(20000);
        var node = new ArtNetNode(new ArtNetNodeSettings { Port = port, SendPolls = false, ReplyToPolls = false });
        await node.StartAsync();
        node.InjectDatagram(Dmx(new PortAddress(1), 1, 200, 200), A);
        await node.StopAsync();
        Assert.Equal(200, node.GetUniverse(new PortAddress(1))![1]);

        await node.StartAsync();
        ArtNetUniverseEventArgs? last = null;
        node.UniverseChanged += (_, e) => last = e;
        node.InjectDatagram(Dmx(new PortAddress(1), 1, 10, 10), B);
        await node.StopAsync();
        Assert.False(last!.Merging);
        Assert.Equal(10, last.Data[0]);
    }

    [Fact]
    public void ProtocolVersionBelow14_IsIgnoredButLogged()
    {
        var node = new ArtNetNode();
        int changed = 0, logged = 0;
        node.UniverseChanged += (_, _) => changed++;
        node.PacketReceived += (_, _) => logged++;
        node.InjectDatagram(new ArtDmxPacket { PortAddress = new PortAddress(1), Sequence = 1, Data = [1, 2], ProtocolVersion = 13 }.ToArray(), A);
        Assert.Equal(0, changed);
        Assert.Null(node.GetUniverse(new PortAddress(1)));
        Assert.Equal(1, logged);

        node.InjectDatagram(Dmx(new PortAddress(1), 1, 1, 2), A);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task KeepAlive_RepeatsTheSentLength()
    {
        int rxPort = 20000 + Random.Shared.Next(20000);
        var rx = new ArtNetNode(new ArtNetNodeSettings { Port = rxPort, SendPolls = false, ReplyToPolls = false });
        var lengths = new System.Collections.Concurrent.ConcurrentQueue<int>();
        rx.DmxReceived += (_, e) => lengths.Enqueue(e.Packet.Data.Length);
        await rx.StartAsync();
        var tx = new ArtNetNode(new ArtNetNodeSettings { Port = rxPort + 1, SendPolls = false, ReplyToPolls = false,
            DmxKeepAlive = TimeSpan.FromMilliseconds(150) });
        tx.Settings.StaticDmxTargets.Add(new IPEndPoint(IPAddress.Loopback, rxPort));
        await tx.StartAsync();
        try
        {
            await tx.SendDmxAsync(new PortAddress(1), new byte[24]);
            await tx.SetChannelsAsync(new PortAddress(1), 30, new byte[] { 1, 2 }); // grows the frame to 32 channels
            await Task.Delay(600);
        }
        finally
        {
            await tx.StopAsync();
            await rx.StopAsync();
        }
        var seen = lengths.ToArray();
        Assert.True(seen.Length >= 3, $"only {seen.Length} frames received");
        Assert.Equal(24, seen[0]);
        Assert.All(seen[1..], l => Assert.Equal(32, l));
    }
}
