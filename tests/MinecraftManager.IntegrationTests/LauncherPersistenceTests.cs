using System.Text.Json;
using MinecraftManager.Core.Profiles;
using MinecraftManager.Infrastructure.Persistence;

namespace MinecraftManager.IntegrationTests;

public sealed class LauncherPersistenceTests
{
    [Fact]
    public async Task LegacyConfigurationMigratesWithoutChangingGameDirectory()
    {
        using var data = new TemporaryDirectory(); using var game = new TemporaryDirectory(); using var source = new TemporaryDirectory();
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            instances = new[] { new { id, displayName = "Legacy", rootPath = game.Path, source = new { type = "localFolder", folderPath = source.Path }, createdAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z") } },
            serverProfiles = Array.Empty<object>(),
            settings = new { theme = 0, maxConcurrentDownloads = 4, downloadTimeoutSeconds = 60, backupRetentionCount = 3, backupRetentionDays = 14, checkOnStartup = true, language = 0 }
        });
        await File.WriteAllTextAsync(Path.Combine(data.Path, "config.json"), json);
        var marker = Path.Combine(game.Path, "save.txt"); await File.WriteAllTextAsync(marker, "unchanged");

        var loaded = await new JsonConfigurationStore(new ApplicationPaths(data.Path)).LoadAsync(default);

        Assert.Equal(2, loaded.SchemaVersion);
        var instance = Assert.Single(loaded.Instances);
        Assert.Equal(id, instance.Id);
        Assert.Equal(Core.Models.InstanceOwnership.ManagedExternal, instance.Location.Ownership);
        Assert.False(instance.Runtime.IsConfigured);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(marker));
        Assert.True(File.Exists(Path.Combine(data.Path, "config.json.bak")));
    }

    [Fact]
    public async Task OfflineProfilesPersistWithoutCredentials()
    {
        using var data = new TemporaryDirectory();
        var service = new OfflineProfileService(new JsonOfflineProfileStore(new ApplicationPaths(data.Path)));
        var profile = await service.AddAsync("Player_1", default);
        var identity = await service.GetLaunchIdentityAsync(profile.Id, default);
        Assert.Equal(profile.OfflinePlayerUuid, identity!.PlayerUuid);
        var json = await File.ReadAllTextAsync(Path.Combine(data.Path, "profiles", "offline-profiles.json"));
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }
}
