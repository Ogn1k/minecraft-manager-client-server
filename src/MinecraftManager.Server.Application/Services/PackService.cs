using System.Text.Json;
using MinecraftManager.Server.Application.Abstractions;
using MinecraftManager.Server.Domain.Common;
using MinecraftManager.Server.Domain.Entities;

namespace MinecraftManager.Server.Application.Services;

public sealed class PackService(IServerDataStore data, ISystemClock clock)
{
    public async Task<Pack> CreatePackAsync(string slug, string displayName, CancellationToken cancellationToken)
    {
        var normalized = slug.Trim().ToLowerInvariant();
        if (normalized.Length is < 2 or > 80 || normalized.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new DomainRuleException("invalid_pack_slug", "Pack slug is invalid.");
        if (await data.AnyAsync(data.Packs.Where(x => x.NormalizedSlug == normalized), cancellationToken)) throw new DomainRuleException("pack_exists", "Pack slug already exists.");
        var pack = new Pack { Slug = slug.Trim(), NormalizedSlug = normalized, DisplayName = Required(displayName, 120) };
        data.Add(pack); await data.SaveChangesAsync(cancellationToken); return pack;
    }

    private static readonly HashSet<string> SupportedLoaders = new(StringComparer.OrdinalIgnoreCase) { "fabric", "neoforge", "forge", "quilt" };

    public async Task<PackVersion> CreateDraftAsync(Guid packId, string version, string? minimumClientVersion, string? minecraftVersion, string? loaderType, string? loaderVersion, CancellationToken cancellationToken)
    {
        if (!await data.AnyAsync(data.Packs.Where(x => x.Id == packId && !x.IsArchived), cancellationToken)) throw new DomainRuleException("pack_not_found", "Pack was not found.");
        var normalized = NormalizeVersion(version);
        if (await data.AnyAsync(data.PackVersions.Where(x => x.PackId == packId && x.NormalizedVersion == normalized), cancellationToken)) throw new DomainRuleException("version_exists", "Pack version already exists.");
        ValidateRuntimeDeclaration(minecraftVersion, loaderType, loaderVersion);
        var entity = new PackVersion { PackId = packId, Version = version.Trim(), NormalizedVersion = normalized, MinimumClientVersion = minimumClientVersion, MinecraftVersion = minecraftVersion, LoaderType = loaderType, LoaderVersion = loaderVersion };
        data.Add(entity); await data.SaveChangesAsync(cancellationToken); return entity;
    }

    public async Task SetManagedPathsAsync(Guid versionId, IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        var version = await data.SingleOrDefaultAsync(data.PackVersions.Where(x => x.Id == versionId), cancellationToken) ?? throw new DomainRuleException("version_not_found", "Pack version was not found.");
        version.EnsureDraft(); var normalized = paths.Select(x => new ManagedScope(x).Value).ToArray(); ManifestCanonicalizer.EnsureNoCollisions(normalized);
        foreach (var existing in await data.ToListAsync(data.ManagedPaths.Where(x => x.PackVersionId == versionId), cancellationToken)) data.Remove(existing);
        foreach (var path in normalized) data.Add(new ManagedPath { PackVersionId = versionId, Path = path, NormalizedPath = path.Normalize().ToUpperInvariant() });
        version.ConcurrencyToken = Guid.NewGuid(); await data.SaveChangesAsync(cancellationToken);
    }

    public async Task AddFileAsync(Guid versionId, string path, Guid blobId, string? contentType, CancellationToken cancellationToken)
    {
        var version = await data.SingleOrDefaultAsync(data.PackVersions.Where(x => x.Id == versionId), cancellationToken) ?? throw new DomainRuleException("version_not_found", "Pack version was not found.");
        version.EnsureDraft(); var relative = new RelativeManifestPath(path);
        var blob = await data.SingleOrDefaultAsync(data.FileBlobs.Where(x => x.Id == blobId && x.State == BlobState.Verified), cancellationToken) ?? throw new DomainRuleException("blob_not_verified", "The blob is unavailable or unverified.");
        var normalizedPath = relative.Value.Normalize().ToUpperInvariant();
        if (await data.AnyAsync(data.PackFiles.Where(x => x.PackVersionId == versionId && x.NormalizedPath == normalizedPath), cancellationToken)) throw new DomainRuleException("path_collision", "A file already uses this path.");
        data.Add(new PackFile { PackVersionId = versionId, Path = relative.Value, NormalizedPath = normalizedPath, BlobId = blob.Id, ContentType = contentType }); version.ConcurrencyToken = Guid.NewGuid(); await data.SaveChangesAsync(cancellationToken);
    }

    public async Task<(byte[] Manifest, string Digest)> PreviewAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var version = await data.SingleOrDefaultAsync(data.PackVersions.Where(x => x.Id == versionId), cancellationToken) ?? throw new DomainRuleException("version_not_found", "Pack version was not found.");
        var pack = await data.SingleOrDefaultAsync(data.Packs.Where(x => x.Id == version.PackId), cancellationToken) ?? throw new DomainRuleException("pack_not_found", "Pack was not found.");
        var scopes = await data.ToListAsync(data.ManagedPaths.Where(x => x.PackVersionId == versionId).Select(x => x.Path), cancellationToken);
        var files = await data.ToListAsync(from file in data.PackFiles join blob in data.FileBlobs on file.BlobId equals blob.Id where file.PackVersionId == versionId && blob.State == BlobState.Verified select new BlobJoin(file, blob), cancellationToken);
        if (files.Count != await data.CountAsync(data.PackFiles.Where(x => x.PackVersionId == versionId), cancellationToken)) throw new DomainRuleException("blob_not_verified", "Every pack file must reference a verified blob.");
        var built = ManifestCanonicalizer.Build(pack.Slug, version.Version, version.MinimumClientVersion, scopes.Select(x => new ManagedScope(x)), files.Select(x => (x.file.Id, new RelativeManifestPath(x.file.Path), x.blob.Size, new Sha256Digest(x.blob.Sha256), x.file.ContentType)), version.MinecraftVersion, version.LoaderType is { } loaderType && version.LoaderVersion is { } loaderVersion ? (loaderType, loaderVersion) : null);
        return (built.Bytes, built.Digest.Value);
    }

