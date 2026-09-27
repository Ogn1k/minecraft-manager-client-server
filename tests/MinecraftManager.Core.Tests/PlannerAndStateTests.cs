using MinecraftManager.Core.Filesystem;
using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Updates;

namespace MinecraftManager.Core.Tests;

public sealed class PlannerAndStateTests
{
    [Fact]
    public async Task PlannerAddsReplacesDeletesAndPreservesUnmanaged()
    {
        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "mods"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "mods", "replace.jar"), "old");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "mods", "delete.jar"), "delete");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "options.txt"), "keep");
        var hashes = new HashService();
        var oldReplace = await hashes.ComputeFileSha256Async(Path.Combine(temp.Path, "mods", "replace.jar"), default);
        var oldDelete = await hashes.ComputeFileSha256Async(Path.Combine(temp.Path, "mods", "delete.jar"), default);
        var manifest = new PackManifest
        {
            SchemaVersion = 1,
            PackId = "p",
            PackVersion = "2",
            ManagedPaths = ["mods/"],
            Files =
        [
            new() { Path = "mods/add.jar", Size = 3, Sha256 = Hash("add") },
            new() { Path = "mods/replace.jar", Size = 3, Sha256 = Hash("new") }
        ]
        };
        var validated = new ManifestParser().Validate(manifest, new Version(1, 0)).Manifest!;
        var instance = new MinecraftInstance { Id = Guid.NewGuid(), DisplayName = "Test", Location = new(temp.Path, InstanceOwnership.ManagedExternal), Runtime = new(), Pack = new(new LocalFolderSourceSettings(temp.Path + "-source")), Launch = LaunchConfiguration.Default };
        var state = new InstanceState(null, [new("mods/replace.jar", oldReplace, 3), new("mods/delete.jar", oldDelete, 6)]);
        var plan = await new UpdatePlanner(new SafePathResolver(), hashes).CreatePlanAsync(instance, state, validated, default);
        Assert.Single(plan.FilesToAdd); Assert.Single(plan.FilesToReplace); Assert.Single(plan.FilesToDelete);
        Assert.DoesNotContain(plan.FilesToDelete, x => x.Path == "options.txt");
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(temp.Path, "options.txt")));
    }

    [Fact]
    public void StateMachineRejectsInvalidTransition()
    {
        var machine = new UpdateStateMachine();
        Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(UpdateStage.Applying));
        machine.TransitionTo(UpdateStage.Checking); machine.TransitionTo(UpdateStage.Planning); machine.TransitionTo(UpdateStage.AwaitingConfirmation);
        Assert.Equal(UpdateStage.AwaitingConfirmation, machine.Current);
    }

    private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
