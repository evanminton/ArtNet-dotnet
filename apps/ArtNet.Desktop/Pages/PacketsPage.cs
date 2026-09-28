using ArtNet.Desktop.Services;

namespace ArtNet.Desktop.Pages;

/// <summary>Live packet log with OpCode filter; tap a packet for every field in readable form and its raw bytes.</summary>
public sealed class PacketsPage : ContentPage
{
    private sealed record FilterOption(string Name, ArtNetOpCode? OpCode)
    {
        public override string ToString() => Name;
    }

    public PacketsPage(ArtNetService service)
    {
        Title = "Packets";
        var filters = new List<FilterOption> { new("All packets", null) };
        filters.AddRange(ArtNetOptionCatalog.OpCodes.Select(o => new FilterOption($"{o.Name} ({o.RawText})", o.Value)));
        var picker = Ui.Picker(filters, 0, 280);
        picker.SelectedIndexChanged += (_, _) => service.LogFilter = (picker.SelectedItem as FilterOption)?.OpCode;

        var list = new CollectionView
        {
            ItemsSource = service.PacketLog,
            SelectionMode = SelectionMode.Single,
            EmptyView = Ui.Caption("No packets yet. ArtDmx and ArtSync are hidden unless 'DMX' is on (or selected in the filter)."),
            ItemTemplate = new DataTemplate(() =>
            {
                var time = Ui.Mono("", 12);
                time.SetBinding(Label.TextProperty, static (PacketLogEntry e) => e.TimeText);
                var summary = new Label { LineBreakMode = LineBreakMode.TailTruncation };
                summary.SetBinding(Label.TextProperty, static (PacketLogEntry e) => e.Summary);
                var source = Ui.Caption();
                source.SetBinding(Label.TextProperty, static (PacketLogEntry e) => e.Source);
                var g = new Grid
                {
                    ColumnDefinitions = [new(new GridLength(96)), new(GridLength.Star)],
                    RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
                    Padding = new Thickness(4, 6),
                    ColumnSpacing = 8,
                };
                g.Add(time, 0, 0);
                g.Add(summary, 1, 0);
                g.Add(source, 1, 1);
                return g;
            }),
        };
        list.SelectionChanged += async (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is not PacketLogEntry entry) return;
            list.SelectedItem = null;
            await Navigation.PushAsync(new PacketDetailPage(entry));
        };

        var header = Ui.Row(
            picker,
            Ui.Switch("DMX", service.LogDmx, on => service.LogDmx = on),
            Ui.Switch("Polls", service.LogPolls, on => service.LogPolls = on),
            Ui.Switch("Pause", service.LogPaused, on => service.LogPaused = on),
            Ui.Button("Clear", service.ClearLog));

        var root = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)], Padding = 12, RowSpacing = 10 };
        root.Add(header, 0, 0);
        root.Add(list, 0, 1);
        Content = root;
    }
}

/// <summary>Full human-readable dump of one packet plus its raw bytes.</summary>
public sealed class PacketDetailPage : ContentPage
{
    public PacketDetailPage(PacketLogEntry entry)
    {
        Title = entry.TypeText;
        Content = Ui.Page(
            Ui.Caption($"{entry.TimeText} · from {entry.Source} · {entry.Packet.Size} bytes"),
            Ui.Caption(entry.Packet.OpCode.ToDescription()),
            Ui.Card("Fields", Ui.Fields(entry.Packet.Describe())),
            Ui.Card("Raw bytes", Ui.Mono(ArtNetFormatter.HexDump(entry.Packet.ToArray()), 11)));
    }
}
