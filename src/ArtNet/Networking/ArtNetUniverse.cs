using System.Net;

namespace ArtNet.Networking;

/// <summary>
/// Receive state of one Port-Address: up to two merged ArtDmx sources (HTP or LTP), sequence checking, the
/// AcCancelMerge takeover. Synchronous (ArtSync) output is buffered by <see cref="ArtNetNode"/>.
/// </summary>
public sealed class ArtNetUniverse
{
    private sealed class Source(IPAddress address, byte physical)
    {
        public IPAddress Address { get; } = address;
        public byte Physical { get; } = physical;
        public byte[] Data { get; } = new byte[ArtNetConstants.DmxChannels];
        public byte LastSequence { get; set; }
        public DateTime LastSeen { get; set; }
        public long Order { get; set; }
    }

    private readonly object _lock = new();
    private readonly List<Source> _sources = [];
    private readonly byte[] _output = new byte[ArtNetConstants.DmxChannels];
    private (IPAddress Address, byte Physical)? _exclusive;
    private bool _cancelMergePending;
    private long _order;

    public ArtNetUniverse(PortAddress address, ArtNetMergeMode mergeMode = ArtNetMergeMode.Htp)
    {
        Address = address;
        MergeMode = mergeMode;
    }

    public PortAddress Address { get; }

    public ArtNetMergeMode MergeMode { get; set; }

    /// <summary>Hold time of a failed source.</summary>
    public TimeSpan MergeTimeout { get; set; } = ArtNetConstants.MergeTimeout;

    /// <summary>Drop out-of-order packets (sequence field).</summary>
    public bool UseSequenceNumbers { get; set; } = true;

    /// <summary>A packet up to this many sequence steps behind the last accepted one is treated as out of order.</summary>
    public const int SequenceWindow = 20;

    /// <summary>Accepted ArtDmx packets.</summary>
    public long PacketCount { get; private set; }

    /// <summary>Packets dropped (out of order, third source, excluded after cancel merge).</summary>
    public long DroppedCount { get; private set; }

    /// <summary>Number of channels in the last accepted packet.</summary>
    public int LastLength { get; private set; }

    public DateTime LastUpdate { get; private set; }

    /// <summary>True while two sources are live.</summary>
    public bool IsMerging
    {
        get { lock (_lock) { Expire(DateTime.UtcNow); return _sources.Count > 1; } }
    }

    /// <summary>Current source addresses.</summary>
    public IReadOnlyList<IPAddress> Sources
    {
        get { lock (_lock) { Expire(DateTime.UtcNow); return _sources.Select(s => s.Address).ToArray(); } }
    }

    /// <summary>Data received within <see cref="ArtNetConstants.StatusTimeout"/> (like the DMX status LED).</summary>
    public bool IsActive => DateTime.UtcNow - LastUpdate < ArtNetConstants.StatusTimeout;

    /// <summary>
    /// Copy of the merged output (512 channels) as received. In synchronous (ArtSync) mode this can be ahead of the
    /// output raised by <see cref="ArtNetNode.UniverseChanged"/>, which waits for the next ArtSync.
    /// </summary>
    public byte[] GetData()
    {
        lock (_lock) return (byte[])_output.Clone();
    }

    /// <summary>Level of a 1-based channel.</summary>
    public byte this[int channel]
    {
        get { lock (_lock) return channel is >= 1 and <= ArtNetConstants.DmxChannels ? _output[channel - 1] : (byte)0; }
    }

    /// <summary>
    /// ArtAddress AcCancelMerge: if currently merging, the next ArtDmx becomes the only accepted source. Ignored
    /// when not merging.
    /// </summary>
    public void CancelMerge()
    {
        lock (_lock)
        {
            Expire(DateTime.UtcNow);
            if (_sources.Count > 1) _cancelMergePending = true;
        }
    }

    /// <summary>Clears the output buffer (ArtAddress AcClearOp).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_output);
            foreach (var s in _sources) Array.Clear(s.Data);
        }
    }

    /// <summary>
    /// Forgets the merge sources (and any AcCancelMerge takeover) but keeps the last output, so the next ArtDmx is
    /// treated as the only source. Used when the node stops.
    /// </summary>
    internal void ResetSources()
    {
        lock (_lock)
        {
            _sources.Clear();
            _exclusive = null;
            _cancelMergePending = false;
        }
    }

    /// <summary>
    /// Applies an ArtDmx. Returns the merged output (a copy) or null when the packet was dropped.
    /// </summary>
    public byte[]? Apply(IPAddress from, ArtDmxPacket packet, out bool merging)
    {
        merging = false;
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            Expire(now);
            var key = (from, packet.Physical);

            if (_cancelMergePending)
            {
                _cancelMergePending = false;
                _exclusive = key;
                _sources.RemoveAll(s => !(s.Address.Equals(from) && s.Physical == packet.Physical));
            }
            if (_exclusive is { } ex && !(ex.Address.Equals(from) && ex.Physical == packet.Physical))
            {
                DroppedCount++;
                return null;
            }

            var src = _sources.FirstOrDefault(s => s.Address.Equals(from) && s.Physical == packet.Physical);
            if (src is null)
            {
                if (_sources.Count >= 2) { DroppedCount++; return null; } // merging is limited to two sources
                src = new Source(from, packet.Physical);
                _sources.Add(src);
            }
            else if (UseSequenceNumbers && packet.Sequence != 0 && src.LastSequence != 0)
            {
                // Drop only packets slightly older than the last one (reordering on the network). A larger jump
                // back is a restarted controller: accept it rather than freezing output for up to 127 frames.
                int diff = (packet.Sequence - src.LastSequence) & 0xFF;
                if (diff >= 256 - SequenceWindow) { DroppedCount++; return null; }
            }

            src.LastSequence = packet.Sequence;
            src.LastSeen = now;
            src.Order = ++_order;
            var d = packet.Data;
            int n = Math.Min(d.Length, ArtNetConstants.DmxChannels);
            d.AsSpan(0, n).CopyTo(src.Data);
            src.Data.AsSpan(n).Clear();

            if (_sources.Count == 1)
                src.Data.CopyTo(_output, 0);
            else if (MergeMode == ArtNetMergeMode.Ltp)
                _sources.MaxBy(s => s.Order)!.Data.CopyTo(_output, 0);
            else
            {
                var a = _sources[0].Data; var b = _sources[1].Data;
                for (int i = 0; i < _output.Length; i++) _output[i] = Math.Max(a[i], b[i]);
            }

            merging = _sources.Count > 1;
            PacketCount++;
            LastLength = n;
            LastUpdate = now;
            return (byte[])_output.Clone();
        }
    }

    private void Expire(DateTime now)
    {
        var cutoff = now - MergeTimeout;
        _sources.RemoveAll(s => s.LastSeen < cutoff);
        if (_exclusive is { } ex && !_sources.Any(s => s.Address.Equals(ex.Address) && s.Physical == ex.Physical))
            _exclusive = null; // the controller that took over has gone: accept anyone again
    }

    public override string ToString() => $"Universe {Address} · {PacketCount} packets · {(IsMerging ? "merging" : "single source")}";
}
