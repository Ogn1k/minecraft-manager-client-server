using MinecraftManager.Server.Domain.Entities;

namespace MinecraftManager.Server.Application.Abstractions;

public interface IServerDataStore
{
    IQueryable<Pack> Packs { get; }
    IQueryable<PackVersion> PackVersions { get; }
    IQueryable<PackFile> PackFiles { get; }
    IQueryable<ManagedPath> ManagedPaths { get; }
    IQueryable<FileBlob> FileBlobs { get; }
    IQueryable<Machine> Machines { get; }
    IQueryable<MinecraftInstance> Instances { get; }
    IQueryable<DeviceCredential> DeviceCredentials { get; }
    IQueryable<RegistrationCode> RegistrationCodes { get; }
    IQueryable<PackAssignment> Assignments { get; }
    IQueryable<Deployment> Deployments { get; }
    IQueryable<DeploymentTarget> DeploymentTargets { get; }
    IQueryable<OutboxMessage> OutboxMessages { get; }
    void Add<TEntity>(TEntity entity) where TEntity : class;
    void Remove<TEntity>(TEntity entity) where TEntity : class;
    Task<List<TEntity>> ToListAsync<TEntity>(IQueryable<TEntity> query, CancellationToken cancellationToken);
    Task<TEntity?> SingleOrDefaultAsync<TEntity>(IQueryable<TEntity> query, CancellationToken cancellationToken);
    Task<bool> AnyAsync<TEntity>(IQueryable<TEntity> query, CancellationToken cancellationToken);
    Task<int> CountAsync<TEntity>(IQueryable<TEntity> query, CancellationToken cancellationToken);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken);
}

public sealed record StoredObject(Guid BlobId, string Sha256, long Size, string StorageKey);
public sealed record DownloadTicket(Uri Url, DateTimeOffset ExpiresAtUtc);
public interface IFileStorage
{
    Task<StoredObject> StoreVerifiedAsync(Stream content, long maximumBytes, string? expectedSha256, long? expectedSize, CancellationToken cancellationToken);
    Task<DownloadTicket> CreateDownloadTicketAsync(string storageKey, TimeSpan lifetime, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken);
    Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken);
}

public interface ITokenService
{
    (string Token, DateTimeOffset ExpiresAtUtc) CreateDeviceAccessToken(Machine machine);
    (string Token, DateTimeOffset ExpiresAtUtc) CreateAdminAccessToken(Guid userId, string userName, IEnumerable<string> roles);
}

public interface IClientNotifier
{
    Task SendAsync(string groupName, string eventType, string payloadJson, CancellationToken cancellationToken);
}

public interface ISystemClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : ISystemClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
