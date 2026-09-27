using Microsoft.Extensions.DependencyInjection;
using MinecraftManager.App.ViewModels;
using MinecraftManager.Core.Launching;
using MinecraftManager.Core.Profiles;

namespace MinecraftManager.App.Tests;

public sealed class CompositionTests
{
    [Fact]
    public void ApplicationCompositionBuildsAndResolvesLauncher()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.NotNull(provider.GetRequiredService<LauncherViewModel>());
        Assert.NotNull(provider.GetRequiredService<IGameLaunchService>());
        Assert.NotNull(provider.GetRequiredService<IOfflineProfileService>());
    }
}
