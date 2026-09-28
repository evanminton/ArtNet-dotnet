using ArtNet.Desktop.Services;
using ArtNet.Networking;

namespace ArtNet.Desktop.Pages;

/// <summary>Discovered Art-Net devices (one row per IP / bind index).</summary>
public sealed class NodesPage : ContentPage
{
    private readonly ArtNetService _service;

    public NodesPage(ArtNetService service)
    {
        _service = service;
        Title = "Nodes";
        BindingContext = service;

        var status = Ui.Caption();
        status.SetBinding(Label.TextProperty, static (ArtNetService s) => s.StatusText);
        var error = Ui.Caption();
        error.TextColor = Colors.OrangeRed;
        error.SetBinding(Label.TextProperty, static (ArtNetService s) => s.LastError);

        var startStop = new Button();
        startStop.SetBinding(Button.TextProperty, static (ArtNetService s) => s.StartStopText);
        startStop.Clicked += async (_, _) => await _service.ToggleAsync();

        var poll = Ui.Button("Poll now", async () => await _service.RequireNode().PollAsync(), error);
        var locateAll = Ui.Button("Locate all", async () =>
        {
            var node = _service.RequireNode();
            foreach (var n in _service.Nodes.Where(n => !n.IsLocal))
                await node.SendAddressAsync(n.Address, ArtAddressPacket.ForCommand(ArtNetAddressCommand.LedLocate, n.BindIndex), TimeSpan.FromMilliseconds(300));
        }, error);
        var normalAll = Ui.Button("LEDs normal", async () =>
        {
            var node = _service.RequireNode();
            foreach (var n in _service.Nodes.Where(n => !n.IsLocal))
                await node.SendAddressAsync(n.Address, ArtAddressPacket.ForCommand(ArtNetAddressCommand.LedNormal, n.BindIndex), TimeSpan.FromMilliseconds(300));
        }, error);

        var list = new CollectionView
        {
            ItemsSource = service.Nodes,
            SelectionMode = SelectionMode.Single,
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical) { ItemSpacing = 8 },
            EmptyView = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 6,
                Children =
                {
                    new Label { Text = "No Art-Net devices yet.", Style = Ui.Style("Heading"), HorizontalOptions = LayoutOptions.Center },
                    new Label { Text = "Start the node; every device answering ArtPoll (every 2.5 s) appears here with its ports, universes and status. " +
                                       "This app answers polls too, so it lists itself.", Style = Ui.Style("Caption"), HorizontalTextAlignment = TextAlignment.Center },
                },
            },
            ItemTemplate = new DataTemplate(() =>
            {
                var name = new Label { Style = Ui.Style("Heading") };
                name.SetBinding(Label.TextProperty, static (ArtNetRemoteNode n) => n.DisplayName);
                var style = new Label { FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.End };
                style.SetBinding(Label.TextProperty, static (ArtNetRemoteNode n) => n.StyleName);
                var longName = new Label();
                longName.SetBinding(Label.TextProperty, static (ArtNetRemoteNode n) => n.LongName);
                var address = Ui.Caption();
                address.SetBinding(Label.TextProperty, static (ArtNetRemoteNode n) => n.AddressText);
                var universes = new Label();
                universes.SetBinding(Label.TextProperty, static (ArtNetRemoteNode n) => n.UniversesText);
                var report = Ui.Caption();
                report.SetBinding(Label.TextProperty, static (ArtNetRemoteNode n) => n.NodeReportText);

                var grid = new Grid
                {
                    ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
                    RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto)],
                    ColumnSpacing = 8,
                    RowSpacing = 2,
                };
                grid.Add(name, 0, 0);
                grid.Add(style, 1, 0);
                grid.Add(longName, 0, 1);
                grid.Add(address, 1, 1);
                grid.Add(universes, 0, 2);
                grid.Add(report, 0, 3);
                Grid.SetColumnSpan(report, 2);
                return new Border { Style = Ui.Style("Card"), Content = grid };
            }),
        };
        list.SelectionChanged += async (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is not ArtNetRemoteNode node) return;
            list.SelectedItem = null;
            await Navigation.PushAsync(new NodeDetailPage(_service, node));
        };

        var header = new VerticalStackLayout
        {
            Spacing = 6,
            Children = { Ui.Row(startStop, poll, locateAll, normalAll), status, error },
        };
        var root = new Grid { RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)], Padding = 12, RowSpacing = 10 };
        root.Add(header, 0, 0);
        root.Add(list, 0, 1);
        Content = root;
    }
}