    public async Task<PackVersion> PublishAsync(Guid versionId, CancellationToken cancellationToken)
    {
        await using var transaction = await data.BeginTransactionAsync(cancellationToken);
        var version = await data.SingleOrDefaultAsync(data.PackVersions.Where(x => x.Id == versionId), cancellationToken) ?? throw new DomainRuleException("version_not_found", "Pack version was not found.");
        version.EnsureDraft(); var preview = await PreviewAsync(versionId, cancellationToken); version.Publish(preview.Manifest, new(preview.Digest), clock.UtcNow); await data.SaveChangesAsync(cancellationToken);
        if (transaction is ICommitTransaction commit) await commit.CommitAsync(cancellationToken);
        return version;
    }

    private static void ValidateRuntimeDeclaration(string? minecraftVersion, string? loaderType, string? loaderVersion)
    {
        if (minecraftVersion is { } mc && (mc.Length is < 1 or > 64 || !System.Text.RegularExpressions.Regex.IsMatch(mc, @"^[0-9A-Za-z._+\-]+$")))
            throw new DomainRuleException("invalid_minecraft_version", "Minecraft version is invalid.");
        if (loaderType is not null)
        {
            if (!SupportedLoaders.Contains(loaderType)) throw new DomainRuleException("invalid_mod_loader", "Mod loader type is not supported.");
            if (string.IsNullOrWhiteSpace(loaderVersion) || loaderVersion.Trim().Length is < 1 or > 128) throw new DomainRuleException("invalid_mod_loader_version", "Mod loader version is invalid.");
        }
        else if (loaderVersion is not null) throw new DomainRuleException("invalid_mod_loader", "Mod loader version was provided without a loader type.");
    }

    private static string NormalizeVersion(string value)
    {
        value = value.Trim();
        if (value.Length is < 1 or > 128 || !System.Text.RegularExpressions.Regex.IsMatch(value, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")) throw new DomainRuleException("invalid_version", "Pack version must use SemVer 2.0 syntax.");
        return value.ToLowerInvariant();
    }
    private static string Required(string value, int max) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > max ? throw new DomainRuleException("invalid_value", "A required value is invalid.") : value.Trim();
    private sealed record BlobJoin(PackFile file, FileBlob blob);
}

public interface ICommitTransaction : IAsyncDisposable { Task CommitAsync(CancellationToken cancellationToken); }
