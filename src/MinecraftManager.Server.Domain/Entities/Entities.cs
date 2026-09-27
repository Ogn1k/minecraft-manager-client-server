using MinecraftManager.Server.Domain.Common;

namespace MinecraftManager.Server.Domain.Entities;

public enum PackVersionState { Draft, Published, Superseded, Archived }
public enum BlobState { Temporary, Verified, Failed, PendingDeletion }
public enum DeploymentState { Draft, Active, Cancelled, Completed }
public enum DeploymentTargetStatus { Pending, Notified, Seen, AwaitingUserConfirmation, Downloading, Applying, Succeeded, Failed, Declined, Cancelled }

public sealed class Pack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Slug { get; set; }
    public required string NormalizedSlug { get; set; }
    public required string DisplayName { get; set; }
    public string? Description { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<PackVersion> Versions { get; set; } = [];
}

public sealed class PackVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PackId { get; set; }
    public Pack? Pack { get; set; }
    public required string Version { get; set; }
    public required string NormalizedVersion { get; set; }
    public string? MinimumClientVersion { get; set; }
    public string? MinecraftVersion { get; set; }
    public string? LoaderType { get; set; }
    public string? LoaderVersion { get; set; }
    public PackVersionState State { get; private set; } = PackVersionState.Draft;
    public byte[]? ManifestJson { get; private set; }
    public string? ManifestSha256 { get; private set; }
    public DateTimeOffset? PublishedAtUtc { get; private set; }
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();
    public List<PackFile> Files { get; set; } = [];
    public List<ManagedPath> ManagedPaths { get; set; } = [];

    public void Publish(byte[] manifest, Sha256Digest digest, DateTimeOffset now)
    {
        if (State != PackVersionState.Draft) throw new DomainRuleException("version_immutable", "Only a draft can be published.");
        ManifestJson = manifest.ToArray(); ManifestSha256 = digest.Value; PublishedAtUtc = now; State = PackVersionState.Published;
    }
    public void EnsureDraft() { if (State != PackVersionState.Draft) throw new DomainRuleException("version_immutable", "Published pack versions are immutable."); }
    public void Supersede() { if (State != PackVersionState.Published) throw new DomainRuleException("invalid_state", "Only published versions can be superseded."); State = PackVersionState.Superseded; }
    public void Archive() { if (State == PackVersionState.Draft) throw new DomainRuleException("invalid_state", "Discard drafts instead of archiving them."); State = PackVersionState.Archived; }
}

