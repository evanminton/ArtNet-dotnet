using ArtNet.Shared.Pages;
using ArtNet.Shared.Services;

namespace ArtNet.Maui;

/// <summary>Tab bar: Nodes, DMX, Show, Packets, Reference, Settings.</summary>
public sealed class AppShell : Shell
{
    public AppShell(ArtNetService service)
    {
        Title = "Art-Net Monitor";
        FlyoutBehavior = FlyoutBehavior.Disabled;
        var tabs = new TabBar();
        tabs.Items.Add(Tab("Nodes", "nodes", () => new NodesPage(service)));
        tabs.Items.Add(Tab("DMX", "dmx", () => new DmxPage(service)));
        tabs.Items.Add(Tab("Show", "show", () => new ShowPage(service)));
        tabs.Items.Add(Tab("Packets", "packets", () => new PacketsPage(service)));
        tabs.Items.Add(Tab("Reference", "reference", () => new ReferencePage()));
        tabs.Items.Add(Tab("Settings", "settings", () => new SettingsPage(service)));
        Items.Add(tabs);
    }

    private static ShellContent Tab(string title, string route, Func<Page> create) => new()
    {
        Title = title,
        Route = route,
        ContentTemplate = new DataTemplate(() => create()),
    };
}
