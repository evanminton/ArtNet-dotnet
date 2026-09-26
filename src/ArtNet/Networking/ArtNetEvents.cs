using System.Net;

namespace ArtNet.Networking;

/// <summary>A received packet.</summary>
public sealed class ArtNetPacketEventArgs<T>(T packet, IPEndPoint remoteEndPoint, ArtNetRemoteNode? node) : EventArgs where T : ArtNetPacket
{
    public T Packet { get; } = packet;
    public IPEndPoint RemoteEndPoint { get; } = remoteEndPoint;

    /// <summary>The known node at the source address (bind 1), if any.</summary>
    public ArtNetRemoteNode? Node { get; } = node;

    public DateTime Received { get; } = DateTime.Now;
}

/// <summary>A node appeared, changed or disappeared.</summary>
public sealed class ArtNetNodeEventArgs(ArtNetRemoteNode node, bool timedOut = false) : EventArgs
{
    public ArtNetRemoteNode Node { get; } = node;

    /// <summary>For NodeLost: true when the node stopped replying (always the case in Art-Net, which has no opt-out).</summary>
    public bool TimedOut { get; } = timedOut;
}

/// <summary>New output levels for a universe (after merge and sync handling).</summary>
public sealed class ArtNetUniverseEventArgs(PortAddress address, byte[] data, IReadOnlyList<IPAddress> sources, bool merging, bool synchronous) : EventArgs
{
    public PortAddress Address { get; } = address;

    /// <summary>512 levels (a copy; index 0 = channel 1).</summary>
    public byte[] Data { get; } = data;

    /// <summary>Contributing source addresses (1 or 2).</summary>
    public IReadOnlyList<IPAddress> Sources { get; } = sources;

    public bool Merging { get; } = merging;

    /// <summary>True when released by ArtSync.</summary>
    public bool Synchronous { get; } = synchronous;
}

/// <summary>Firmware upload progress.</summary>
public sealed record ArtNetFirmwareProgress(int Block, int TotalBlocks, ArtNetFirmwareReplyType LastReply)
{
    public double Fraction => TotalBlocks == 0 ? 0 : (double)Block / TotalBlocks;
    public override string ToString() => $"Block {Block}/{TotalBlocks} ({Fraction:P0}) – {LastReply.ToDisplayName()}";
}

/// <summary>A non-fatal error on a network thread.</summary>
public sealed class ArtNetErrorEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
