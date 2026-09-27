using System.Text.Json;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Updates;

namespace MinecraftManager.Infrastructure.Persistence;

public sealed class ApplicationPaths : IApplicationPaths
{
    public ApplicationPaths(string? overrideDirectory = null)
    {
        DataDirectory = overrideDirectory ?? GetDefaultDataDirectory();
        ConfigurationFile = Path.Combine(DataDirectory, "config.json");
        InstanceStateDirectory = Path.Combine(DataDirectory, "state", "instances");
        TransactionDirectory = Path.Combine(DataDirectory, "transactions");
        HistoryDirectory = Path.Combine(DataDirectory, "history");
        LogDirectory = Path.Combine(DataDirectory, "logs");
        CacheDirectory = Path.Combine(DataDirectory, "cache");
        SharedAssetsDirectory = Path.Combine(DataDirectory, "shared", "assets");
        SharedLibrariesDirectory = Path.Combine(DataDirectory, "shared", "libraries");
        SharedVersionsDirectory = Path.Combine(DataDirectory, "shared", "versions");
        RuntimeStateDirectory = Path.Combine(DataDirectory, "runtime-state");
        NativeWorkDirectory = Path.Combine(DataDirectory, "native-work");
        OfflineProfilesFile = Path.Combine(DataDirectory, "profiles", "offline-profiles.json");
    }
    public string DataDirectory { get; }
    public string ConfigurationFile { get; }
    public string InstanceStateDirectory { get; }
    public string TransactionDirectory { get; }
    public string HistoryDirectory { get; }
    public string LogDirectory { get; }
    public string CacheDirectory { get; }
    public string SharedAssetsDirectory { get; }
    public string SharedLibrariesDirectory { get; }
    public string SharedVersionsDirectory { get; }
    public string RuntimeStateDirectory { get; }
    public string NativeWorkDirectory { get; }
    public string OfflineProfilesFile { get; }

    private static string GetDefaultDataDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdg)) return Path.Combine(xdg, "minecraft-manager");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MinecraftManager");
    }
}

internal static class AtomicJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            if (File.Exists(path)) File.Move(path, path + ".bak", true);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return default;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken);
    }
}

public sealed class JsonConfigurationStore(IApplicationPaths paths) : IConfigurationStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<ClientConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await LoadAndMigrateAsync(paths.ConfigurationFile, cancellationToken);
            if (loaded is not null) return loaded;
            var backupPath = paths.ConfigurationFile + ".bak";
            return await LoadAndMigrateAsync(backupPath, cancellationToken) ?? ClientConfiguration.Empty;
        }
        catch (JsonException)
        {
            var backup = paths.ConfigurationFile + ".bak";
            return await LoadAndMigrateAsync(backup, cancellationToken) ?? throw new InvalidDataException("Configuration and backup are unreadable.");
        }
    }
    public async Task SaveAsync(ClientConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration.SchemaVersion != 2) throw new InvalidDataException("Unsupported configuration schema.");
        await gate.WaitAsync(cancellationToken);
        try { await AtomicJson.WriteAsync(paths.ConfigurationFile, configuration, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task<ClientConfiguration?> LoadAndMigrateAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var document = await AtomicJson.ReadAsync<JsonDocument>(path, cancellationToken);
        if (document is null) return null;
        using (document)
        {
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schema))
                throw new InvalidDataException("Configuration schema version is missing.");
            if (schema.GetInt32() == 2)
                return document.RootElement.Deserialize<ClientConfiguration>(AtomicJson.Options)
                    ?? throw new InvalidDataException("Configuration is empty.");
            if (schema.GetInt32() != 1) throw new InvalidDataException("Unsupported configuration schema.");
            var legacy = document.RootElement.Deserialize<LegacyConfiguration>(AtomicJson.Options)
                ?? throw new InvalidDataException("Legacy configuration is empty.");
            var migrated = new ClientConfiguration(2,
                legacy.Instances.Select(x => new MinecraftInstance
                {
                    Id = x.Id,
                    DisplayName = x.DisplayName,
                    Location = new(Path.GetFullPath(x.RootPath), InstanceOwnership.ManagedExternal),
                    Runtime = new(),
                    Pack = new(x.Source),
                    Launch = LaunchConfiguration.Default,
                    CreatedAtUtc = x.CreatedAtUtc
                }).ToArray(),
                legacy.ServerProfiles,
                legacy.Settings);
            if (Path.GetFullPath(path).Equals(Path.GetFullPath(paths.ConfigurationFile), StringComparison.OrdinalIgnoreCase))
                await AtomicJson.WriteAsync(paths.ConfigurationFile, migrated, cancellationToken);
            return migrated;
        }
    }

    private sealed record LegacyConfiguration(
        int SchemaVersion,
        IReadOnlyList<LegacyInstance> Instances,
        IReadOnlyList<ServerProfile> ServerProfiles,
        ApplicationSettings Settings);

    private sealed record LegacyInstance(
        Guid Id,
        string DisplayName,
        string RootPath,
        UpdateSourceSettings Source,
        DateTimeOffset CreatedAtUtc);
}

public sealed class JsonInstanceStateStore(IApplicationPaths paths) : IInstanceStateStore
{
    public Task<InstanceState> LoadAsync(Guid id, CancellationToken ct) => LoadCoreAsync(id, ct);
    private async Task<InstanceState> LoadCoreAsync(Guid id, CancellationToken ct) =>
        await AtomicJson.ReadAsync<InstanceState>(Path.Combine(paths.InstanceStateDirectory, id.ToString("N") + ".json"), ct) ?? new(null, []);
    public Task SaveAsync(Guid id, InstanceState state, CancellationToken ct) =>
        AtomicJson.WriteAsync(Path.Combine(paths.InstanceStateDirectory, id.ToString("N") + ".json"), state, ct);
}

public sealed class JsonTransactionJournalStore(IApplicationPaths paths) : ITransactionJournalStore
{
    public Task SaveAsync(TransactionJournal journal, CancellationToken ct) => AtomicJson.WriteAsync(GetPath(journal.TransactionId), journal, ct);
    public async Task<IReadOnlyList<TransactionJournal>> FindIncompleteAsync(CancellationToken ct)
    {
        if (!Directory.Exists(paths.TransactionDirectory)) return [];
        var journals = new List<TransactionJournal>();
        foreach (var file in Directory.EnumerateFiles(paths.TransactionDirectory, "journal.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var journal = await AtomicJson.ReadAsync<TransactionJournal>(file, ct);
            if (journal is not null && journal.Phase is not TransactionPhase.Committed and not TransactionPhase.RolledBack) journals.Add(journal);
        }
        return journals;
    }
    private string GetPath(Guid id) => Path.Combine(paths.TransactionDirectory, id.ToString("N"), "journal.json");
}

public sealed class JsonHistoryStore(IApplicationPaths paths) : IHistoryStore
{
    public async Task AppendAsync(UpdateHistoryEntry entry, CancellationToken ct)
    {
        var current = (await GetAsync(entry.InstanceId, 99, ct)).ToList();
        current.Insert(0, entry);
        await AtomicJson.WriteAsync(GetPath(entry.InstanceId), current, ct);
    }
    public async Task<IReadOnlyList<UpdateHistoryEntry>> GetAsync(Guid id, int maximum, CancellationToken ct)
    {
        var items = await AtomicJson.ReadAsync<List<UpdateHistoryEntry>>(GetPath(id), ct) ?? [];
        return items.Take(Math.Clamp(maximum, 1, 100)).ToArray();
    }
    private string GetPath(Guid id) => Path.Combine(paths.HistoryDirectory, id.ToString("N") + ".json");
}
