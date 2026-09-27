using MinecraftManager.Core.Filesystem;
using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Updates;

namespace MinecraftManager.Infrastructure.Sources;

public sealed class LocalFolderUpdateSource(
    string folderPath,
    IManifestParser parser,
    ISafePathResolver paths,
    Version clientVersion) : IUpdateSource
{
    private readonly TrustedRoot root = TrustedRoot.Create(folderPath);
    public string DisplayName => $"Local folder ({root.FullPath})";
    public string Identity => "local:" + root.FullPath;

    public async Task<ManifestEnvelope> GetManifestAsync(ManifestRequest request, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root.FullPath, "manifest.json");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var result = await parser.ParseAsync(stream, clientVersion, cancellationToken);
        if (!result.IsValid) throw new InvalidDataException(string.Join("; ", result.Errors.Select(x => x.Message)));
        return new(result.Manifest);
    }

    public Task<Stream> OpenFileAsync(ManifestFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filesRoot = TrustedRoot.Create(Path.Combine(root.FullPath, "files"));
        var relative = RelativeManifestPath.Parse(file.SourcePath ?? file.Path);
        var resolved = paths.ResolveFile(filesRoot, relative);
        paths.VerifyNoLinks(filesRoot, resolved);
        var info = new FileInfo(resolved.FullPath);
        if (!info.Exists || info.Length != file.Size) throw new InvalidDataException($"Source file size does not match manifest: {relative.Value}");
        return Task.FromResult<Stream>(new FileStream(resolved.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
