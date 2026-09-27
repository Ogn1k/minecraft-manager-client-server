using System.Collections.Concurrent;
using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Runtime;
using MinecraftManager.Core.Services;
using MinecraftManager.Core.Updates;

namespace MinecraftManager.Infrastructure.Services;

public sealed class InstanceService(IConfigurationStore store) : IInstanceService
{
    public async Task<IReadOnlyList<MinecraftInstance>> GetAllAsync(CancellationToken ct) => (await store.LoadAsync(ct)).Instances;
    public async Task<MinecraftInstance?> GetAsync(Guid id, CancellationToken ct) => (await store.LoadAsync(ct)).Instances.SingleOrDefault(x => x.Id == id);
    public async Task SaveAsync(MinecraftInstance instance, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(instance.DisplayName)) throw new ArgumentException("Instance name is required.");
        var config = await store.LoadAsync(ct);
        var items = config.Instances.Where(x => x.Id != instance.Id).Append(instance).OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        await store.SaveAsync(config with { Instances = items }, ct);
    }
    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        var config = await store.LoadAsync(ct);
        await store.SaveAsync(config with { Instances = config.Instances.Where(x => x.Id != id).ToArray() }, ct);
    }
}

public sealed class UpdateCoordinator(
    IInstanceService instances,
    IInstanceStateStore states,
    IUpdateSourceFactory sources,
    IUpdatePlanner planner,
    IUpdateExecutor executor,
    IModLoaderInstaller loaderInstaller,
    IModLoaderRuntimeRegistry loaderRuntimes,
    IMinecraftVersionResolver versionResolver,
    IRuntimeManager runtimeManager,
    IJavaRuntimeService java,
    MinecraftManager.Core.Persistence.IApplicationPaths paths,
    MinecraftManager.Infrastructure.Sources.IManagedApiClient managedApi) : IUpdateCoordinator
{
    private readonly ConcurrentDictionary<Guid, Pending> pending = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    public UpdateStage Stage { get; private set; } = UpdateStage.Idle;

    public async Task<PlannedUpdate> CheckAsync(Guid instanceId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Stage = UpdateStage.Checking;
            var instance = await instances.GetAsync(instanceId, ct) ?? throw new InvalidOperationException("Instance was not found.");
            var settings = instance.Pack?.Source ?? throw new InvalidOperationException("The instance has no pack source.");
            if (settings is LocalFolderSourceSettings local && PathsOverlap(instance.Location.GameDirectory, local.FolderPath))
                throw new InvalidOperationException("The local pack source and Minecraft installation must not overlap.");
            if (settings is LocalArchiveSourceSettings archive && PathsOverlap(instance.Location.GameDirectory, archive.ArchivePath))
                throw new InvalidOperationException("The local pack archive and Minecraft installation must not overlap.");
            var source = sources.Create(settings);
            try
            {
                var envelope = await source.GetManifestAsync(new(), ct);
                var manifest = envelope.Manifest ?? throw new InvalidDataException("The source did not return a manifest.");
                Stage = UpdateStage.Planning;
                var state = await states.LoadAsync(instanceId, ct);
                state = state with { LastCheckedAtUtc = DateTimeOffset.UtcNow, AvailablePackVersion = state.InstalledPack?.PackVersion == manifest.Value.PackVersion ? null : manifest.Value.PackVersion };
                await states.SaveAsync(instanceId, state, ct);
                var plan = await planner.CreatePlanAsync(instance, state, manifest, ct);
                pending[plan.PlanId] = new(instance, manifest, settings, source.DisplayName, plan, envelope.DeploymentId);
                Stage = UpdateStage.AwaitingConfirmation;
                if (settings is ManagedServerSourceSettings managed && envelope.DeploymentId is { } deploymentId)
                    await TryReportAsync(managed.ServerProfileId, deploymentId, 1, "awaitingUserConfirmation", null, ct);
                return new(plan, source.DisplayName);
            }
            finally { await source.DisposeAsync(); }
        }
        catch { Stage = ct.IsCancellationRequested ? UpdateStage.Cancelled : UpdateStage.Failed; throw; }
        finally { gate.Release(); }
    }

    public async Task<UpdateResult> InstallAsync(Guid planId, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        if (!pending.TryRemove(planId, out var item)) return new(false, new("plan_missing", "The reviewed plan is no longer available.", false));
        await gate.WaitAsync(ct);
        try
        {
            var state = await states.LoadAsync(item.Instance.Id, ct);
            await using var source = sources.Create(item.Settings);
            var current = await source.GetManifestAsync(new(), ct);
            if (current.Manifest?.CanonicalSha256 != item.Manifest.CanonicalSha256)
                return new(false, new("stale_plan", "The source changed; review the update again.", false));
            var plan = await planner.CreatePlanAsync(item.Instance, state, item.Manifest, ct);
            // Preserve reviewed identity while enforcing identical change sets.
            if (!Equivalent(item.Plan, plan)) return new(false, new("stale_plan", "Local files changed; review the update again.", false));
            await ApplyManifestLoaderAsync(item.Instance, item.Manifest.Value, ct);
            Stage = UpdateStage.Downloading;
            if (item.Settings is ManagedServerSourceSettings managed && item.DeploymentId is { } deploymentId)
                await TryReportAsync(managed.ServerProfileId, deploymentId, 2, "downloading", null, ct);
            var result = await executor.ExecuteAsync(UpdateConfirmation.Confirm(item.Plan), item.Manifest, source, progress, ct);
            if (result.Succeeded)
                result = await PrepareRuntimeAsync(item.Instance.Id, result.TransactionId, progress, ct);
            Stage = result.Succeeded ? UpdateStage.Completed : result.Failure?.Code == "recovery_required" ? UpdateStage.RecoveryRequired : UpdateStage.Failed;
            if (item.Settings is ManagedServerSourceSettings managedResult && item.DeploymentId is { } resultDeploymentId)
                await TryReportAsync(managedResult.ServerProfileId, resultDeploymentId, 3, result.Succeeded ? "succeeded" : "failed", result.Failure?.Code, CancellationToken.None);
            return result;
        }
        finally { gate.Release(); }
    }

    public void Decline(Guid planId) { pending.TryRemove(planId, out _); Stage = UpdateStage.Cancelled; }

    private async Task<UpdateResult> PrepareRuntimeAsync(Guid instanceId, Guid? transactionId, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        try
        {
            var instance = await instances.GetAsync(instanceId, ct) ?? throw new InvalidOperationException("The updated instance was not found.");
            if (!instance.Runtime.IsConfigured) return new(true, null, transactionId);
            progress?.Report(new(UpdateStage.Downloading, null, 0, 0, 0, 0, null, "Preparing Minecraft runtime…"));
            var resolved = await versionResolver.ResolveAsync(instance.Runtime, RuntimePlatform.Current, ct);
            var runtimeProgress = progress is null ? null : new Progress<RuntimeProgress>(value =>
                progress.Report(new(UpdateStage.Downloading, value.Artifact, (int)Math.Min(value.CompletedBytes, int.MaxValue),
                    (int)Math.Min(value.TotalBytes ?? 0, int.MaxValue), value.CompletedBytes, value.TotalBytes ?? 0,
                    value.TotalBytes is > 0 ? value.CompletedBytes * 100d / value.TotalBytes : null, "Downloading Minecraft runtime…")));
            var repaired = await runtimeManager.RepairAsync(resolved, runtimeProgress, ct);
            if (!repaired.Succeeded)
                return new(false, new(repaired.ErrorCode ?? "runtime_repair_failed", repaired.Message ?? "Minecraft runtime preparation failed.", true), transactionId);

            var requirement = instance.Runtime.Java ?? new JavaRuntimeRequirement(resolved.RequiredJavaMajor);
            var selectedJava = await java.SelectAsync(instance.Launch.Java, requirement, RuntimePlatform.Current, ct);
            if (selectedJava is null && instance.Launch.Java.Mode == JavaSelectionMode.Automatic)
                selectedJava = await java.ProvisionAsync(requirement, RuntimePlatform.Current, ct);
            if (selectedJava is null)
                return new(false, new("java_missing", $"Compatible Java {requirement.MajorVersion} could not be prepared.", true), transactionId);

            var validation = await runtimeManager.ValidateAsync(resolved, ct);
            if (!validation.IsReady)
                return new(false, new("runtime_validation_failed", "Minecraft runtime is still incomplete after repair.", true), transactionId);
            return new(true, null, transactionId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, new("runtime_prepare_failed", ex.Message, true), transactionId); }
    }

    private async Task ApplyManifestLoaderAsync(MinecraftInstance instance, PackManifest manifest, CancellationToken ct)
    {
        if (manifest.ModLoader is not { } requirement) return;
        if (!Enum.TryParse<ModLoaderType>(requirement.Type, ignoreCase: true, out var type)) throw new InvalidOperationException("The pack requires an unsupported mod loader.");
        var minecraftVersion = instance.Runtime.MinecraftVersion ?? manifest.MinecraftVersion
            ?? throw new InvalidOperationException("The pack requires a mod loader but no Minecraft version is configured.");
        var loader = new ModLoaderConfiguration(type, requirement.Version);
        var descriptor = loaderRuntimes.Resolve(minecraftVersion, loader);
        var metadataPath = Path.Combine(paths.SharedVersionsDirectory, descriptor.ResolvedVersionId, descriptor.ResolvedVersionId + ".json");
        var configurationMatches = instance.Runtime.ModLoader is { } existing && existing.Type == type && existing.Version == requirement.Version;
        if (configurationMatches && File.Exists(metadataPath)) return;
        var result = await loaderInstaller.InstallAsync(new(minecraftVersion, loader, paths.SharedVersionsDirectory), ct);
        if (!result.Succeeded) throw new InvalidOperationException($"The pack requires {type} {requirement.Version}, but its installation failed: {result.Message}");
        await instances.SaveAsync(instance with { Runtime = instance.Runtime with { MinecraftVersion = minecraftVersion, ModLoader = loader } }, ct);
    }

    private static bool Equivalent(UpdatePlan a, UpdatePlan b) =>
        a.ManifestSha256 == b.ManifestSha256 &&
        a.FilesToAdd.Select(x => (x.Path, x.Sha256)).SequenceEqual(b.FilesToAdd.Select(x => (x.Path, x.Sha256))) &&
        a.FilesToReplace.Select(x => (x.Path, x.ExistingSha256)).SequenceEqual(b.FilesToReplace.Select(x => (x.Path, x.ExistingSha256))) &&
        a.FilesToDelete.Select(x => (x.Path, x.ExistingSha256)).SequenceEqual(b.FilesToDelete.Select(x => (x.Path, x.ExistingSha256)));
    private static bool PathsOverlap(string left, string right)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return a.Equals(b, comparison) || a.StartsWith(b + Path.DirectorySeparatorChar, comparison) || b.StartsWith(a + Path.DirectorySeparatorChar, comparison);
    }
    private async Task TryReportAsync(Guid profileId, Guid deploymentId, long sequence, string status, string? error, CancellationToken ct)
    {
        try { await managedApi.ReportStatusAsync(profileId, deploymentId, sequence, status, error, ct); }
        catch { /* Status reporting is best effort; authoritative state is refreshed later. */ }
    }
    private sealed record Pending(MinecraftInstance Instance, ValidatedManifest Manifest, UpdateSourceSettings Settings, string DisplayName, UpdatePlan Plan, Guid? DeploymentId);
}
