using MinecraftManager.Core.Models;

namespace MinecraftManager.Core.Runtime;

public sealed record LoaderRuntimeDescriptor(
    ModLoaderType Type,
    string LoaderVersion,
    string MinecraftVersion,
    string ResolvedVersionId);

public sealed record LoaderInstallRequest(string MinecraftVersion, ModLoaderConfiguration Loader, string VersionsDirectory);
public sealed record LoaderInstallResult(bool Succeeded, string? ResolvedVersionId = null, string? ErrorCode = null, string? Message = null);

public interface IModLoaderInstaller
{
    Task<LoaderInstallResult> InstallAsync(LoaderInstallRequest request, CancellationToken cancellationToken);
}

public interface IModLoaderRuntimeProvider
{
    ModLoaderType Type { get; }
    LoaderRuntimeDescriptor Resolve(string minecraftVersion, ModLoaderConfiguration loader);
}

public sealed class ConventionModLoaderRuntimeProvider(ModLoaderType type) : IModLoaderRuntimeProvider
{
    public ModLoaderType Type { get; } = type;
    public LoaderRuntimeDescriptor Resolve(string minecraftVersion, ModLoaderConfiguration loader)
    {
        if (loader.Type != Type || string.IsNullOrWhiteSpace(loader.Version)) throw new InvalidOperationException("Loader configuration is invalid.");
        MinecraftVersionResolver.ValidateVersionId(minecraftVersion);
        MinecraftVersionResolver.ValidateVersionId(loader.Version);
        var token = Type switch { ModLoaderType.Fabric => "fabric", ModLoaderType.NeoForge => "neoforge", ModLoaderType.Forge => "forge", ModLoaderType.Quilt => "quilt", _ => throw new NotSupportedException() };
        return new(Type, loader.Version, minecraftVersion, $"{minecraftVersion}-{token}-{loader.Version}");
    }
}

public interface IModLoaderRuntimeRegistry
{
    LoaderRuntimeDescriptor Resolve(string minecraftVersion, ModLoaderConfiguration loader);
}

public sealed class ModLoaderRuntimeRegistry(IEnumerable<IModLoaderRuntimeProvider> providers) : IModLoaderRuntimeRegistry
{
    private readonly IReadOnlyDictionary<ModLoaderType, IModLoaderRuntimeProvider> values = providers.ToDictionary(x => x.Type);
    public LoaderRuntimeDescriptor Resolve(string minecraftVersion, ModLoaderConfiguration loader) =>
        values.TryGetValue(loader.Type, out var provider) ? provider.Resolve(minecraftVersion, loader) : throw new NotSupportedException($"{loader.Type} is not supported.");
}

public sealed class ModLoaderAwareVersionResolver(MinecraftVersionResolver vanilla, IModLoaderRuntimeRegistry loaders) : IMinecraftVersionResolver
{
    public Task<ResolvedMinecraftVersion> ResolveAsync(RuntimeConfiguration configuration, RuntimePlatform platform, CancellationToken cancellationToken)
    {
        if (configuration.ModLoader is null) return vanilla.ResolveAsync(configuration, platform, cancellationToken);
        if (string.IsNullOrWhiteSpace(configuration.MinecraftVersion)) throw new InvalidOperationException("Minecraft version is not configured.");
        var descriptor = loaders.Resolve(configuration.MinecraftVersion, configuration.ModLoader);
        return vanilla.ResolveAsync(configuration with { MinecraftVersion = descriptor.ResolvedVersionId, ModLoader = null }, platform, cancellationToken);
    }
}
