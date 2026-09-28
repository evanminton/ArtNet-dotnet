using ArtNet.Desktop.Pages;
using ArtNet.Desktop.Services;

namespace ArtNet.Desktop;

/// <summary>
/// Desktop shell: a fixed sidebar (Nodes, DMX, Show, Packets, Reference, Settings) with the node status and
/// Start/Stop in its footer, and a Windows menu bar with keyboard shortcuts on every page.
/// </summary>
public sealed class AppShell : Shell
{
    private readonly ArtNetService _service;
    private readonly List<FlyoutItem> _sections = [];

    public AppShell(ArtNetService service)
    {
        _service = service;
        Title = "Art-Net Desktop";
        FlyoutBehavior = FlyoutBehavior.Locked;
        FlyoutWidth = 210;
        BindingContext = service;

        Add("Nodes", "nodes", () => new NodesPage(service));
        Add("DMX", "dmx", () => new DmxPage(service));
        Add("Show", "show", () => new ShowPage(service));
        Add("Packets", "packets", () => new PacketsPage(service));
        Add("Reference", "reference", () => new ReferencePage());
        Add("Settings", "settings", () => new SettingsPage(service));

        FlyoutHeader = new VerticalStackLayout
        {
            Padding = new Thickness(16, 16, 16, 8),
            Children =
            {
                new Label { Text = "Art-Net Desktop", Style = Ui.Style("Heading") },
                Ui.Caption("Art-Net 4 · UDP 6454"),
            },
        };
        FlyoutFooter = Footer();

        Navigated += (_, _) => AttachMenu(CurrentPage);
    }

    private void Add(string title, string route, Func<Page> create)
    {
        var item = new FlyoutItem
        {
            Title = title,
            Route = route,
            Items = { new ShellContent { Title = title, Route = route + "-page", ContentTemplate = new DataTemplate(() => create()) } },
        };
        _sections.Add(item);
        Items.Add(item);
    }

    private View Footer()
    {
        var startStop = new Button { Margin = new Thickness(0, 4, 0, 0) };
        startStop.SetBinding(Button.TextProperty, static (ArtNetService s) => s.StartStopText);
        startStop.Clicked += async (_, _) => await _service.ToggleAsync();
        var status = Ui.Caption();
        status.LineBreakMode = LineBreakMode.WordWrap;
        status.SetBinding(Label.TextProperty, static (ArtNetService s) => s.StatusText);
        var error = Ui.Caption();
        error.TextColor = Colors.OrangeRed;
        error.LineBreakMode = LineBreakMode.WordWrap;
        error.SetBinding(Label.TextProperty, static (ArtNetService s) => s.LastError);
        var tc = Ui.Mono("", 15);
        tc.SetBinding(Label.TextProperty, static (ArtNetService s) => s.LastTimeCode);
        return new VerticalStackLayout
        {
            Padding = new Thickness(16, 8, 16, 16),
            Spacing = 4,
            BindingContext = _service,
            Children = { Ui.Caption("Timecode"), tc, status, error, startStop },
        };
    }

    // ------------------------------------------------------------------ menu bar

    private void AttachMenu(Page? page)
    {
        if (page is null || page.MenuBarItems.Count > 0) return;

        var file = new MenuBarItem { Text = "File" };
        file.Add(Item("Export packet log", Ctrl("E"), async () => DesktopActions.OpenFile(await DesktopActions.ExportPacketLogAsync(_service))));
        file.Add(Item("Export node list", Ctrl("E", shift: true), async () => DesktopActions.OpenFile(await DesktopActions.ExportNodesAsync(_service))));
        file.Add(Item("Open exports folder", null, () => { DesktopActions.OpenFolder(DesktopActions.ExportFolder); return Task.CompletedTask; }));
        file.Add(new MenuFlyoutSeparator());
        file.Add(Item("Exit", null, () => { Application.Current?.Quit(); return Task.CompletedTask; }));

        var node = new MenuBarItem { Text = "Node" };
        node.Add(Item("Start / Stop", Key("F5"), _service.ToggleAsync));
        node.Add(Item("Restart", Ctrl("F5"), _service.RestartAsync));
        node.Add(Item("Poll now", Ctrl("R"), () => _service.RequireNode().PollAsync().AsTask()));
        node.Add(Item("ArtSync", null, () => _service.RequireNode().SendSyncAsync().AsTask()));
        node.Add(new MenuFlyoutSeparator());
        node.Add(Item("Clear packet log", Ctrl("L"), () => { _service.ClearLog(); return Task.CompletedTask; }));
        node.Add(Item("Clear show events", null, () => { _service.ClearShowEvents(); return Task.CompletedTask; }));

        var go = new MenuBarItem { Text = "Go" };
        for (int i = 0; i < _sections.Count; i++)
        {
            var section = _sections[i];
            go.Add(Item(section.Title, Ctrl((i + 1).ToString()), () => { CurrentItem = section; return Task.CompletedTask; }));
        }

        var help = new MenuBarItem { Text = "Help" };
        help.Add(Item("Network / firewall help", Key("F1"), () => Alert("Network help", DesktopActions.FirewallHelp)));
        help.Add(Item("About Art-Net Desktop", null, () => Alert("About", DesktopActions.AboutText)));

        page.MenuBarItems.Add(file);
        page.MenuBarItems.Add(node);
        page.MenuBarItems.Add(go);
        page.MenuBarItems.Add(help);
    }

    private static KeyboardAccelerator Ctrl(string key, bool shift = false) => new()
    {
        Key = key,
        Modifiers = shift ? KeyboardAcceleratorModifiers.Ctrl | KeyboardAcceleratorModifiers.Shift : KeyboardAcceleratorModifiers.Ctrl,
    };

    private static KeyboardAccelerator Key(string key) => new() { Key = key, Modifiers = KeyboardAcceleratorModifiers.None };

    private MenuFlyoutItem Item(string text, KeyboardAccelerator? accelerator, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (accelerator is not null) item.KeyboardAccelerators.Add(accelerator);
        item.Clicked += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { await Alert("Art-Net Desktop", ex.Message); }
        };
        return item;
    }

    private Task Alert(string title, string message) => DisplayAlertAsync(title, message, "OK");
}
