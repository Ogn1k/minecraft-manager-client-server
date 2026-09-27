using System.Collections.Concurrent;
using MinecraftManager.Core.Launching;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Profiles;
using MinecraftManager.Core.Runtime;
using MinecraftManager.Core.Services;

namespace MinecraftManager.Infrastructure.Launching;

public sealed class PackLaunchStatusService(IInstanceStateStore states) : IPackLaunchStatusService
{
    public async Task<PackLaunchStatus> GetAsync(MinecraftInstance instance, CancellationToken cancellationToken)
    {
        if (instance.Pack is null) return new(PackLaunchState.None);
        var state = await states.LoadAsync(instance.Id, cancellationToken);
        if (state.InstalledPack is null) return new(PackLaunchState.NotInstalled, AvailableVersion: state.AvailablePackVersion);
        if (state.PackIntegrityFailed) return new(PackLaunchState.Corrupt, state.InstalledPack.PackVersion, state.AvailablePackVersion);
        if (state.AvailablePackVersion is not null && state.AvailablePackVersion != state.InstalledPack.PackVersion)
            return new(instance.Pack.UpdatePolicy == PackUpdatePolicy.RequiredBeforeLaunch ? PackLaunchState.RequiredUpdate : PackLaunchState.UpdateAvailable,
                state.InstalledPack.PackVersion, state.AvailablePackVersion);
        return new(PackLaunchState.Current, state.InstalledPack.PackVersion);
    }
}

public sealed class LaunchReadinessService(
    IOfflineProfileService profiles,
    IPackLaunchStatusService packs,
    IMinecraftVersionResolver versions,
    IRuntimeManager runtime,
    IJavaRuntimeService java,
    IGameProcessMonitor processes) : ILaunchReadinessService
{
    public async Task<LaunchReadiness> CheckAsync(MinecraftInstance instance, Guid? profileId, CancellationToken cancellationToken)
    {
        var issues = new List<LaunchIssue>();
        if (!Directory.Exists(instance.Location.GameDirectory)) issues.Add(Block(LaunchIssueCode.MissingGameDirectory, "Game directory is missing.", LaunchRemediation.OpenInstance));
        if (!instance.Runtime.IsConfigured) issues.Add(Block(LaunchIssueCode.RuntimeNotConfigured, "Minecraft version is not configured.", LaunchRemediation.ConfigureRuntime));
        if (profileId is null || await profiles.GetLaunchIdentityAsync(profileId.Value, cancellationToken) is null) issues.Add(Block(LaunchIssueCode.OfflineProfileRequired, "Choose a valid offline profile.", LaunchRemediation.ManageProfiles));
        if (processes.IsRunning(instance.Id)) issues.Add(Block(LaunchIssueCode.AlreadyRunning, "Minecraft is already running for this instance.", LaunchRemediation.None));
        var pack = await packs.GetAsync(instance, cancellationToken);
        if (pack.State == PackLaunchState.NotInstalled) issues.Add(Block(LaunchIssueCode.PackNotInstalled, "The managed pack is not installed.", LaunchRemediation.ReviewUpdate));
        if (pack.State == PackLaunchState.RequiredUpdate) issues.Add(Block(LaunchIssueCode.RequiredPackUpdate, "A required pack update must be reviewed.", LaunchRemediation.ReviewUpdate));
        if (pack.State == PackLaunchState.Corrupt) issues.Add(Block(LaunchIssueCode.PackCorrupt, "Managed pack files are corrupt.", LaunchRemediation.ReviewUpdate));
        if (pack.State == PackLaunchState.UpdateAvailable) issues.Add(new(LaunchIssueCode.PackUpdateAvailable, LaunchIssueSeverity.Warning, "A pack update is available.", LaunchRemediation.ReviewUpdate));
        if (instance.Runtime.IsConfigured)
        {
            try
            {
                var resolved = await versions.ResolveAsync(instance.Runtime, RuntimePlatform.Current, cancellationToken);
                var state = await runtime.ValidateAsync(resolved, cancellationToken);
                if (!state.IsReady) issues.Add(Block(LaunchIssueCode.MissingLibraries, "Runtime files are missing or corrupt.", LaunchRemediation.RepairRuntime));
                var requirement = instance.Runtime.Java ?? new JavaRuntimeRequirement(resolved.RequiredJavaMajor);
                if (await java.SelectAsync(instance.Launch.Java, requirement, RuntimePlatform.Current, cancellationToken) is null)
                    issues.Add(Block(LaunchIssueCode.MissingJava, $"Compatible Java {requirement.MajorVersion} was not found.", LaunchRemediation.SelectJava));
            }
            catch (FileNotFoundException) { issues.Add(Block(LaunchIssueCode.MissingVersionMetadata, "Minecraft version metadata is missing.", LaunchRemediation.RepairRuntime)); }
            catch (InvalidDataException ex) { issues.Add(Block(LaunchIssueCode.InvalidModLoader, ex.Message, LaunchRemediation.RepairRuntime)); }
        }
        return LaunchReadiness.From(issues);
    }
    private static LaunchIssue Block(LaunchIssueCode code, string message, LaunchRemediation remediation) => new(code, LaunchIssueSeverity.Blocking, message, remediation);
}

public sealed class GameLaunchService(
    IInstanceService instances,
    IOfflineProfileService profiles,
    ILaunchReadinessService readiness,
    IMinecraftVersionResolver versions,
    IRuntimeManager runtime,
    IJavaRuntimeService java,
    ILaunchPlanner planner,
    ILaunchExecutor executor,
    IApplicationPaths paths) : IGameLaunchService
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    public async Task<LaunchReadiness> CheckReadinessAsync(Guid instanceId, Guid? profileId, CancellationToken cancellationToken)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken) ?? throw new KeyNotFoundException("Instance was not found.");
        return await readiness.CheckAsync(instance, profileId, cancellationToken);
    }
    public async Task<LaunchResult> LaunchAsync(Guid instanceId, Guid profileId, CancellationToken cancellationToken)
    {
        var gate = gates.GetOrAdd(instanceId, _ => new(1, 1)); await gate.WaitAsync(cancellationToken);
        try
        {
            var instance = await instances.GetAsync(instanceId, cancellationToken) ?? throw new KeyNotFoundException("Instance was not found.");
            var state = await readiness.CheckAsync(instance, profileId, cancellationToken);
            if (!state.CanLaunch) return new(false, null, state, new("not_ready", "The instance is not ready to launch.", true));
            var identity = await profiles.GetLaunchIdentityAsync(profileId, cancellationToken) ?? throw new InvalidOperationException("Offline profile is missing.");
            var resolved = await versions.ResolveAsync(instance.Runtime, RuntimePlatform.Current, cancellationToken);
            var requirement = instance.Runtime.Java ?? new JavaRuntimeRequirement(resolved.RequiredJavaMajor);
            var selectedJava = await java.SelectAsync(instance.Launch.Java, requirement, RuntimePlatform.Current, cancellationToken) ?? throw new InvalidOperationException("Compatible Java is missing.");
            var launchId = Guid.NewGuid();
            var natives = await runtime.PrepareNativesAsync(resolved, launchId, cancellationToken);
            var plan = planner.CreatePlan(instance, resolved, selectedJava, identity, paths.SharedAssetsDirectory, natives) with { LaunchId = launchId };
            var running = await executor.LaunchAsync(plan, cancellationToken);
            return new(true, new(instanceId, running.ProcessId, GameProcessState.Running, running.StartedAtUtc), state, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(false, null, new(false, []), new("launch_failed", ex.Message, true)); }
        finally { gate.Release(); }
    }
}
