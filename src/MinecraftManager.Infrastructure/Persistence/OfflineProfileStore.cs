using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Profiles;

namespace MinecraftManager.Infrastructure.Persistence;

public sealed class JsonOfflineProfileStore(IApplicationPaths paths) : IOfflineProfileStore
{
    private sealed record Document(int SchemaVersion, IReadOnlyList<OfflinePlayerProfile> Profiles);
    public async Task<IReadOnlyList<OfflinePlayerProfile>> LoadAsync(CancellationToken cancellationToken)
    {
        var document = await AtomicJson.ReadAsync<Document>(paths.OfflineProfilesFile, cancellationToken);
        if (document is null) return [];
        if (document.SchemaVersion != 1) throw new InvalidDataException("Unsupported offline profile schema.");
        return document.Profiles;
    }
    public Task SaveAsync(IReadOnlyList<OfflinePlayerProfile> profiles, CancellationToken cancellationToken) =>
        AtomicJson.WriteAsync(paths.OfflineProfilesFile, new Document(1, profiles), cancellationToken);
}