public sealed class PackFile { public Guid Id { get; set; } = Guid.NewGuid(); public Guid PackVersionId { get; set; } public required string Path { get; set; } public required string NormalizedPath { get; set; } public Guid BlobId { get; set; } public FileBlob? Blob { get; set; } public string? ContentType { get; set; } }
public sealed class ManagedPath { public Guid Id { get; set; } = Guid.NewGuid(); public Guid PackVersionId { get; set; } public required string Path { get; set; } public required string NormalizedPath { get; set; } }
public sealed class FileBlob { public Guid Id { get; set; } = Guid.NewGuid(); public required string Sha256 { get; set; } public long Size { get; set; } public required string StorageKey { get; set; } public BlobState State { get; set; } public string? ContentType { get; set; } public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset? VerifiedAtUtc { get; set; } }
public sealed class Machine { public Guid Id { get; set; } = Guid.NewGuid(); public Guid OwnerUserId { get; set; } public required string DisplayName { get; set; } public string? Os { get; set; } public string? Architecture { get; set; } public string? ClientVersion { get; set; } public DateTimeOffset? LastSeenAtUtc { get; set; } public DateTimeOffset? RevokedAtUtc { get; set; } public long AuthorizationVersion { get; set; } }
public sealed class MinecraftInstance { public Guid Id { get; set; } = Guid.NewGuid(); public Guid MachineId { get; set; } public Guid ClientInstanceId { get; set; } public required string DisplayName { get; set; } public string? InstalledPackVersion { get; set; } public DateTimeOffset LastSeenAtUtc { get; set; } }
public sealed class DeviceCredential { public Guid Id { get; set; } = Guid.NewGuid(); public Guid MachineId { get; set; } public required string SecretHash { get; set; } public Guid FamilyId { get; set; } public DateTimeOffset ExpiresAtUtc { get; set; } public DateTimeOffset? RevokedAtUtc { get; set; } public Guid? ReplacedById { get; set; } public Guid ConcurrencyToken { get; set; } = Guid.NewGuid(); }
public sealed class RegistrationCode { public Guid Id { get; set; } = Guid.NewGuid(); public Guid OwnerUserId { get; set; } public required string CodeHash { get; set; } public DateTimeOffset ExpiresAtUtc { get; set; } public DateTimeOffset? ConsumedAtUtc { get; set; } public Guid ConcurrencyToken { get; set; } = Guid.NewGuid(); }
public sealed class ClientSession { public Guid Id { get; set; } = Guid.NewGuid(); public Guid MachineId { get; set; } public DateTimeOffset IssuedAtUtc { get; set; } public DateTimeOffset LastSeenAtUtc { get; set; } public DateTimeOffset ExpiresAtUtc { get; set; } }
public sealed class ApiKey { public Guid Id { get; set; } = Guid.NewGuid(); public Guid CreatedByUserId { get; set; } public required string PublicId { get; set; } public required string SecretHash { get; set; } public required string Scopes { get; set; } public DateTimeOffset? ExpiresAtUtc { get; set; } public DateTimeOffset? RevokedAtUtc { get; set; } public DateTimeOffset? LastUsedAtUtc { get; set; } }
public sealed class PackAssignment { public Guid Id { get; set; } = Guid.NewGuid(); public Guid InstanceId { get; set; } public Guid PackId { get; set; } public Guid PackVersionId { get; set; } public long Revision { get; set; } public bool IsActive { get; set; } = true; public DateTimeOffset AssignedAtUtc { get; set; } }
public sealed class Deployment { public Guid Id { get; set; } = Guid.NewGuid(); public Guid PackVersionId { get; set; } public Guid CreatedByUserId { get; set; } public DeploymentState State { get; set; } = DeploymentState.Active; public string? Note { get; set; } public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset? CancelledAtUtc { get; set; } public List<DeploymentTarget> Targets { get; set; } = []; }
public sealed class DeploymentTarget
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid DeploymentId { get; set; } public Guid InstanceId { get; set; } public DeploymentTargetStatus Status { get; private set; } = DeploymentTargetStatus.Pending; public long LastSequence { get; private set; } = -1; public string? ErrorCode { get; private set; } public DateTimeOffset UpdatedAtUtc { get; private set; } = DateTimeOffset.UtcNow;
    public bool Report(long sequence, DeploymentTargetStatus next, string? errorCode, DateTimeOffset now)
    {
        if (sequence <= LastSequence) return false;
        if (!Allowed(Status, next)) throw new DomainRuleException("invalid_deployment_transition", $"Cannot transition from {Status} to {next}.");
        LastSequence = sequence; Status = next; ErrorCode = errorCode; UpdatedAtUtc = now; return true;
    }
    private static bool Allowed(DeploymentTargetStatus from, DeploymentTargetStatus to) => from switch
    {
        DeploymentTargetStatus.Pending => to is DeploymentTargetStatus.Notified or DeploymentTargetStatus.Seen or DeploymentTargetStatus.AwaitingUserConfirmation or DeploymentTargetStatus.Cancelled,
        DeploymentTargetStatus.Notified => to is DeploymentTargetStatus.Seen or DeploymentTargetStatus.AwaitingUserConfirmation or DeploymentTargetStatus.Cancelled,
        DeploymentTargetStatus.Seen => to is DeploymentTargetStatus.AwaitingUserConfirmation or DeploymentTargetStatus.Cancelled,
        DeploymentTargetStatus.AwaitingUserConfirmation => to is DeploymentTargetStatus.Downloading or DeploymentTargetStatus.Declined or DeploymentTargetStatus.Cancelled,
        DeploymentTargetStatus.Downloading => to is DeploymentTargetStatus.Applying or DeploymentTargetStatus.Failed or DeploymentTargetStatus.Cancelled,
        DeploymentTargetStatus.Applying => to is DeploymentTargetStatus.Succeeded or DeploymentTargetStatus.Failed,
        _ => false
    };
}
public sealed class AuditEvent { public Guid Id { get; set; } = Guid.NewGuid(); public required string ActorType { get; set; } public Guid? ActorId { get; set; } public required string Action { get; set; } public required string TargetType { get; set; } public Guid? TargetId { get; set; } public string? DetailsJson { get; set; } public string? CorrelationId { get; set; } public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow; }
public sealed class OutboxMessage { public Guid Id { get; set; } = Guid.NewGuid(); public required string Type { get; set; } public required string PayloadJson { get; set; } public required string GroupName { get; set; } public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset? PublishedAtUtc { get; set; } public int Attempts { get; set; } public DateTimeOffset? NextAttemptAtUtc { get; set; } public string? LastError { get; set; } }
public sealed class IdempotencyRecord { public Guid Id { get; set; } = Guid.NewGuid(); public required string Subject { get; set; } public required string Key { get; set; } public required string RequestHash { get; set; } public string? ResponseJson { get; set; } public int StatusCode { get; set; } public DateTimeOffset ExpiresAtUtc { get; set; } }
public sealed class ImportJob { public Guid Id { get; set; } = Guid.NewGuid(); public Guid PackVersionId { get; set; } public required string Kind { get; set; } public required string State { get; set; } public string? ParametersJson { get; set; } public string? ErrorCode { get; set; } public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow; }
public sealed class UploadRecord { public Guid Id { get; set; } = Guid.NewGuid(); public required string TemporaryKey { get; set; } public string? DeclaredSha256 { get; set; } public long? DeclaredSize { get; set; } public required string State { get; set; } public Guid? BlobId { get; set; } public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset ExpiresAtUtc { get; set; } }
