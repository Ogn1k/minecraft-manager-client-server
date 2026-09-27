using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Runtime;

namespace MinecraftManager.Infrastructure.Runtime;

public sealed class MojangRuntimeMetadataSource(HttpClient http, IApplicationPaths paths) : IRuntimeMetadataSource
{
    private static readonly Uri ManifestUri = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
    public async Task<string?> GetVersionJsonAsync(string versionId, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(paths.SharedVersionsDirectory, versionId);
        var cached = Path.Combine(directory, versionId + ".json");
        if (File.Exists(cached)) return await File.ReadAllTextAsync(cached, cancellationToken);
        using var manifestResponse = await http.GetAsync(ManifestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        manifestResponse.EnsureSuccessStatusCode();
        await using var manifestStream = await manifestResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var manifest = await JsonDocument.ParseAsync(manifestStream, new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
        var entry = manifest.RootElement.GetProperty("versions").EnumerateArray().FirstOrDefault(x => x.GetProperty("id").GetString() == versionId);
        if (entry.ValueKind == JsonValueKind.Undefined) return null;
        var url = entry.GetProperty("url").GetString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Version metadata URL must use HTTPS.");
        var json = await http.GetStringAsync(uri, cancellationToken);
        if (json.Length > 4 * 1024 * 1024) throw new InvalidDataException("Version metadata is too large.");
        Directory.CreateDirectory(directory);
        var temporary = cached + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, json, cancellationToken);
        File.Move(temporary, cached, true);
        return json;
    }
}

public sealed class MinecraftRuntimeManager(HttpClient http, IApplicationPaths paths) : IRuntimeManager
{
    public async Task<RuntimeValidationResult> ValidateAsync(ResolvedMinecraftVersion version, CancellationToken cancellationToken)
    {
        var missing = new List<string>(); var corrupt = new List<string>();
        foreach (var item in Artifacts(version))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(item.Path)) missing.Add(item.Display);
            else if (!await MatchesAsync(item.Path, item.Artifact, cancellationToken)) corrupt.Add(item.Display);
        }
        if (version.AssetIndex is not null)
        {
            var indexPath = AssetIndexPath(version.AssetIndex);
            if (File.Exists(indexPath)) await ValidateAssetObjectsAsync(indexPath, missing, corrupt, cancellationToken);
        }
        return new(missing.Count == 0 && corrupt.Count == 0, missing, corrupt);
    }

    public async Task<RuntimeRepairResult> RepairAsync(ResolvedMinecraftVersion version, IProgress<RuntimeProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var artifacts = Artifacts(version).ToArray(); long done = 0;
            foreach (var item in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(item.Path) || !await MatchesAsync(item.Path, item.Artifact, cancellationToken))
                    await DownloadAsync(item.Artifact, item.Path, cancellationToken);
                done++;
                progress?.Report(new("runtime", item.Display, done, artifacts.Length));
            }
            if (version.AssetIndex is not null) await RepairAssetObjectsAsync(AssetIndexPath(version.AssetIndex), progress, cancellationToken);
            await MinecraftManager.Infrastructure.Persistence.AtomicJson.WriteAsync(Path.Combine(paths.RuntimeStateDirectory, version.Id + ".json"),
                new RuntimeLedger(1, version.Id, DateTimeOffset.UtcNow, artifacts.Select(x => x.Artifact.RelativePath).ToArray()), cancellationToken);
            return new(true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, "runtime_repair_failed", ex.Message); }
    }

    public async Task<string> PrepareNativesAsync(ResolvedMinecraftVersion version, Guid launchId, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Path.Combine(paths.NativeWorkDirectory, launchId.ToString("N")));
        Directory.CreateDirectory(root);
        foreach (var native in version.Natives)
        {
            var archive = LibraryPath(native.Artifact);
            if (!File.Exists(archive) || !await MatchesAsync(archive, native.Artifact, cancellationToken)) throw new InvalidDataException($"Native library is missing: {native.Name}");
            using var zip = ZipFile.OpenRead(archive);
            long expanded = 0;
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = entry.FullName.Replace('\\', '/');
                if (normalized.EndsWith('/') || native.Excludes.Any(x => normalized.StartsWith(x, StringComparison.Ordinal))) continue;
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Native archive contains a symbolic link.");
                if (normalized.Split('/').Any(x => x is "" or "." or "..") || Path.IsPathRooted(normalized)) throw new InvalidDataException("Native archive contains an unsafe path.");
                expanded = checked(expanded + entry.Length);
                if (expanded > 512L * 1024 * 1024) throw new InvalidDataException("Native archive expands beyond the safety limit.");
                var destination = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(root + Path.DirectorySeparatorChar, PathComparison)) throw new InvalidDataException("Native path escapes its work directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = entry.Open(); await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, cancellationToken);
            }
        }
        return root;
    }

    private IEnumerable<(RuntimeArtifact Artifact, string Path, string Display)> Artifacts(ResolvedMinecraftVersion version)
    {
        foreach (var library in version.Libraries) yield return (library.Artifact, LibraryPath(library.Artifact), library.Name);
        foreach (var native in version.Natives) yield return (native.Artifact, LibraryPath(native.Artifact), native.Name + " (native)");
        if (version.ClientJar is not null) yield return (version.ClientJar, Path.Combine(paths.SharedVersionsDirectory, version.Id, version.Id + ".jar"), version.Id + " client");
        if (version.AssetIndex is not null) yield return (version.AssetIndex.Artifact, AssetIndexPath(version.AssetIndex), "asset index " + version.AssetIndex.Id);
    }

    private string LibraryPath(RuntimeArtifact artifact) => Path.Combine(paths.SharedLibrariesDirectory, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    private string AssetIndexPath(AssetIndexReference index) => Path.Combine(paths.SharedAssetsDirectory, "indexes", index.Id + ".json");

    private async Task DownloadAsync(RuntimeArtifact artifact, string destination, CancellationToken cancellationToken)
    {
        if (artifact.Uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Runtime downloads require HTTPS.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await GetWithRetryAsync(artifact.Uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (artifact.Size is { } expected && response.Content.Headers.ContentLength is { } actual && actual != expected) throw new InvalidDataException("Runtime artifact size does not match metadata.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total = checked(total + read);
                    if (total > (artifact.Size ?? 2L * 1024 * 1024 * 1024)) throw new InvalidDataException("Runtime artifact exceeds its size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            if (!await MatchesAsync(temporary, artifact, cancellationToken)) throw new InvalidDataException("Runtime artifact failed integrity validation.");
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<bool> MatchesAsync(string path, RuntimeArtifact artifact, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (artifact.Size is { } size && info.Length != size) return false;
        if (artifact.Sha256 is not null) return await HashAsync(path, SHA256.Create(), cancellationToken) == artifact.Sha256;
        if (artifact.Sha1 is not null) return await HashAsync(path, SHA1.Create(), cancellationToken) == artifact.Sha1;
        return true;
    }
    private static async Task<string> HashAsync(string path, HashAlgorithm algorithm, CancellationToken cancellationToken)
    {
        using (algorithm) await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            return Convert.ToHexString(await algorithm.ComputeHashAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private async Task ValidateAssetObjectsAsync(string indexPath, List<string> missing, List<string> corrupt, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken));
        foreach (var item in document.RootElement.GetProperty("objects").EnumerateObject().Take(1_000_000))
        {
            var hash = ValidateAssetHash(item.Value.GetProperty("hash").GetString());
            var path = Path.Combine(paths.SharedAssetsDirectory, "objects", hash[..2], hash);
            if (!File.Exists(path)) missing.Add("asset " + item.Name);
            else if (!await MatchesAsync(path, new(new Uri("https://resources.download.minecraft.net/" + hash[..2] + "/" + hash), "", item.Value.GetProperty("size").GetInt64(), hash), cancellationToken)) corrupt.Add("asset " + item.Name);
        }
    }

    private async Task RepairAssetObjectsAsync(string indexPath, IProgress<RuntimeProgress>? progress, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken));
        var objects = document.RootElement.GetProperty("objects").EnumerateObject().Take(1_000_000).ToArray(); long done = 0;
        foreach (var item in objects)
        {
            var hash = ValidateAssetHash(item.Value.GetProperty("hash").GetString());
            var artifact = new RuntimeArtifact(new Uri("https://resources.download.minecraft.net/" + hash[..2] + "/" + hash), hash, item.Value.GetProperty("size").GetInt64(), hash);
            var destination = Path.Combine(paths.SharedAssetsDirectory, "objects", hash[..2], hash);
            if (!File.Exists(destination) || !await MatchesAsync(destination, artifact, cancellationToken)) await DownloadAsync(artifact, destination, cancellationToken);
            progress?.Report(new("assets", item.Name, ++done, objects.Length));
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string ValidateAssetHash(string? hash) => hash is { Length: 40 } && hash.All(char.IsAsciiHexDigit) ? hash.ToLowerInvariant() : throw new InvalidDataException("Asset hash is invalid.");
    private async Task<HttpResponseMessage> GetWithRetryAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (attempt >= 2 || response.IsSuccessStatusCode || response.StatusCode is not (System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests) && (int)response.StatusCode < 500)
                return response;
            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << attempt)), cancellationToken);
        }
    }
    private sealed record RuntimeLedger(int SchemaVersion, string VersionId, DateTimeOffset VerifiedAtUtc, IReadOnlyList<string> Artifacts);
}
