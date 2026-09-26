using ArtNet.Maui.Services;

namespace ArtNet.Maui;

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
        var window = new Window(new AppShell(_service)) { Title = "Art-Net Monitor" };
        window.Created += async (_, _) =>
        {
            if (_service.Settings.AutoStart) await _service.StartAsync();
        };
        window.Destroying += async (_, _) => await _service.StopAsync();
        return window;
    }
}
