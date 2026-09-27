using MinecraftManager.Core.Models;
using MinecraftManager.Core.Updates;

namespace MinecraftManager.Core.Persistence;

public interface IApplicationPaths
{
    string DataDirectory { get; }
    string ConfigurationFile { get; }
    string InstanceStateDirectory { get; }
    string TransactionDirectory { get; }
    string HistoryDirectory { get; }
    string LogDirectory { get; }
    string CacheDirectory { get; }
    string SharedAssetsDirectory { get; }
    string SharedLibrariesDirectory { get; }
    string SharedVersionsDirectory { get; }
    string RuntimeStateDirectory { get; }
    string NativeWorkDirectory { get; }
    string OfflineProfilesFile { get; }
}

public interface IConfigurationStore
{
    Task<ClientConfiguration> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(ClientConfiguration configuration, CancellationToken cancellationToken);
}

public interface IInstanceStateStore
{
    Task<InstanceState> LoadAsync(Guid instanceId, CancellationToken cancellationToken);
    Task SaveAsync(Guid instanceId, InstanceState state, CancellationToken cancellationToken);
}

public sealed record UpdateHistoryEntry(
    Guid TransactionId, Guid InstanceId, string PackId, string PackVersion,
    DateTimeOffset StartedAtUtc, DateTimeOffset FinishedAtUtc,
    int Added, int Replaced, int Deleted, string Source,
    bool Succeeded, string? ErrorCode, bool RolledBack);

public interface IHistoryStore
{
    Task AppendAsync(UpdateHistoryEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<UpdateHistoryEntry>> GetAsync(Guid instanceId, int maximum, CancellationToken cancellationToken);
}

public interface ITransactionJournalStore
{
    Task SaveAsync(TransactionJournal journal, CancellationToken cancellationToken);
    Task<IReadOnlyList<TransactionJournal>> FindIncompleteAsync(CancellationToken cancellationToken);
}
