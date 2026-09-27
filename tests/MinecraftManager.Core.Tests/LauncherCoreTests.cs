using MinecraftManager.Core.Launching;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Profiles;
using MinecraftManager.Core.Runtime;
using MinecraftManager.Core.Services;

namespace MinecraftManager.Core.Tests;

public sealed class LauncherCoreTests
{
    [Fact]
    public void OfflineUuidMatchesMinecraftCanonicalVector()
    {
        Assert.Equal(Guid.Parse("b50ad385-829d-3141-a216-7e7d7539ba7f"), OfflineProfileRules.CreateOfflineUuid("Notch"));
    }

    [Theory]
    [InlineData("Player", true)]
    [InlineData("a_b9", true)]
    [InlineData("", false)]
    [InlineData("player with space", false)]
    [InlineData("abcdefghijklmnopq", false)]
    public void OfflineNamesAreConservativelyValidated(string name, bool valid) => Assert.Equal(valid, OfflineProfileRules.Validate(name).IsValid);

    [Fact]
    public void JavaPolicyRequiresExactMajorAndArchitecture()
    {
        var policy = new JavaCompatibilityPolicy();
        var platform = new RuntimePlatform(RuntimeOperatingSystem.Windows, CpuArchitecture.X64);
        Assert.True(policy.Validate(new("java", 21, "test", CpuArchitecture.X64, JavaRuntimeOrigin.Custom), new(21), platform).IsCompatible);
        Assert.False(policy.Validate(new("java", 17, "test", CpuArchitecture.X64, JavaRuntimeOrigin.Custom), new(21), platform).IsCompatible);
        Assert.False(policy.Validate(new("java", 21, "test", CpuArchitecture.Arm64, JavaRuntimeOrigin.Custom), new(21), platform).IsCompatible);
    }

    [Fact]
    public void LaunchPlannerExpandsOfflineIdentityAndRejectsOwnedFlags()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mm-launch-test"));
        var instance = new MinecraftInstance
        {
            Id = Guid.NewGuid(), DisplayName = "Test", Location = new(root, InstanceOwnership.ApplicationOwned),
            Runtime = new("1.21.1", Java: new(21)), Pack = null,
            Launch = new(new(), new(1024, 4096), new(), ["-Dexample=true"])
        };
        var artifact = new RuntimeArtifact(new("https://libraries.minecraft.net/library.jar"), "org/test/library.jar", 1, "00");
        var runtime = new ResolvedMinecraftVersion("1.21.1", "example.Main", [new("test", artifact)], null,
            [new("-Djava.library.path=${natives_directory}"), new("-p"), new("${library_directory}/cpw/mods/securejarhandler.jar"), new("-cp"), new("${classpath}")],
            [new("--username"), new("${auth_player_name}"), new("--uuid"), new("${auth_uuid}")], [], 21);
        var identity = new OfflineLaunchIdentity("Player", OfflineProfileRules.CreateOfflineUuid("Player"));

        var plan = new LaunchPlanner().CreatePlan(instance, runtime, new("java", 21, "test", CpuArchitecture.X64, JavaRuntimeOrigin.Custom), identity,
            Path.Combine(root, "shared", "assets"), Path.Combine(root, "natives"));

        Assert.Contains("Player", plan.GameArguments);
        Assert.Contains(identity.PlayerUuid.ToString("N"), plan.GameArguments);
        Assert.Contains("-Xmx4096M", plan.JvmArguments);
        Assert.Contains(plan.JvmArguments, x => x.Replace('\\', '/').Contains("shared/libraries/cpw/mods/securejarhandler.jar", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.JvmArguments.Concat(plan.GameArguments), x => x.Contains("${", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => LaunchPlanner.ValidateUserArgument("-cp=evil"));
        Assert.Throws<ArgumentException>(() => LaunchPlanner.ValidateUserArgument("@args.txt"));
    }

    [Fact]
    public async Task VersionResolverMergesParentAndEvaluatesOsRules()
    {
        var source = new MemoryMetadata(new Dictionary<string, string>
        {
            ["base"] = """{"id":"base","mainClass":"base.Main","javaVersion":{"majorVersion":17},"libraries":[{"name":"all","downloads":{"artifact":{"url":"https://libraries.minecraft.net/all.jar","path":"a/all.jar","sha1":"00","size":1}}}]}""",
            ["child"] = """{"id":"child","inheritsFrom":"base","mainClass":"child.Main","arguments":{"game":["--demo"]},"libraries":[{"name":"win","rules":[{"action":"allow","os":{"name":"windows"}}],"downloads":{"artifact":{"url":"https://libraries.minecraft.net/win.jar","path":"a/win.jar","sha1":"00","size":1}}}]}"""
        });
        var result = await new MinecraftVersionResolver(source).ResolveAsync(new("child"), new(RuntimeOperatingSystem.Windows, CpuArchitecture.X64), default);
        Assert.Equal("child.Main", result.MainClass);
        Assert.Equal(2, result.Libraries.Count);
        Assert.Equal(17, result.RequiredJavaMajor);
        Assert.Contains(result.GameArguments, x => x.Value == "--demo");
    }

    [Theory]
    [InlineData(ModLoaderType.Fabric, "1.21.1-fabric-0.16.0")]
    [InlineData(ModLoaderType.Quilt, "1.21.1-quilt-0.27.0")]
    public void LoaderRegistryUsesStableInstalledProfileIds(ModLoaderType type, string expected)
    {
        var registry = new ModLoaderRuntimeRegistry([new ConventionModLoaderRuntimeProvider(type)]);
        Assert.Equal(expected, registry.Resolve("1.21.1", new(type, type == ModLoaderType.Fabric ? "0.16.0" : "0.27.0")).ResolvedVersionId);
    }

    [Fact]
    public void SelectedInstanceContextPublishesOnlyRealChanges()
    {
        var context = new SelectedInstanceContext(); var events = new List<SelectedInstanceChangedEventArgs>();
        context.Changed += (_, value) => events.Add(value);
        var id = Guid.NewGuid(); context.Select(id); context.Select(id); context.Select(null);
        Assert.Equal(2, events.Count);
        Assert.Equal(id, events[0].CurrentId);
        Assert.Null(events[1].CurrentId);
    }

    private sealed class MemoryMetadata(IReadOnlyDictionary<string, string> values) : IRuntimeMetadataSource
    {
        public Task<string?> GetVersionJsonAsync(string versionId, CancellationToken cancellationToken) => Task.FromResult(values.GetValueOrDefault(versionId));
    }
}
