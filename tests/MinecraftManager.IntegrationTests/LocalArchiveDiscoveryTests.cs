using System.Text.Json;
using MinecraftManager.Core.Services;
using MinecraftManager.Infrastructure.Sources;
using MinecraftManager.Core.Manifests;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using MinecraftManager.Infrastructure.Persistence;

namespace MinecraftManager.IntegrationTests;

public sealed class LocalArchiveDiscoveryTests
{
    [Fact]
    public async Task UsesOnlyExecutableAdjacentUpdatesDirectoryWithoutRecursing()
    {
        using var root = new TemporaryDirectory();
        var updates = Directory.CreateDirectory(Path.Combine(root.Path, "updates")).FullName;
        Directory.CreateDirectory(Path.Combine(updates, "nested"));
        CreatePack(Path.Combine(updates, "b.7Z"), "pack-b", "2");
        CreatePack(Path.Combine(updates, "a.7z"), "pack-a", "1");
        CreatePack(Path.Combine(updates, "nested", "ignored.7z"), "ignored", "1");
        await File.WriteAllTextAsync(Path.Combine(updates, "ignored.zip"), "not an archive");
        var service = Create(root.Path);

        var result = await service.DiscoverAsync(null, null, default);

        Assert.True(result.DirectoryExists);
        Assert.Equal(updates, result.UpdatesDirectory);
        Assert.Equal(["a.7z", "b.7Z"], result.Candidates.Select(x => x.FileName));
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task MissingDirectoryIsReportedWithoutCreatingIt()
    {
        using var root = new TemporaryDirectory();
        var service = Create(root.Path);

        var result = await service.DiscoverAsync(null, null, default);

        Assert.False(result.DirectoryExists);
        Assert.Empty(result.Candidates);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "updates")));
    }

    [Fact]
    public async Task InvalidArchiveDoesNotHideValidCandidate()
    {
        using var root = new TemporaryDirectory();
        var updates = Directory.CreateDirectory(Path.Combine(root.Path, "updates")).FullName;
        CreatePack(Path.Combine(updates, "valid.7z"), "main", "1");
        await File.WriteAllTextAsync(Path.Combine(updates, "broken.7z"), "broken");
        var service = Create(root.Path);

        var result = await service.DiscoverAsync(null, null, default);

        Assert.Single(result.Candidates);
        Assert.Single(result.Failures);
        Assert.Equal("broken.7z", result.Failures[0].FileName);
    }

    [Fact]
    public async Task FiltersDifferentPackAndInstalledManifest()
    {
        using var root = new TemporaryDirectory();
        var updates = Directory.CreateDirectory(Path.Combine(root.Path, "updates")).FullName;
        CreatePack(Path.Combine(updates, "main.7z"), "main", "1");
        CreatePack(Path.Combine(updates, "other.7z"), "other", "1");
        var service = Create(root.Path);
        var first = await service.DiscoverAsync("main", null, default);
        var installedHash = Assert.Single(first.Candidates).Manifest.CanonicalSha256;

        var result = await service.DiscoverAsync("main", installedHash, default);

        Assert.Empty(result.Candidates);
        Assert.True(result.HasCurrentArchive);
    }

    [Fact]
    public async Task UnsafeEntryIsRejected()
    {
        using var root = new TemporaryDirectory();
        var updates = Directory.CreateDirectory(Path.Combine(root.Path, "updates")).FullName;
        var archive = Path.Combine(updates, "unsafe.7z");
        CreateSevenZip(archive, ("manifest.json", Manifest("main", "1")), ("../escape.txt", "bad"));

        var result = await Create(root.Path).DiscoverAsync(null, null, default);

        Assert.Empty(result.Candidates);
        Assert.Single(result.Failures);
    }

    private static LocalArchiveDiscoveryService Create(string root) =>
        new(new FixedExecutableDirectory(root), new ManifestParser(), new ApplicationPaths(Path.Combine(root, "app-data")), new Version(1, 0, 0));

    private static void CreatePack(string path, string packId, string version) =>
        CreateSevenZip(path, ("manifest.json", Manifest(packId, version)));

    private static string Manifest(string packId, string version) => JsonSerializer.Serialize(new
    {
        schemaVersion = 2,
        packId,
        packVersion = version,
        minecraftVersion = "1.21.1",
        managedPaths = Array.Empty<string>(),
        files = Array.Empty<object>(),
        metadata = new { displayName = packId, releaseNotes = (string?)null }
    });

    private static void CreateSevenZip(string path, params (string Path, string Content)[] files)
    {
        using var output = File.Create(path);
        using var writer = WriterFactory.OpenWriter(output, ArchiveType.SevenZip, new SevenZipWriterOptions(CompressionType.LZMA2));
        foreach (var item in files)
        {
            using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(item.Content));
            writer.Write(item.Path, content, null);
        }
    }

    private sealed class FixedExecutableDirectory(string path) : IExecutableDirectoryProvider
    {
        public string BaseDirectory { get; } = path;
    }
}
