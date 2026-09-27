using MinecraftManager.Server.Domain.Common;
using MinecraftManager.Server.Domain.Entities;
using MinecraftManager.Server.Contracts.V1;
using System.Text.Json;

namespace MinecraftManager.Server.UnitTests;

public sealed class DomainRulesTests
{
    [Theory]
    [InlineData("../../escape")]
    [InlineData("C:/Windows/file")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share")]
    [InlineData("mods\\bad.jar")]
    [InlineData("mods/../bad.jar")]
    [InlineData("mods/con")]
    public void Unsafe_manifest_paths_are_rejected(string path) => Assert.Throws<DomainRuleException>(() => new RelativeManifestPath(path));

    [Fact]
    public void Protected_scope_is_rejected() => Assert.Throws<DomainRuleException>(() => new ManagedScope("saves/"));

    [Fact]
    public void Canonical_manifest_is_stable_and_sorted()
    {
        var hash = new Sha256Digest(new string('a', 64));
        var first = ManifestCanonicalizer.Build("main", "1.0.0", null, [new("mods/")], [(Guid.Parse("00000000-0000-0000-0000-000000000002"), new("mods/z.jar"), 1, hash, null), (Guid.Parse("00000000-0000-0000-0000-000000000001"), new("mods/a.jar"), 1, hash, null)]);
        var second = ManifestCanonicalizer.Build("main", "1.0.0", null, [new("mods/")], [(Guid.Parse("00000000-0000-0000-0000-000000000001"), new("mods/a.jar"), 1, hash, null), (Guid.Parse("00000000-0000-0000-0000-000000000002"), new("mods/z.jar"), 1, hash, null)]);
        Assert.Equal(first.Digest, second.Digest); Assert.Equal(first.Bytes, second.Bytes);
    }

    [Fact]
    public void Published_version_cannot_publish_twice()
    {
        var version = new PackVersion { Version = "1.0.0", NormalizedVersion = "1.0.0" };
        version.Publish([], new(new string('a', 64)), DateTimeOffset.UtcNow);
        Assert.Throws<DomainRuleException>(() => version.Publish([], new(new string('a', 64)), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Deployment_reports_are_monotonic()
    {
        var target = new DeploymentTarget();
        Assert.True(target.Report(0, DeploymentTargetStatus.Seen, null, DateTimeOffset.UtcNow));
        Assert.False(target.Report(0, DeploymentTargetStatus.Seen, null, DateTimeOffset.UtcNow));
        Assert.True(target.Report(1, DeploymentTargetStatus.AwaitingUserConfirmation, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Manifest_v1_uses_the_shared_wire_names()
    {
        var manifest = new ManifestV1(1, "main", "1.2.3", "1.0.0", ["mods/"], [], null);
        var json = JsonSerializer.Serialize(manifest, ContractJson.Options);
        Assert.Contains("\"schemaVersion\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"packVersion\":\"1.2.3\"", json, StringComparison.Ordinal);
        Assert.Contains("\"managedPaths\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PackVersion", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Custom_url_policy_blocks_loopback()
    {
        var policy = new MinecraftManager.Server.Infrastructure.Imports.RemoteUrlPolicy();
        var error = await Assert.ThrowsAsync<DomainRuleException>(() => policy.ValidateAsync(new Uri("http://127.0.0.1/private"), CancellationToken.None));
        Assert.Equal("import_url_blocked", error.Code);
    }
}
