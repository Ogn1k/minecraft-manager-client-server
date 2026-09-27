using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftManager.Core.Filesystem;
using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Updates;
using MinecraftManager.Infrastructure.Persistence;
using MinecraftManager.Infrastructure.Sources;
using MinecraftManager.Infrastructure.Updates;
using MinecraftManager.Infrastructure.Diagnostics;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;

namespace MinecraftManager.IntegrationTests;

public sealed class PersistenceAndUpdateTests
{
    [Fact]
    public async Task ConfigurationRoundTripsMultipleInstancesWithoutCredentials()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonConfigurationStore(new ApplicationPaths(temp.Path));
        var config = ClientConfiguration.Empty with
        {
            Instances =
        [
            Instance("One", temp.Path, temp.Path), Instance("Two", temp.Path, temp.Path)
        ]
        };
        await store.SaveAsync(config, default);
        var loaded = await store.LoadAsync(default);
        Assert.Equal(2, loaded.Instances.Count);
        var json = await File.ReadAllTextAsync(Path.Combine(temp.Path, "config.json"));
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalSourceTransactionAddsReplacesDeletesAndPreservesUnmanaged()
    {
        using var app = new TemporaryDirectory(); using var root = new TemporaryDirectory(); using var source = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, "mods")); Directory.CreateDirectory(Path.Combine(source.Path, "files", "mods"));
        await File.WriteAllTextAsync(Path.Combine(root.Path, "mods", "replace.jar"), "old");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "mods", "delete.jar"), "delete");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "options.txt"), "user-owned");
        await File.WriteAllTextAsync(Path.Combine(source.Path, "files", "mods", "add.jar"), "add");
        await File.WriteAllTextAsync(Path.Combine(source.Path, "files", "mods", "replace.jar"), "new");
        var manifest = new PackManifest
        {
            SchemaVersion = 1,
            PackId = "pack",
            PackVersion = "2.0",
            ManagedPaths = ["mods/"],
            Files =
        [new() { Path = "mods/add.jar", Size = 3, Sha256 = Hash("add") }, new() { Path = "mods/replace.jar", Size = 3, Sha256 = Hash("new") }]
        };
        await File.WriteAllTextAsync(Path.Combine(source.Path, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var appPaths = new ApplicationPaths(app.Path); var config = new JsonConfigurationStore(appPaths); var stateStore = new JsonInstanceStateStore(appPaths);
        var instance = Instance("Test", root.Path, source.Path);
        await config.SaveAsync(ClientConfiguration.Empty with { Instances = [instance] }, default);
        await stateStore.SaveAsync(instance.Id, new(null, [new("mods/replace.jar", Hash("old"), 3), new("mods/delete.jar", Hash("delete"), 6)]), default);
        var parser = new ManifestParser(); var resolver = new SafePathResolver(); var hash = new HashService();
        await using var updateSource = new LocalFolderUpdateSource(source.Path, parser, resolver, new Version(1, 0));
        var validated = (await updateSource.GetManifestAsync(new(), default)).Manifest!;
        var plan = await new UpdatePlanner(resolver, hash).CreatePlanAsync(instance, await stateStore.LoadAsync(instance.Id, default), validated, default);
        var journals = new RecordingJournalStore();
        var updates = new List<UpdateProgress>();
        var executor = new UpdateExecutor(appPaths, resolver, hash, config, stateStore, journals, new JsonHistoryStore(appPaths));
        var result = await executor.ExecuteAsync(UpdateConfirmation.Confirm(plan), validated, updateSource, new InlineProgress<UpdateProgress>(updates.Add), default);

        Assert.True(result.Succeeded, result.Failure?.SafeMessage);
        Assert.Equal("add", await File.ReadAllTextAsync(Path.Combine(root.Path, "mods", "add.jar")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(root.Path, "mods", "replace.jar")));
        Assert.False(File.Exists(Path.Combine(root.Path, "mods", "delete.jar")));
        Assert.Equal("user-owned", await File.ReadAllTextAsync(Path.Combine(root.Path, "options.txt")));
        Assert.Equal("2.0", (await stateStore.LoadAsync(instance.Id, default)).InstalledPack!.PackVersion);
        Assert.Equal(8, journals.SaveCount);
        Assert.Contains(updates, x => x.Stage == UpdateStage.BackingUp);
        Assert.Contains(updates, x => x.Stage == UpdateStage.Validating);
    }

    [Fact]
    public async Task HashMismatchDoesNotModifyTarget()
    {
        using var app = new TemporaryDirectory(); using var root = new TemporaryDirectory(); using var source = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(source.Path, "files", "mods"));
        await File.WriteAllTextAsync(Path.Combine(source.Path, "files", "mods", "bad.jar"), "bad");
        var manifest = new PackManifest { SchemaVersion = 1, PackId = "pack", PackVersion = "1", ManagedPaths = ["mods/"], Files = [new() { Path = "mods/bad.jar", Size = 3, Sha256 = Hash("not-bad") }] };
        await File.WriteAllTextAsync(Path.Combine(source.Path, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var appPaths = new ApplicationPaths(app.Path); var config = new JsonConfigurationStore(appPaths); var state = new JsonInstanceStateStore(appPaths);
        var instance = Instance("Test", root.Path, source.Path); await config.SaveAsync(ClientConfiguration.Empty with { Instances = [instance] }, default);
        var parser = new ManifestParser(); var resolver = new SafePathResolver(); var hashes = new HashService();
        await using var updateSource = new LocalFolderUpdateSource(source.Path, parser, resolver, new Version(1, 0));
        var validated = (await updateSource.GetManifestAsync(new(), default)).Manifest!;
        var plan = await new UpdatePlanner(resolver, hashes).CreatePlanAsync(instance, new(null, []), validated, default);
        var executor = new UpdateExecutor(appPaths, resolver, hashes, config, state, new JsonTransactionJournalStore(appPaths), new JsonHistoryStore(appPaths));
        var result = await executor.ExecuteAsync(UpdateConfirmation.Confirm(plan), validated, updateSource, null, default);
        Assert.False(result.Succeeded); Assert.False(File.Exists(Path.Combine(root.Path, "mods", "bad.jar")));
    }

    [Fact]
    public async Task SevenZipSourceReadsManifestAndPackFileThroughSafeCache()
    {
        using var app = new TemporaryDirectory(); using var source = new TemporaryDirectory();
        var archivePath = Path.Combine(source.Path, "pack.7z");
        var manifest = new PackManifest
        {
            SchemaVersion = 1,
            PackId = "archive-pack",
            PackVersion = "1.0",
            ManagedPaths = ["mods/"],
            Files = [new() { Path = "mods/example.jar", Size = 3, Sha256 = Hash("jar") }]
        };
        CreateSevenZip(archivePath,
            ("manifest.json", JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            ("files/mods/example.jar", "jar"));

        await using var updateSource = new LocalArchiveUpdateSource(
            archivePath, new ManifestParser(), new SafePathResolver(), new ApplicationPaths(app.Path), new Version(1, 0));

        var envelope = await updateSource.GetManifestAsync(new(), default);
        Assert.Equal("archive-pack", envelope.Manifest!.Value.PackId);
        await using var content = await updateSource.OpenFileAsync(envelope.Manifest.Value.Files[0], default);
        using var reader = new StreamReader(content);
        Assert.Equal("jar", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task SevenZipSourceRejectsEntryOutsidePackStructure()
    {
        using var app = new TemporaryDirectory(); using var source = new TemporaryDirectory();
        var archivePath = Path.Combine(source.Path, "unsafe.7z");
        CreateSevenZip(archivePath, ("manifest.json", "{}"), ("../escape.txt", "unsafe"));
        await using var updateSource = new LocalArchiveUpdateSource(
            archivePath, new ManifestParser(), new SafePathResolver(), new ApplicationPaths(app.Path), new Version(1, 0));

        await Assert.ThrowsAnyAsync<Exception>(() => updateSource.GetManifestAsync(new(), default));

        Assert.False(File.Exists(Path.Combine(app.Path, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(source.Path, "escape.txt")));
    }

    [Fact]
    public async Task RecoveryRemovesAppliedAddition()
    {
        using var app = new TemporaryDirectory(); using var root = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, "mods")); await File.WriteAllTextAsync(Path.Combine(root.Path, "mods", "new.jar"), "new");
        var appPaths = new ApplicationPaths(app.Path); var journalStore = new JsonTransactionJournalStore(appPaths); var tx = Guid.NewGuid();
        var journal = new TransactionJournal(1, tx, Guid.NewGuid(), root.Path, Hash("manifest"), "test", TransactionPhase.Applying,
            [new(FileChangeKind.Add, "mods/new.jar", null, Hash("new"), 3, TransactionOperationState.Applied)], DateTimeOffset.UtcNow);
        await journalStore.SaveAsync(journal, default);
        var recovery = new RecoveryService(appPaths, journalStore, new SafePathResolver(), new HashService());
        var result = await recovery.RollbackAsync(tx, default);
        Assert.True(result.Succeeded); Assert.False(File.Exists(Path.Combine(root.Path, "mods", "new.jar")));
    }

    [Fact]
    public async Task DiagnosticsRedactsAbsoluteRoots()
    {
        using var app = new TemporaryDirectory(); using var root = new TemporaryDirectory();
        var paths = new ApplicationPaths(app.Path); var config = new JsonConfigurationStore(paths);
        await config.SaveAsync(ClientConfiguration.Empty with { Instances = [Instance("Private", root.Path, root.Path + "-source")] }, default);
        var output = await new DiagnosticsService(paths, config).ExportAsync(default);
        using var archive = System.IO.Compression.ZipFile.OpenRead(output);
        var entry = archive.GetEntry("sanitized-config.json")!; using var reader = new StreamReader(entry.Open()); var json = await reader.ReadToEndAsync();
        Assert.DoesNotContain(root.Path, json, StringComparison.OrdinalIgnoreCase); Assert.Contains("redacted", json, StringComparison.OrdinalIgnoreCase);
    }

    private static MinecraftInstance Instance(string name, string root, string source) => new() { Id = Guid.NewGuid(), DisplayName = name, Location = new(root, InstanceOwnership.ManagedExternal), Runtime = new(), Pack = new(new LocalFolderSourceSettings(source)), Launch = LaunchConfiguration.Default };
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void CreateSevenZip(string path, params (string Path, string Content)[] files)
    {
        using var output = File.Create(path);
        using var writer = WriterFactory.OpenWriter(output, ArchiveType.SevenZip, new SevenZipWriterOptions(CompressionType.LZMA2));
        foreach (var file in files)
        {
            using var content = new MemoryStream(Encoding.UTF8.GetBytes(file.Content));
            writer.Write(file.Path, content, DateTime.UtcNow);
        }
    }

    private sealed class RecordingJournalStore : ITransactionJournalStore
    {
        public int SaveCount { get; private set; }
        public Task SaveAsync(TransactionJournal journal, CancellationToken cancellationToken) { SaveCount++; return Task.CompletedTask; }
        public Task<IReadOnlyList<TransactionJournal>> FindIncompleteAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TransactionJournal>>([]);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mm-integration-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
    public string Path { get; }
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}
