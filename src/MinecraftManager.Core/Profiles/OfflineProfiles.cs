using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftManager.Core.Profiles;

public sealed record OfflinePlayerProfile(Guid Id, string DisplayName, Guid OfflinePlayerUuid, DateTimeOffset CreatedAtUtc);
public sealed record OfflineLaunchIdentity(string PlayerName, Guid PlayerUuid);
public sealed record ProfileValidationResult(bool IsValid, string? ErrorCode = null, string? Message = null);

public interface IOfflineProfileStore
{
    Task<IReadOnlyList<OfflinePlayerProfile>> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(IReadOnlyList<OfflinePlayerProfile> profiles, CancellationToken cancellationToken);
}

public interface IOfflineProfileService
{
    Task<IReadOnlyList<OfflinePlayerProfile>> GetAllAsync(CancellationToken cancellationToken);
    Task<OfflinePlayerProfile> AddAsync(string displayName, CancellationToken cancellationToken);
    Task<OfflinePlayerProfile> RenameAsync(Guid profileId, string displayName, bool identityChangeConfirmed, CancellationToken cancellationToken);
    Task RemoveAsync(Guid profileId, CancellationToken cancellationToken);
    Task<OfflineLaunchIdentity?> GetLaunchIdentityAsync(Guid profileId, CancellationToken cancellationToken);
}

public static partial class OfflineProfileRules
{
    [GeneratedRegex("^[A-Za-z0-9_]{1,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static ProfileValidationResult Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new(false, "profile_name_required", "A player name is required.");
        var trimmed = name.Trim();
        return NamePattern().IsMatch(trimmed)
            ? new(true)
            : new(false, "profile_name_invalid", "Use 1-16 letters, numbers, or underscores.");
    }

    public static Guid CreateOfflineUuid(string playerName)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return Guid.ParseExact($"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}", "D");
    }
}

public sealed class OfflineProfileService(IOfflineProfileStore store) : IOfflineProfileService
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public Task<IReadOnlyList<OfflinePlayerProfile>> GetAllAsync(CancellationToken cancellationToken) => store.LoadAsync(cancellationToken);

    public async Task<OfflinePlayerProfile> AddAsync(string displayName, CancellationToken cancellationToken)
    {
        var name = ValidateName(displayName);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = (await store.LoadAsync(cancellationToken)).ToList();
            if (profiles.Any(x => x.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("An offline profile with this name already exists.");
            var profile = new OfflinePlayerProfile(Guid.NewGuid(), name, OfflineProfileRules.CreateOfflineUuid(name), DateTimeOffset.UtcNow);
            profiles.Add(profile);
            await store.SaveAsync(profiles.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken);
            return profile;
        }
        finally { gate.Release(); }
    }

    public async Task<OfflinePlayerProfile> RenameAsync(Guid profileId, string displayName, bool identityChangeConfirmed, CancellationToken cancellationToken)
    {
        if (!identityChangeConfirmed) throw new InvalidOperationException("Renaming changes the offline player UUID and requires confirmation.");
        var name = ValidateName(displayName);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = (await store.LoadAsync(cancellationToken)).ToList();
            var index = profiles.FindIndex(x => x.Id == profileId);
            if (index < 0) throw new KeyNotFoundException("Offline profile was not found.");
            if (profiles.Any(x => x.Id != profileId && x.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("An offline profile with this name already exists.");
            profiles[index] = profiles[index] with { DisplayName = name, OfflinePlayerUuid = OfflineProfileRules.CreateOfflineUuid(name) };
            await store.SaveAsync(profiles, cancellationToken);
            return profiles[index];
        }
        finally { gate.Release(); }
    }

    public async Task RemoveAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = await store.LoadAsync(cancellationToken);
            await store.SaveAsync(profiles.Where(x => x.Id != profileId).ToArray(), cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<OfflineLaunchIdentity?> GetLaunchIdentityAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var profile = (await store.LoadAsync(cancellationToken)).SingleOrDefault(x => x.Id == profileId);
        return profile is null ? null : new(profile.DisplayName, profile.OfflinePlayerUuid);
    }

    private static string ValidateName(string value)
    {
        var result = OfflineProfileRules.Validate(value);
        if (!result.IsValid) throw new ArgumentException(result.Message, nameof(value));
        return value.Trim();
    }
}
