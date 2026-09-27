using MinecraftManager.Core.Models;
using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Updates;

namespace MinecraftManager.Core.Services;

public interface IInstanceService
{
    Task<IReadOnlyList<MinecraftInstance>> GetAllAsync(CancellationToken cancellationToken);
    Task<MinecraftInstance?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task SaveAsync(MinecraftInstance instance, CancellationToken cancellationToken);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken);
}

public sealed record PlannedUpdate(UpdatePlan Plan, string SourceDisplayName);

public interface IUpdateCoordinator
{
    UpdateStage Stage { get; }
    Task<PlannedUpdate> CheckAsync(Guid instanceId, CancellationToken cancellationToken);
    Task<UpdateResult> InstallAsync(Guid planId, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken);
    void Decline(Guid planId);
}

public sealed record LocalArchiveCandidate(
    string ArchivePath,
    string FileName,
    ValidatedManifest Manifest)
{
    public string PackId => Manifest.Value.PackId;
    public string PackVersion => Manifest.Value.PackVersion;
    public string DisplayName => Manifest.Value.Metadata?.DisplayName ?? Manifest.Value.PackId;
}

public sealed record LocalArchiveDiscoveryFailure(string FileName, string Reason);

public sealed record LocalArchiveDiscoveryResult(
    string UpdatesDirectory,
    IReadOnlyList<LocalArchiveCandidate> Candidates,
    IReadOnlyList<LocalArchiveDiscoveryFailure> Failures,
    bool DirectoryExists,
    bool HasCurrentArchive = false);

public interface IExecutableDirectoryProvider
{
    string BaseDirectory { get; }
}

public interface ILocalArchiveDiscoveryService
{
    Task<LocalArchiveDiscoveryResult> DiscoverAsync(
        string? requiredPackId,
        string? installedManifestSha256,
        CancellationToken cancellationToken);
}
