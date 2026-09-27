using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftManager.Server.Contracts.V1;

public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record ManifestV1(
    int SchemaVersion,
    string PackId,
    string PackVersion,
    string? MinimumClientVersion,
    IReadOnlyList<string> ManagedPaths,
    IReadOnlyList<ManifestFileV1> Files,
    ManifestMetadataV1? Metadata,
    string? MinecraftVersion = null,
    ModLoaderRequirementV1? ModLoader = null);

public sealed record ModLoaderRequirementV1(string Type, string Version);

public sealed record ManifestFileV1(
    Guid Id,
    string Path,
    long Size,
    string Sha256,
    string? ContentType,
    DownloadReferenceV1 Download,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record DownloadReferenceV1(string Type, Guid FileId);
public sealed record ManifestMetadataV1(string? DisplayName, string? ReleaseNotes);

public sealed record ProblemV1(string Code, string Title, int Status, string TraceId, string? Detail = null);
public sealed record CompleteRegistrationRequest(string RegistrationCode, string DisplayName);
public sealed record RegistrationResponse(Guid MachineId, string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc, string RefreshCredential);
public sealed record RefreshTokenRequest(string RefreshCredential);
public sealed record UpsertInstanceRequest(string DisplayName, string Os, string Architecture, string ClientVersion, string? InstalledPackVersion);
public sealed record InstanceResponse(Guid Id, Guid MachineId, Guid ClientInstanceId, string DisplayName, string? InstalledPackVersion);
public sealed record HeartbeatRequest(string ClientVersion, IReadOnlyList<Guid> ClientInstanceIds);
public sealed record AssignmentResponse(Guid InstanceId, Guid PackId, Guid PackVersionId, string PackVersion, long Revision);
public sealed record CreateDeploymentRequest(Guid PackVersionId, IReadOnlyList<Guid> TargetInstanceIds, string? Note);
public sealed record DeploymentStatusRequest(Guid ClientInstanceId, long Sequence, string Status, DateTimeOffset ClientTimestampUtc, ClientErrorV1? Error);
public sealed record ClientErrorV1(string Code, string? SafeMessage);
public sealed record DownloadTicketResponse(Uri Url, DateTimeOffset ExpiresAtUtc, long Size, string Sha256);

/// <summary>Declarative launcher policy only. Executable paths and command-line fragments are intentionally absent.</summary>
public sealed record ClientRuntimePolicyV1(
    int SchemaVersion,
    string MinecraftVersion,
    string? LoaderType,
    string? LoaderVersion,
    string? RequiredPackVersion,
    string UpdatePolicy,
    string? MinimumClientVersion,
    int? RecommendedJavaMajor,
    int? RecommendedMemoryMb);

public static class ClientRuntimePolicyValidator
{
    private static readonly HashSet<string> Loaders = new(StringComparer.OrdinalIgnoreCase) { "fabric", "neoforge", "forge", "quilt" };
    public static IReadOnlyList<string> Validate(ClientRuntimePolicyV1 policy)
    {
        var errors = new List<string>();
        if (policy.SchemaVersion != 1) errors.Add("Unsupported runtime policy schema.");
        if (string.IsNullOrWhiteSpace(policy.MinecraftVersion) || policy.MinecraftVersion.Length > 64) errors.Add("Minecraft version is invalid.");
        if (policy.LoaderType is not null && (!Loaders.Contains(policy.LoaderType) || string.IsNullOrWhiteSpace(policy.LoaderVersion) || policy.LoaderVersion.Length > 64)) errors.Add("Loader is invalid.");
        if (policy.UpdatePolicy is not ("optional" or "requiredBeforeLaunch")) errors.Add("Update policy is invalid.");
        if (policy.RecommendedJavaMajor is < 8 or > 99) errors.Add("Recommended Java major is invalid.");
        if (policy.RecommendedMemoryMb is < 256 or > 262_144) errors.Add("Recommended memory is invalid.");
        return errors;
    }
}

public sealed record UpdateAvailableEvent(Guid EventId, Guid InstanceId, Guid DeploymentId, string PackId, string PackVersion, DateTimeOffset OccurredAtUtc);
public sealed record PackAssignmentChangedEvent(Guid EventId, Guid InstanceId, long AssignmentRevision, DateTimeOffset OccurredAtUtc);
public sealed record DeploymentCancelledEvent(Guid EventId, Guid InstanceId, Guid DeploymentId, DateTimeOffset OccurredAtUtc);
public sealed record RefreshRequestedEvent(Guid EventId, string Reason, DateTimeOffset OccurredAtUtc);
public sealed record ServerNoticeEvent(Guid EventId, string Severity, string Message, DateTimeOffset ExpiresAtUtc);

public interface IClientEvents
{
    Task UpdateAvailable(UpdateAvailableEvent message);
    Task PackAssignmentChanged(PackAssignmentChangedEvent message);
    Task DeploymentCancelled(DeploymentCancelledEvent message);
    Task RefreshRequested(RefreshRequestedEvent message);
    Task ServerNotice(ServerNoticeEvent message);
}
