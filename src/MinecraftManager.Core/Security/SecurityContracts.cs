namespace MinecraftManager.Core.Security;

public sealed record DeviceCredential(string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc, string RefreshCredential);

public interface ISecureCredentialStore
{
    bool IsAvailable { get; }
    Task<DeviceCredential?> GetAsync(Guid serverProfileId, CancellationToken cancellationToken);
    Task SetAsync(Guid serverProfileId, DeviceCredential credential, CancellationToken cancellationToken);
    Task RemoveAsync(Guid serverProfileId, CancellationToken cancellationToken);
}

public interface IClientRegistrationService
{
    Task<Guid> RegisterAsync(Guid serverProfileId, string registrationCode, string displayName, CancellationToken cancellationToken);
    Task RevokeAsync(Guid serverProfileId, CancellationToken cancellationToken);
}

public enum ServerConnectionStatus { Offline, Connecting, Online, Reauthenticating, RegistrationRequired }

public interface IServerConnectionService : IAsyncDisposable
{
    event EventHandler<Guid>? RefreshRequested;
    IReadOnlyDictionary<Guid, ServerConnectionStatus> Statuses { get; }
    Task ConnectAsync(Guid serverProfileId, CancellationToken cancellationToken);
    Task DisconnectAsync(Guid serverProfileId, CancellationToken cancellationToken);
}

public sealed record UpdateAvailableEvent(Guid EventId, Guid InstanceId, Guid DeploymentId, string PackId, string PackVersion, DateTimeOffset OccurredAtUtc);
public sealed record PackAssignmentChangedEvent(Guid EventId, Guid InstanceId, long AssignmentRevision, DateTimeOffset OccurredAtUtc);
public sealed record DeploymentCancelledEvent(Guid EventId, Guid InstanceId, Guid DeploymentId, DateTimeOffset OccurredAtUtc);
public sealed record RefreshRequestedEvent(Guid EventId, string Reason, DateTimeOffset OccurredAtUtc);
public sealed record ServerNoticeEvent(Guid EventId, string Severity, string Message, DateTimeOffset ExpiresAtUtc);
