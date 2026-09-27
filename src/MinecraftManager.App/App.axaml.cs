using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MinecraftManager.App.Services;
using MinecraftManager.App.ViewModels;
using MinecraftManager.App.Views;
using MinecraftManager.Core.Filesystem;
using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Security;
using MinecraftManager.Core.Services;
using MinecraftManager.Core.Updates;
using MinecraftManager.Infrastructure.ManagedServer;
using MinecraftManager.Infrastructure.Persistence;
using MinecraftManager.Infrastructure.Security;
using MinecraftManager.Infrastructure.Services;
using MinecraftManager.Infrastructure.Sources;
using MinecraftManager.Infrastructure.Updates;
using MinecraftManager.Core.Diagnostics;
using MinecraftManager.Infrastructure.Diagnostics;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Launching;
using MinecraftManager.Core.Profiles;
using MinecraftManager.Core.Runtime;
using MinecraftManager.Infrastructure.Launching;
using MinecraftManager.Infrastructure.Runtime;

namespace MinecraftManager.App;

public sealed class App : Application
{
    internal static ApplicationSettings BootstrapSettings { get; set; } = new();
    private ServiceProvider? provider;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        ApplyTheme(BootstrapSettings.Theme);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = provider.GetRequiredService<MainWindow>();
            desktop.Exit += async (_, _) => await provider.DisposeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(ThemePreference preference)
    {
        if (Current is not { } application) return;

        application.RequestedThemeVariant = preference switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(x => x.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(BootstrapSettings);
        services.AddSingleton(new Version(1, 0, 0));
        services.AddSingleton<IApplicationPaths, ApplicationPaths>();
        services.AddSingleton<IConfigurationStore, JsonConfigurationStore>();
        services.AddSingleton<IInstanceStateStore, JsonInstanceStateStore>();
        services.AddSingleton<ITransactionJournalStore, JsonTransactionJournalStore>();
        services.AddSingleton<IHistoryStore, JsonHistoryStore>();
        services.AddSingleton<ILoggerProvider, SafeFileLoggerProvider>();
        services.AddSingleton<IDiagnosticsService, DiagnosticsService>();
        services.AddSingleton<IManifestParser, ManifestParser>();
        services.AddSingleton<ISafePathResolver, SafePathResolver>();
        services.AddSingleton<IHashService, HashService>();
        services.AddSingleton<IUpdatePlanner, UpdatePlanner>();
        services.AddSingleton<IInstanceService, InstanceService>();
        services.AddSingleton<ISelectedInstanceContext, SelectedInstanceContext>();
        services.AddSingleton<IUpdateExecutor, UpdateExecutor>();
        services.AddSingleton<IRecoveryService, RecoveryService>();
        services.AddSingleton<IUpdateCoordinator, UpdateCoordinator>();
        services.AddSingleton<ISecureCredentialStore, PlatformCredentialStore>();
        services.AddSingleton<ManagedApiClient>();
        services.AddSingleton<IManagedApiClient>(x => x.GetRequiredService<ManagedApiClient>());
        services.AddSingleton<IClientRegistrationService>(x => x.GetRequiredService<ManagedApiClient>());
        services.AddSingleton<IServerConnectionService, SignalRConnectionService>();
        services.AddSingleton<IUpdateSourceFactory, UpdateSourceFactory>();
        services.AddSingleton<IExecutableDirectoryProvider, ExecutableDirectoryProvider>();
        services.AddSingleton<ILocalArchiveDiscoveryService, LocalArchiveDiscoveryService>();
        services.AddSingleton<IOfflineProfileStore, JsonOfflineProfileStore>();
        services.AddSingleton<IOfflineProfileService, OfflineProfileService>();
        services.AddSingleton<IJavaCompatibilityPolicy, JavaCompatibilityPolicy>();
        services.AddHttpClient<JavaRuntimeService>(client => client.Timeout = TimeSpan.FromMinutes(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<IJavaRuntimeService>(x => x.GetRequiredService<JavaRuntimeService>());
        services.AddTransient<MinecraftVersionResolver>();
        services.AddSingleton<IModLoaderRuntimeProvider>(new ConventionModLoaderRuntimeProvider(ModLoaderType.Fabric));
        services.AddSingleton<IModLoaderRuntimeProvider>(new ConventionModLoaderRuntimeProvider(ModLoaderType.Quilt));
        services.AddSingleton<IModLoaderRuntimeProvider>(new ConventionModLoaderRuntimeProvider(ModLoaderType.Forge));
        services.AddSingleton<IModLoaderRuntimeProvider>(new ConventionModLoaderRuntimeProvider(ModLoaderType.NeoForge));
        services.AddSingleton<IModLoaderRuntimeRegistry, ModLoaderRuntimeRegistry>();
        services.AddTransient<IMinecraftVersionResolver, ModLoaderAwareVersionResolver>();
        services.AddSingleton<GameProcessMonitor>();
        services.AddSingleton<IGameProcessMonitor>(x => x.GetRequiredService<GameProcessMonitor>());
        services.AddSingleton<ILaunchExecutor, LaunchExecutor>();
        services.AddSingleton<ILaunchPlanner, LaunchPlanner>();
        services.AddSingleton<IDesktopFolderService, DesktopFolderService>();
        services.AddSingleton<PortraitBackgroundService>();
        services.AddSingleton<IPackLaunchStatusService, PackLaunchStatusService>();
        services.AddTransient<ILaunchReadinessService, LaunchReadinessService>();
        services.AddTransient<IGameLaunchService, GameLaunchService>();
        services.AddHttpClient("updates", client => client.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient("managed", client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient("downloads", client => client.Timeout = TimeSpan.FromMinutes(10));
        services.AddHttpClient<MojangRuntimeMetadataSource>(client => client.Timeout = TimeSpan.FromSeconds(30)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<IRuntimeMetadataSource>(x => x.GetRequiredService<MojangRuntimeMetadataSource>());
        services.AddHttpClient<MinecraftRuntimeManager>(client => client.Timeout = TimeSpan.FromMinutes(10)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<IRuntimeManager>(x => x.GetRequiredService<MinecraftRuntimeManager>());
        services.AddHttpClient<TrustedModLoaderInstaller>(client => client.Timeout = TimeSpan.FromMinutes(2)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<IModLoaderInstaller>(x => x.GetRequiredService<TrustedModLoaderInstaller>());
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<LauncherViewModel>();
        services.AddSingleton<ArchiveSelectionService>();
        services.AddSingleton<IArchiveSelectionService>(x => x.GetRequiredService<ArchiveSelectionService>());
        services.AddTransient<SimpleLauncherViewModel>();
        services.AddTransient<MainWindow>();
    }
}
