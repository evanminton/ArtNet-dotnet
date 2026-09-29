using ArtNet.Shared.Services;
using ArtNet.Desktop.Services;

namespace ArtNet.Desktop;

public partial class App : Application
{
    private readonly ArtNetService _service;

    public App(ArtNetService service)
    {
        InitializeComponent();
        _service = service;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell(_service)) { Title = "Art-Net Desktop" };
        WindowPlacement.Restore(window);
        window.Created += async (_, _) =>
        {
            WindowPlacement.RestoreMaximized(window);
            if (_service.Settings.AutoStart) await _service.StartAsync();
        };
        // Synchronous: the process can exit before an awaited stop finishes.
        window.Destroying += (_, _) =>
        {
            WindowPlacement.Save(window);
            _service.Shutdown();
        };
        return window;
    }
}
