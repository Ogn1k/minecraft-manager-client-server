using System.Security.Cryptography;
using MinecraftManager.Core.Filesystem;
using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Models;

namespace MinecraftManager.Core.Updates;

public interface IHashService
{
    Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken);
    Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken);
}

public sealed class HashService : IHashService
{
    public async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeSha256Async(stream, cancellationToken);
    }
}

public interface IUpdatePlanner
{
    Task<UpdatePlan> CreatePlanAsync(MinecraftInstance instance, InstanceState state, ValidatedManifest manifest, CancellationToken cancellationToken);
}

public sealed class UpdatePlanner(ISafePathResolver paths, IHashService hashes) : IUpdatePlanner
{
    public async Task<UpdatePlan> CreatePlanAsync(MinecraftInstance instance, InstanceState state, ValidatedManifest manifest, CancellationToken cancellationToken)
    {
        var root = TrustedRoot.Create(instance.Location.GameDirectory);
        var adds = new List<PlannedFile>();
        var replaces = new List<PlannedReplacement>();
        var unchanged = new List<string>();
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in manifest.Value.Files.OrderBy(x => x.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = RelativeManifestPath.Parse(file.Path);
            desired.Add(relative.Value);
            var resolved = paths.ResolveFile(root, relative);
            paths.VerifyNoLinks(root, resolved);
            if (!File.Exists(resolved.FullPath)) { adds.Add(new(relative.Value, file.Sha256.ToLowerInvariant(), file.Size)); continue; }
            if ((File.GetAttributes(resolved.FullPath) & FileAttributes.Directory) != 0)
                throw new IOException($"Expected a regular file at {relative.Value}.");
            var actual = await hashes.ComputeFileSha256Async(resolved.FullPath, cancellationToken);
            if (actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) unchanged.Add(relative.Value);
            else replaces.Add(new(relative.Value, file.Sha256.ToLowerInvariant(), file.Size, actual));
        }

        var scopes = manifest.Value.ManagedPaths.Select(ManagedScope.Parse).ToArray();
        var deletes = new List<PlannedDeletion>();
        foreach (var managed in state.ManagedFiles.OrderBy(x => x.Path, StringComparer.Ordinal))
        {
            if (desired.Contains(managed.Path)) continue;
            var relative = RelativeManifestPath.Parse(managed.Path);
            if (!scopes.Any(x => x.Contains(relative))) continue;
            var resolved = paths.ResolveFile(root, relative);
            paths.VerifyNoLinks(root, resolved);
            if (!File.Exists(resolved.FullPath)) continue;
            var actual = await hashes.ComputeFileSha256Async(resolved.FullPath, cancellationToken);
            if (actual.Equals(managed.Sha256, StringComparison.OrdinalIgnoreCase))
                deletes.Add(new(relative.Value, actual, new FileInfo(resolved.FullPath).Length));
        }

        return new(Guid.NewGuid(), instance.Id, manifest.Value.PackId, manifest.Value.PackVersion, manifest.CanonicalSha256,
            adds, replaces, deletes, unchanged, [], adds.Sum(x => x.Size) + replaces.Sum(x => x.Size),
            replaces.Sum(x => new FileInfo(paths.ResolveFile(root, RelativeManifestPath.Parse(x.Path)).FullPath).Length) + deletes.Sum(x => x.Size), DateTimeOffset.UtcNow);
    }
}
