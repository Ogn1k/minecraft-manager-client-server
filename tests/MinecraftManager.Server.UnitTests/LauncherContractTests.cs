using MinecraftManager.Server.Contracts.V1;

namespace MinecraftManager.Server.UnitTests;

public sealed class LauncherContractTests
{
    [Fact]
    public void RuntimePolicyContainsNoRemoteExecutionOrPlayerIdentityFields()
    {
        var names = typeof(ClientRuntimePolicyV1).GetProperties().Select(x => x.Name).ToArray();
        foreach (var forbidden in new[] { "Command", "Executable", "Path", "Arguments", "Environment", "Player", "Profile" })
            Assert.DoesNotContain(names, x => x.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RuntimePolicyIsStrictlyValidated()
    {
        Assert.Empty(ClientRuntimePolicyValidator.Validate(new(1, "1.21.1", "fabric", "0.16.0", "3.5.0", "requiredBeforeLaunch", "1.0.0", 21, 4096)));
        Assert.NotEmpty(ClientRuntimePolicyValidator.Validate(new(2, "", "unknown", "", null, "autoRun", null, 2, 1)));
    }
}
