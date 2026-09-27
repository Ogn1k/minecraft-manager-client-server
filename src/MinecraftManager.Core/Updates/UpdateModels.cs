using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Models;

namespace MinecraftManager.Core.Updates;

public enum UpdateStage { Idle, Checking, Planning, AwaitingConfirmation, Downloading, Verifying, BackingUp, Applying, Validating, RollingBack, Completed, Failed, Cancelled, RecoveryRequired }
public enum FileChangeKind { Add, Replace, Delete }

public sealed record PlannedFile(string Path, string Sha256, long Size);
public sealed record PlannedReplacement(string Path, string Sha256, long Size, string ExistingSha256);
public sealed record PlannedDeletion(string Path, string ExistingSha256, long Size);
public sealed record PlanWarning(string Code, string Message, bool IsBlocking = false);

public sealed record UpdatePlan(
    Guid PlanId,
    Guid InstanceId,
    string PackId,
    string TargetVersion,
    string ManifestSha256,
    IReadOnlyList<PlannedFile> FilesToAdd,
    IReadOnlyList<PlannedReplacement> FilesToReplace,
    IReadOnlyList<PlannedDeletion> FilesToDelete,
    IReadOnlyList<string> UnchangedFiles,
    IReadOnlyList<PlanWarning> Warnings,
    long TotalDownloadSize,
    long EstimatedBackupSize,
    DateTimeOffset CreatedAtUtc);

public sealed record ConfirmedUpdatePlan(UpdatePlan Plan, string ConfirmationDigest, DateTimeOffset ConfirmedAtUtc);
public sealed record UpdateProgress(UpdateStage Stage, string? CurrentRelativePath, int CompletedFiles, int TotalFiles, long ProcessedBytes, long TotalBytes, double? Percentage, string Message);
public sealed record UpdateResult(bool Succeeded, UpdateFailure? Failure, Guid? TransactionId = null);
public sealed record UpdateFailure(string Code, string SafeMessage, bool IsRetryable, string? RelativePath = null);

public sealed record ManifestRequest(string? KnownETag = null, DateTimeOffset? IfModifiedSince = null);
public sealed record ManifestEnvelope(ValidatedManifest? Manifest, string? ETag = null, DateTimeOffset? LastModified = null, bool NotModified = false, Guid? DeploymentId = null);

public interface IUpdateSource : IAsyncDisposable
{
    string DisplayName { get; }
    string Identity { get; }
    Task<ManifestEnvelope> GetManifestAsync(ManifestRequest request, CancellationToken cancellationToken);
    Task<Stream> OpenFileAsync(ManifestFile file, CancellationToken cancellationToken);
}

public interface IUpdateSourceFactory
{
    IUpdateSource Create(UpdateSourceSettings settings);
}
