using System.Text;
using MinecraftManager.Core.Manifests;

namespace MinecraftManager.Core.Tests;

public sealed class ManifestTests
{
    [Fact]
    public async Task ParsesSharedSchema()
    {
        const string json = """
        {"schemaVersion":1,"packId":"main","packVersion":"1.2.0","minimumClientVersion":"1.0.0","managedPaths":["mods/"],"files":[{"path":"mods/a.jar","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":3}]}
        """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await new ManifestParser().ParseAsync(stream, new Version(1, 0), CancellationToken.None);
        Assert.True(result.IsValid);
        Assert.Equal("1.2.0", result.Manifest!.Value.PackVersion);
        Assert.Equal(64, result.Manifest.CanonicalSha256.Length);
    }

    [Fact]
    public async Task RejectsUnknownFields()
    {
        const string json = """{"schemaVersion":1,"packId":"main","packVersion":"1.0","managedPaths":[],"files":[],"command":"calc"}""";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await new ManifestParser().ParseAsync(stream, new Version(1, 0), CancellationToken.None);
        Assert.Contains(result.Errors, x => x.Code == "manifest_json_invalid");
    }

    [Fact]
    public void RejectsCaseFoldedCollision()
    {
        var manifest = new PackManifest
        {
            SchemaVersion = 1,
            PackId = "p",
            PackVersion = "1",
            ManagedPaths = ["mods/"],
            Files =
        [
            new() { Path = "mods/A.jar", Size = 0, Sha256 = new string('a', 64) },
            new() { Path = "mods/a.jar", Size = 0, Sha256 = new string('b', 64) }
        ]
        };
        Assert.Contains(new ManifestParser().Validate(manifest, new Version(1, 0)).Errors, x => x.Code == "duplicate_path");
    }

    [Fact]
    public void RejectsNewerMinimumClient()
    {
        var manifest = new PackManifest { SchemaVersion = 1, PackId = "p", PackVersion = "1", MinimumClientVersion = "2.0", ManagedPaths = [], Files = [] };
        Assert.Contains(new ManifestParser().Validate(manifest, new Version(1, 0)).Errors, x => x.Code == "client_version_unsupported");
    }

    [Fact]
    public async Task ParsesManifestWithModLoaderDeclaration()
    {
        const string json = """
        {"schemaVersion":2,"packId":"main","packVersion":"1.0","minecraftVersion":"1.21.1","modLoader":{"type":"NeoForge","version":"21.1.101"},"managedPaths":["mods/"],"files":[]}
        """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await new ManifestParser().ParseAsync(stream, new Version(1, 0), CancellationToken.None);
        Assert.True(result.IsValid);
        Assert.Equal("1.21.1", result.Manifest!.Value.MinecraftVersion);
        Assert.NotNull(result.Manifest.Value.ModLoader);
        Assert.Equal("NeoForge", result.Manifest.Value.ModLoader!.Type);
        Assert.Equal("21.1.101", result.Manifest.Value.ModLoader.Version);
    }

    [Fact]
    public void RejectsUnknownModLoaderType()
    {
        var manifest = new PackManifest
        {
            SchemaVersion = 2,
            PackId = "p",
            PackVersion = "1",
            ModLoader = new("Wizard", "1.0"),
            ManagedPaths = [],
            Files = []
        };
        Assert.Contains(new ManifestParser().Validate(manifest, new Version(1, 0)).Errors, x => x.Code == "invalid_mod_loader");
    }
}
