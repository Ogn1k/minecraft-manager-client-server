using Avalonia;
using System.Globalization;
using MinecraftManager.Core.Models;
using MinecraftManager.Infrastructure.Persistence;

namespace MinecraftManager.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstance = new Mutex(true, "MinecraftManager.Client.SingleInstance", out var createdNew);
        if (!createdNew) return;

        var settings = LoadBootstrapSettings();
        App.BootstrapSettings = settings;
        var culture = CultureInfo.GetCultureInfo(settings.Language == LanguagePreference.Russian ? "ru-RU" : "en-US");
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static ApplicationSettings LoadBootstrapSettings()
    {
        try
        {
            // Avalonia has not installed its UI synchronization context yet, so
            // synchronously waiting for this small one-time read cannot deadlock
            // the UI thread. The rest of application loading remains asynchronous.
            return new JsonConfigurationStore(new ApplicationPaths())
                .LoadAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                .Settings;
        }
        catch
        {
            // Let the window open with safe defaults. Its normal asynchronous load
            // will report an unreadable configuration to the user.
            return new ApplicationSettings();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
