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
            if (_service.Settings.AutoStart) await _service.StartAsync();
        };
        window.Destroying += async (_, _) =>
        {
            WindowPlacement.Save(window);
            await _service.StopAsync();
        };
        return window;
    }
}
