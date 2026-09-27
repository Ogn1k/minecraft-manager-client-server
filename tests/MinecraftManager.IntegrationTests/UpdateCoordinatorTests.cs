using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Runtime;
using MinecraftManager.Core.Services;
using MinecraftManager.Core.Updates;
using MinecraftManager.Infrastructure.Persistence;
using MinecraftManager.Infrastructure.Services;
using MinecraftManager.Infrastructure.Sources;

namespace MinecraftManager.IntegrationTests;

public sealed class UpdateCoordinatorTests
{
    [Fact]
    public async Task MatchingLoaderConfigurationStillInstallsMissingMetadata()
    {
        using var app = new TemporaryDirectory();
        using var game = new TemporaryDirectory();
        var paths = new ApplicationPaths(app.Path);
        var loader = new ModLoaderConfiguration(ModLoaderType.NeoForge, "21.1.250");
        var instance = new MinecraftInstance
        {
            Id = Guid.NewGuid(), DisplayName = "Linux",
            Location = new(game.Path, InstanceOwnership.ApplicationOwned),
            Runtime = new("1.21.1", loader),
            Pack = new(new LocalFolderSourceSettings(game.Path + "-source")),
            Launch = LaunchConfiguration.Default
        };
        var manifest = new ValidatedManifest(new PackManifest
        {
            SchemaVersion = 2, PackId = "pack", PackVersion = "1", MinecraftVersion = "1.21.1",
            ModLoader = new("NeoForge", "21.1.250"), ManagedPaths = [], Files = []
        }, "manifest-hash");
        var instances = new MemoryInstances(instance);
        var installer = new RecordingLoaderInstaller();
        var coordinator = new UpdateCoordinator(
            instances, new MemoryStates(), new SourceFactory(manifest), new Planner(), new Executor(), installer,
            new ModLoaderRuntimeRegistry([new ConventionModLoaderRuntimeProvider(ModLoaderType.NeoForge)]),
            new VersionResolver(), new RuntimeManager(), new JavaRuntimeService(), paths, new ManagedApi());

        var planned = await coordinator.CheckAsync(instance.Id, default);
        var result = await coordinator.InstallAsync(planned.Plan.PlanId, null, default);

        Assert.True(result.Succeeded, result.Failure?.SafeMessage);
        Assert.Equal(1, installer.Calls);
    }

    private sealed class MemoryInstances(MinecraftInstance instance) : IInstanceService
    {
        private MinecraftInstance value = instance;
        public Task<IReadOnlyList<MinecraftInstance>> GetAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MinecraftInstance>>([value]);
        public Task<MinecraftInstance?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult<MinecraftInstance?>(value.Id == id ? value : null);
        public Task SaveAsync(MinecraftInstance updated, CancellationToken ct) { value = updated; return Task.CompletedTask; }
        public Task RemoveAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class MemoryStates : IInstanceStateStore
    {
        public Task<InstanceState> LoadAsync(Guid id, CancellationToken ct) => Task.FromResult(new InstanceState(null, []));
        public Task SaveAsync(Guid id, InstanceState state, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class SourceFactory(ValidatedManifest manifest) : IUpdateSourceFactory
    {
        public IUpdateSource Create(UpdateSourceSettings settings) => new Source(manifest);
    }

    private sealed class Source(ValidatedManifest manifest) : IUpdateSource
    {
        public string DisplayName => "test";
        public string Identity => "test";
        public Task<ManifestEnvelope> GetManifestAsync(ManifestRequest request, CancellationToken ct) => Task.FromResult(new ManifestEnvelope(manifest));
        public Task<Stream> OpenFileAsync(ManifestFile file, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Planner : IUpdatePlanner
    {
        public Task<UpdatePlan> CreatePlanAsync(MinecraftInstance instance, InstanceState state, ValidatedManifest manifest, CancellationToken ct) =>
            Task.FromResult(new UpdatePlan(Guid.NewGuid(), instance.Id, manifest.Value.PackId, manifest.Value.PackVersion,
                manifest.CanonicalSha256, [], [], [], [], [], 0, 0, DateTimeOffset.UtcNow));
    }

    private sealed class Executor : IUpdateExecutor
    {
        public Task<UpdateResult> ExecuteAsync(ConfirmedUpdatePlan plan, ValidatedManifest manifest, IUpdateSource source,
            IProgress<UpdateProgress>? progress, CancellationToken ct) => Task.FromResult(new UpdateResult(true, null, Guid.NewGuid()));
    }

    private sealed class RecordingLoaderInstaller : IModLoaderInstaller
    {
        public int Calls { get; private set; }
        public Task<LoaderInstallResult> InstallAsync(LoaderInstallRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LoaderInstallResult(true, $"{request.MinecraftVersion}-neoforge-{request.Loader.Version}"));
        }
    }

    private sealed class VersionResolver : IMinecraftVersionResolver
    {
        public Task<ResolvedMinecraftVersion> ResolveAsync(RuntimeConfiguration configuration, RuntimePlatform platform, CancellationToken ct) =>
            Task.FromResult(new ResolvedMinecraftVersion("test", "Main", [], null, [], [], [], 21));
    }

    private sealed class RuntimeManager : IRuntimeManager
    {
        public Task<RuntimeValidationResult> ValidateAsync(ResolvedMinecraftVersion version, CancellationToken ct) => Task.FromResult(new RuntimeValidationResult(true, [], []));
        public Task<RuntimeRepairResult> RepairAsync(ResolvedMinecraftVersion version, IProgress<RuntimeProgress>? progress, CancellationToken ct) => Task.FromResult(new RuntimeRepairResult(true));
        public Task<string> PrepareNativesAsync(ResolvedMinecraftVersion version, Guid launchId, CancellationToken ct) => Task.FromResult(string.Empty);
    }

    private sealed class JavaRuntimeService : IJavaRuntimeService
    {
        private static readonly JavaRuntime Runtime = new("java", 21, "test", RuntimePlatform.Current.Architecture, JavaRuntimeOrigin.Managed);
        public Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<JavaRuntime>>([Runtime]);
        public Task<JavaRuntimeValidationResult> ValidateAsync(JavaRuntime runtime, JavaRuntimeRequirement requirement, RuntimePlatform platform, CancellationToken ct) => Task.FromResult(new JavaRuntimeValidationResult(true));
        public Task<JavaRuntime?> SelectAsync(JavaSelection selection, JavaRuntimeRequirement requirement, RuntimePlatform platform, CancellationToken ct) => Task.FromResult<JavaRuntime?>(Runtime);
        public Task<JavaRuntime?> ProvisionAsync(JavaRuntimeRequirement requirement, RuntimePlatform platform, CancellationToken ct) => Task.FromResult<JavaRuntime?>(Runtime);
    }

    private sealed class ManagedApi : IManagedApiClient
    {
        public Task<ManifestEnvelope> GetManifestAsync(ManagedServerSourceSettings settings, ManifestRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenFileAsync(ManagedServerSourceSettings settings, ManifestFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task ReportStatusAsync(Guid serverProfileId, Guid deploymentId, long sequence, string status, string? errorCode, CancellationToken ct) => Task.CompletedTask;
    }
}
