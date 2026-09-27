using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinecraftManager.Server.Application.Abstractions;
using MinecraftManager.Server.Application.Services;
using MinecraftManager.Server.Domain.Entities;

namespace MinecraftManager.Server.Infrastructure.Persistence;

public sealed class ServerUser : IdentityUser<Guid> { public bool Enabled { get; set; } = true; }
public sealed class ServerRole : IdentityRole<Guid> { }

public sealed class ServerDbContext(DbContextOptions<ServerDbContext> options) : IdentityDbContext<ServerUser, ServerRole, Guid>(options), IServerDataStore
{
    public DbSet<Pack> PackSet => Set<Pack>(); public IQueryable<Pack> Packs => PackSet;
    public DbSet<PackVersion> PackVersionSet => Set<PackVersion>(); public IQueryable<PackVersion> PackVersions => PackVersionSet;
    public IQueryable<PackFile> PackFiles => Set<PackFile>(); public IQueryable<ManagedPath> ManagedPaths => Set<ManagedPath>(); public IQueryable<FileBlob> FileBlobs => Set<FileBlob>();
    public IQueryable<Machine> Machines => Set<Machine>(); public IQueryable<MinecraftInstance> Instances => Set<MinecraftInstance>(); public IQueryable<DeviceCredential> DeviceCredentials => Set<DeviceCredential>(); public IQueryable<RegistrationCode> RegistrationCodes => Set<RegistrationCode>();
    public IQueryable<PackAssignment> Assignments => Set<PackAssignment>(); public IQueryable<Deployment> Deployments => Set<Deployment>(); public IQueryable<DeploymentTarget> DeploymentTargets => Set<DeploymentTarget>(); public IQueryable<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    void IServerDataStore.Add<TEntity>(TEntity entity) => Set<TEntity>().Add(entity);
    void IServerDataStore.Remove<TEntity>(TEntity entity) => Set<TEntity>().Remove(entity);
    public Task<List<TEntity>> ToListAsync<TEntity>(IQueryable<TEntity> query, CancellationToken cancellationToken) => EntityFrameworkQueryableExtensions.ToListAsync(query, cancellationToken);
    public Task<TEntity?> SingleOrDefaultAsync<TEntity>(IQueryable<TEntity> query, CancellationToken cancellationToken) => EntityFrameworkQueryableExtensions.SingleOrDefaultAsync(query, cancellationToken);
    public Task<bool> AnyAsync<TEntity>(IQueryable<TEntity> query, CancellationToken cancellationToken) => EntityFrameworkQueryableExtensions.AnyAsync(query, cancellationToken);
    public Task<int> CountAsync<TEntity>(IQueryable<TEntity> query, CancellationToken cancellationToken) => EntityFrameworkQueryableExtensions.CountAsync(query, cancellationToken);
    async Task<IAsyncDisposable> IServerDataStore.BeginTransactionAsync(CancellationToken cancellationToken) => new EfTransaction(await Database.BeginTransactionAsync(cancellationToken));

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<Pack>(e => { e.ToTable("packs"); e.HasKey(x => x.Id); e.Property(x => x.Slug).HasMaxLength(80); e.Property(x => x.NormalizedSlug).HasMaxLength(80); e.Property(x => x.DisplayName).HasMaxLength(120); e.HasIndex(x => x.NormalizedSlug).IsUnique(); });
        b.Entity<PackVersion>(e => { e.ToTable("pack_versions"); e.HasKey(x => x.Id); e.Property(x => x.Version).HasMaxLength(128); e.Property(x => x.NormalizedVersion).HasMaxLength(128); e.Property(x => x.MinecraftVersion).HasMaxLength(64); e.Property(x => x.LoaderType).HasMaxLength(32); e.Property(x => x.LoaderVersion).HasMaxLength(128); e.Property(x => x.ManifestSha256).HasMaxLength(64); e.Property(x => x.ConcurrencyToken).IsConcurrencyToken(); e.HasIndex(x => new { x.PackId, x.NormalizedVersion }).IsUnique(); e.HasOne(x => x.Pack).WithMany(x => x.Versions).HasForeignKey(x => x.PackId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<PackFile>(e => { e.ToTable("pack_files"); e.HasKey(x => x.Id); e.Property(x => x.Path).HasMaxLength(1024); e.Property(x => x.NormalizedPath).HasMaxLength(1024); e.HasIndex(x => new { x.PackVersionId, x.NormalizedPath }).IsUnique(); e.HasOne(x => x.Blob).WithMany().HasForeignKey(x => x.BlobId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<ManagedPath>(e => { e.ToTable("managed_paths"); e.HasKey(x => x.Id); e.Property(x => x.Path).HasMaxLength(1024); e.Property(x => x.NormalizedPath).HasMaxLength(1024); e.HasIndex(x => new { x.PackVersionId, x.NormalizedPath }).IsUnique(); });
        b.Entity<FileBlob>(e => { e.ToTable("file_blobs"); e.HasKey(x => x.Id); e.Property(x => x.Sha256).HasMaxLength(64); e.Property(x => x.StorageKey).HasMaxLength(256); e.HasIndex(x => new { x.Sha256, x.Size }).IsUnique(); e.HasIndex(x => x.StorageKey).IsUnique(); });
        b.Entity<Machine>(e => { e.ToTable("machines"); e.HasKey(x => x.Id); e.Property(x => x.DisplayName).HasMaxLength(120); e.HasIndex(x => new { x.OwnerUserId, x.LastSeenAtUtc }); });
        b.Entity<MinecraftInstance>(e => { e.ToTable("minecraft_instances"); e.HasKey(x => x.Id); e.Property(x => x.DisplayName).HasMaxLength(120); e.HasIndex(x => new { x.MachineId, x.ClientInstanceId }).IsUnique(); });
        b.Entity<DeviceCredential>(e => { e.ToTable("device_credentials"); e.HasKey(x => x.Id); e.Property(x => x.SecretHash).HasMaxLength(128); e.Property(x => x.ConcurrencyToken).IsConcurrencyToken(); e.HasIndex(x => new { x.MachineId, x.RevokedAtUtc }); });
        b.Entity<RegistrationCode>(e => { e.ToTable("registration_codes"); e.HasKey(x => x.Id); e.Property(x => x.CodeHash).HasMaxLength(128); e.Property(x => x.ConcurrencyToken).IsConcurrencyToken(); e.HasIndex(x => x.CodeHash).IsUnique(); });
        b.Entity<ClientSession>(e => { e.ToTable("client_sessions"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.MachineId, x.LastSeenAtUtc }); });
        b.Entity<ApiKey>(e => { e.ToTable("api_keys"); e.HasKey(x => x.Id); e.Property(x => x.PublicId).HasMaxLength(32); e.Property(x => x.SecretHash).HasMaxLength(128); e.HasIndex(x => x.PublicId).IsUnique(); });
        b.Entity<PackAssignment>(e => { e.ToTable("pack_assignments"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.InstanceId, x.PackId }).IsUnique().HasFilter("\"IsActive\" = TRUE"); e.HasIndex(x => new { x.InstanceId, x.IsActive }); });
        b.Entity<Deployment>(e => { e.ToTable("deployments"); e.HasKey(x => x.Id); e.HasMany(x => x.Targets).WithOne().HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<DeploymentTarget>(e => { e.ToTable("deployment_targets"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.DeploymentId, x.InstanceId }).IsUnique(); });
        b.Entity<AuditEvent>(e => { e.ToTable("audit_events"); e.HasKey(x => x.Id); e.HasIndex(x => x.OccurredAtUtc); });
        b.Entity<OutboxMessage>(e => { e.ToTable("outbox_messages"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.PublishedAtUtc, x.NextAttemptAtUtc }); });
        b.Entity<IdempotencyRecord>(e => { e.ToTable("idempotency_records"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.Subject, x.Key }).IsUnique(); });
        b.Entity<ImportJob>(e => { e.ToTable("import_jobs"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.State, x.CreatedAtUtc }); });
        b.Entity<UploadRecord>(e => { e.ToTable("upload_records"); e.HasKey(x => x.Id); e.Property(x => x.TemporaryKey).HasMaxLength(256); e.HasIndex(x => new { x.State, x.ExpiresAtUtc }); });
    }

    private sealed class EfTransaction(IDbContextTransaction inner) : ICommitTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => inner.CommitAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
