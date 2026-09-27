using MinecraftManager.Core.Filesystem;
using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Services;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace MinecraftManager.Infrastructure.Sources;

public sealed class ExecutableDirectoryProvider : IExecutableDirectoryProvider
{
    public string BaseDirectory => AppContext.BaseDirectory;
}

public sealed class LocalArchiveDiscoveryService(
    IExecutableDirectoryProvider executableDirectory,
    IManifestParser parser,
    IApplicationPaths applicationPaths) : ILocalArchiveDiscoveryService
{
    private const int MaximumEntries = 10_500;
    private const long MaximumEntryBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumExpandedBytes = 32L * 1024 * 1024 * 1024;
    private readonly Version clientVersion = typeof(LocalArchiveDiscoveryService).Assembly.GetName().Version ?? new Version(1, 0);

    public LocalArchiveDiscoveryService(IExecutableDirectoryProvider executableDirectory, IManifestParser parser,
        IApplicationPaths applicationPaths, Version clientVersion)
        : this(executableDirectory, parser, applicationPaths) => this.clientVersion = clientVersion;

    public async Task<LocalArchiveDiscoveryResult> DiscoverAsync(string? requiredPackId, string? installedManifestSha256, CancellationToken ct)
    {
        var directory = Path.GetFullPath(Path.Combine(executableDirectory.BaseDirectory, "updates"));
        if (!Directory.Exists(directory)) return new(directory, [], [], false);

        var candidates = new List<LocalArchiveCandidate>();
        var failures = new List<LocalArchiveDiscoveryFailure>();
        var hasCurrentArchive = false;
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".7z", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var manifest = await InspectAsync(path, ct);
                if (requiredPackId is not null && !manifest.Value.PackId.Equals(requiredPackId, StringComparison.Ordinal)) continue;
                if (installedManifestSha256 is not null && manifest.CanonicalSha256.Equals(installedManifestSha256, StringComparison.OrdinalIgnoreCase))
                {
                    hasCurrentArchive = true;
                    continue;
                }
                candidates.Add(new(Path.GetFullPath(path), Path.GetFileName(path), manifest));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or UnsafePathException or ArchiveOperationException)
            {
                failures.Add(new(Path.GetFileName(path), UserSafeReason(ex)));
            }
        }

        return new(directory, candidates, failures, true, hasCurrentArchive);
    }

    private async Task<ValidatedManifest> InspectAsync(string archivePath, CancellationToken ct)
    {
        var archive = new FileInfo(archivePath);
        var cacheRoot = LocalArchiveCache.GetRoot(applicationPaths, archive);
        var cachedManifest = Path.Combine(cacheRoot, "manifest.json");
        if (LocalArchiveCache.IsComplete(cacheRoot) && File.Exists(cachedManifest))
        {
            await using var cached = new FileStream(cachedManifest, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Parse(await parser.ParseAsync(cached, clientVersion, ct));
        }

        return await Task.Run(() => InspectArchiveAsync(archivePath, ct), ct);
    }

    private async Task<ValidatedManifest> InspectArchiveAsync(string archivePath, CancellationToken ct)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        if (archive.Type != ArchiveType.SevenZip) throw new InvalidDataException("The file is not a valid 7z archive.");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IArchiveEntry? manifestEntry = null;
        var files = 0;
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var key = NormalizeAndValidate(entry.Key);
            if (!paths.Add(key)) throw new InvalidDataException($"The archive contains a duplicate path: {key}");
            if (entry.IsDirectory) continue;
            if (++files > MaximumEntries) throw new InvalidDataException($"The archive contains more than {MaximumEntries} files.");
            if (entry.Size < 0 || entry.Size > MaximumEntryBytes) throw new InvalidDataException($"An archive entry has an unsupported size: {key}");
            expandedBytes = checked(expandedBytes + entry.Size);
            if (expandedBytes > MaximumExpandedBytes) throw new InvalidDataException("The expanded archive is larger than 32 GiB.");
            if (key.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) manifestEntry = entry;
        }
        if (manifestEntry is null) throw new InvalidDataException("The archive does not contain manifest.json at its root.");

        await using var stream = manifestEntry.OpenEntryStream();
        return Parse(await parser.ParseAsync(stream, clientVersion, ct));
    }

    private static ValidatedManifest Parse(ManifestValidationResult parsed) => parsed.IsValid
        ? parsed.Manifest!
        : throw new InvalidDataException(string.Join("; ", parsed.Errors.Select(x => x.Message)));

    private static string NormalizeAndValidate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidDataException("The archive contains an entry without a path.");
        var key = RelativeManifestPath.Parse(raw.Replace('\\', '/').TrimEnd('/')).Value;
        if (!key.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) &&
            !key.Equals("files", StringComparison.OrdinalIgnoreCase) &&
            !key.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Archive entries must be manifest.json or below files/: {key}");
        return key;
    }

    private static string UserSafeReason(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "The archive cannot be read.",
        IOException => "The archive could not be opened.",
        _ => ex.Message
    };
}
