using MinecraftManager.Core.Filesystem;
using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Updates;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace MinecraftManager.Infrastructure.Sources;

public sealed class LocalArchiveUpdateSource(
    string archivePath,
    IManifestParser parser,
    ISafePathResolver paths,
    IApplicationPaths applicationPaths,
    Version clientVersion) : IUpdateSource
{
    private const int MaximumEntries = 10_500;
    private const long MaximumEntryBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumExpandedBytes = 32L * 1024 * 1024 * 1024;
    private readonly string archivePath = Path.GetFullPath(archivePath);
    private LocalFolderUpdateSource? extractedSource;

    public string DisplayName => $"7z archive ({archivePath})";
    public string Identity => "archive:" + archivePath;

    public async Task<ManifestEnvelope> GetManifestAsync(ManifestRequest request, CancellationToken cancellationToken)
    {
        var sourceRoot = await EnsureExtractedAsync(cancellationToken);
        extractedSource ??= new LocalFolderUpdateSource(sourceRoot, parser, paths, clientVersion);
        return await extractedSource.GetManifestAsync(request, cancellationToken);
    }

    public async Task<Stream> OpenFileAsync(ManifestFile file, CancellationToken cancellationToken)
    {
        var sourceRoot = await EnsureExtractedAsync(cancellationToken);
        extractedSource ??= new LocalFolderUpdateSource(sourceRoot, parser, paths, clientVersion);
        return await extractedSource.OpenFileAsync(file, cancellationToken);
    }

    private Task<string> EnsureExtractedAsync(CancellationToken cancellationToken) =>
        Task.Run(() => EnsureExtracted(cancellationToken), cancellationToken);

    private string EnsureExtracted(CancellationToken cancellationToken)
    {
        var archive = new FileInfo(archivePath);
        if (!archive.Exists) throw new FileNotFoundException("The selected 7z archive does not exist.", archivePath);
        if (!archive.Extension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected local archive must be a .7z file.");

        var cacheParent = Path.Combine(applicationPaths.DataDirectory, "cache", "archives");
        var cacheRoot = LocalArchiveCache.GetRoot(applicationPaths, archive);
        if (LocalArchiveCache.IsComplete(cacheRoot)) return cacheRoot;

        Directory.CreateDirectory(cacheParent);
        var temporaryRoot = Path.Combine(cacheParent, ".extracting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            ValidateArchive(cancellationToken);
            ExtractArchive(temporaryRoot, cancellationToken);
            File.WriteAllText(Path.Combine(temporaryRoot, ".complete"), Path.GetFileName(cacheRoot));
            try { Directory.Move(temporaryRoot, cacheRoot); }
            catch (IOException) when (Directory.Exists(cacheRoot)) { }
            return cacheRoot;
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, true);
        }
    }

    private void ValidateArchive(CancellationToken cancellationToken)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        if (archive.Type != ArchiveType.SevenZip) throw new InvalidDataException("The selected file is not a valid 7z archive.");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = 0;
        long expandedBytes = 0;
        var hasManifest = false;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = NormalizeAndValidateEntryPath(entry.Key);
            if (!paths.Add(key)) throw new InvalidDataException($"The archive contains duplicate paths: {key}");
            if (entry.IsDirectory) continue;
            if (++files > MaximumEntries) throw new InvalidDataException($"The archive contains more than {MaximumEntries} files.");
            if (entry.Size < 0 || entry.Size > MaximumEntryBytes) throw new InvalidDataException($"An archive entry has an unsupported size: {key}");
            expandedBytes = checked(expandedBytes + entry.Size);
            if (expandedBytes > MaximumExpandedBytes) throw new InvalidDataException("The expanded archive is larger than 32 GiB.");
            if (key.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) hasManifest = true;
        }
        if (!hasManifest) throw new InvalidDataException("The 7z archive does not contain manifest.json at its root.");
    }

    private void ExtractArchive(string destinationRoot, CancellationToken cancellationToken)
    {
        var trustedDestination = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        using var reader = archive.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = NormalizeAndValidateEntryPath(reader.Entry.Key);
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, key.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(trustedDestination, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive entry escapes the extraction directory: {key}");
            if (reader.Entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            reader.WriteEntryToFile(destination, new ExtractionOptions { Overwrite = false, ExtractFullPath = false });
        }
    }

    private static string NormalizeAndValidateEntryPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidDataException("The archive contains an entry without a path.");
        var key = raw.Replace('\\', '/').TrimEnd('/');
        var path = RelativeManifestPath.Parse(key).Value;
        if (!path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) &&
            !path.Equals("files", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Archive entries must be manifest.json or below files/: {path}");
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        if (extractedSource is not null) await extractedSource.DisposeAsync();
    }
}
