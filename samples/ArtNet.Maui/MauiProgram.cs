using ArtNet.Maui.Services;

namespace ArtNet.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddSingleton<AppSettings>(_ => AppSettings.Load());
        builder.Services.AddSingleton<ArtNetService>();
        return builder.Build();
    }
}
