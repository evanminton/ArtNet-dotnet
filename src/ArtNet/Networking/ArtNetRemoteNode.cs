using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;

namespace ArtNet.Networking;

/// <summary>
/// A device discovered through ArtPollReply. One instance per (IP address, BindIndex); a multi-port gateway that
/// reports one port per bind appears as several instances sharing <see cref="Address"/> and <see cref="RootAddress"/>.
/// Implements <see cref="INotifyPropertyChanged"/> for data binding.
/// </summary>
public sealed class ArtNetRemoteNode : INotifyPropertyChanged
{
    internal ArtNetRemoteNode(IPAddress address, byte bindIndex)
    {
        Address = address;
        BindIndex = bindIndex;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Source address of the ArtPollReply.</summary>
    public IPAddress Address { get; }

    /// <summary>Bind index (0/1 = root device).</summary>
    public byte BindIndex { get; }

    /// <summary>Unicast endpoint (always port 0x1936).</summary>
    public IPEndPoint EndPoint => new(Address, ArtNetConstants.Port);

    /// <summary>The most recent ArtPollReply.</summary>
    public ArtPollReplyPacket Reply { get; private set; } = new();

    public DateTime FirstSeen { get; } = DateTime.UtcNow;

    private DateTime _lastSeen = DateTime.UtcNow;
    public DateTime LastSeen { get => _lastSeen; internal set => Set(ref _lastSeen, value); }

    /// <summary>True when the reply came from this machine.</summary>
    public bool IsLocal { get; internal set; }

    /// <summary>Number of ArtPollReply packets received.</summary>
    public int ReplyCount { get; private set; }

    public string ShortName => Reply.ShortName;
    public string LongName => Reply.LongName;
    public string DisplayName => string.IsNullOrWhiteSpace(Reply.ShortName) ? Address.ToString() : Reply.ShortName;
    public string NodeReport => Reply.NodeReport;
    public string NodeReportText => ArtPollReplyPacket.TryParseNodeReport(Reply.NodeReport, out var code, out _, out var text)
        ? $"{code.ToDisplayName()}: {text}" : Reply.NodeReport;
    public ArtNetStyle Style => Reply.Style;
    public string StyleName => Reply.Style.ToDisplayName();
    public ushort Oem => Reply.Oem;
    public string OemText => $"0x{Reply.Oem:X4}";
    public ushort EstaManufacturer => Reply.EstaManufacturer;
    public string EstaText => ArtNetText.FormatEsta(Reply.EstaManufacturer);
    public string FirmwareText => ArtNetText.FormatVersion(Reply.FirmwareVersion);
    public string MacAddress => Reply.MacAddress;
    public IPAddress ReportedAddress => Reply.IpAddress;
    public IPAddress RootAddress => Reply.BindIp.Equals(IPAddress.Any) ? Address : Reply.BindIp;
    public bool IsRoot => Reply.IsRootDevice;
    public ArtNetIndicatorState IndicatorState => Reply.IndicatorState;
    public bool RdmCapable => Reply.Status1Flags.HasFlag(ArtNetStatus1.RdmCapable);
    public bool DhcpConfigured => Reply.Status2.HasFlag(ArtNetStatus2.DhcpConfigured);
    public IReadOnlyList<ArtNetPortInfo> Ports => Reply.Ports;

    /// <summary>Universes this device subscribes to (SwIn / SwOut).</summary>
    public IReadOnlyList<PortAddress> Universes => Reply.SubscribedAddresses.Distinct().OrderBy(a => a).ToArray();

    /// <summary>"Out 1, 2 · In 5" style summary.</summary>
    public string UniversesText
    {
        get
        {
            var outs = Ports.Where(p => p.CanOutput).Select(p => p.OutputAddress.Value.ToString()).ToArray();
            var ins = Ports.Where(p => p.CanInput).Select(p => p.InputAddress.Value.ToString()).ToArray();
            var parts = new List<string>();
            if (outs.Length > 0) parts.Add("Out " + string.Join(", ", outs));
            if (ins.Length > 0) parts.Add("In " + string.Join(", ", ins));
            return parts.Count == 0 ? "No ports" : string.Join(" · ", parts);
        }
    }

    public string AddressText => BindIndex > 1 ? $"{Address} · bind {BindIndex}" : Address.ToString();

    public bool IsSubscribedTo(PortAddress address) => Reply.IsSubscribedTo(address);

    internal bool Update(ArtPollReplyPacket reply)
    {
        var old = Reply;
        Reply = reply;
        ReplyCount++;
        bool changed = ReplyCount == 1 || !old.ToArray().AsSpan().SequenceEqual(reply.ToArray());
        if (changed)
        {
            OnPropertyChanged(nameof(Reply));
            foreach (var p in new[] { nameof(ShortName), nameof(LongName), nameof(DisplayName), nameof(NodeReport), nameof(NodeReportText),
                         nameof(Style), nameof(StyleName), nameof(Oem), nameof(OemText), nameof(EstaText), nameof(FirmwareText),
                         nameof(MacAddress), nameof(ReportedAddress), nameof(RootAddress), nameof(IndicatorState), nameof(RdmCapable),
                         nameof(DhcpConfigured), nameof(Ports), nameof(Universes), nameof(UniversesText) })
                OnPropertyChanged(p);
        }
        OnPropertyChanged(nameof(ReplyCount));
        return changed;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() =>
        $"{DisplayName} ({LongName}) {AddressText} · {StyleName} · {UniversesText} · {NodeReportText}";
}
