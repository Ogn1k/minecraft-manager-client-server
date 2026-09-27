using MinecraftManager.Core.Filesystem;

namespace MinecraftManager.Core.Tests;

public sealed class SafePathTests
{
    [Theory]
    [InlineData("../outside")]
    [InlineData("../../file")]
    [InlineData("C:\\Windows\\file")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share")]
    [InlineData("mods//bad.jar")]
    [InlineData("mods/CON")]
    [InlineData("mods/file. ")]
    public void RejectsUnsafeRelativePaths(string input) => Assert.Throws<UnsafePathException>(() => RelativeManifestPath.Parse(input));

    [Theory]
    [InlineData("mods/example.jar")]
    [InlineData("config/example.json")]
    [InlineData("resourcepacks/example.zip")]
    public void AcceptsSafeRelativePaths(string input) => Assert.Equal(input, RelativeManifestPath.Parse(input).Value);

    [Fact]
    public void ManagedScopeRejectsProtectedContent() => Assert.Throws<UnsafePathException>(() => ManagedScope.Parse("saves/"));

    [Fact]
    public void ResolverPreventsPrefixEscape()
    {
        using var temp = new TemporaryDirectory();
        var root = TrustedRoot.Create(temp.Path);
        var result = new SafePathResolver().ResolveFile(root, RelativeManifestPath.Parse("mods/a.jar"));
        Assert.StartsWith(root.FullPath + System.IO.Path.DirectorySeparatorChar, result.FullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mm-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
    public string Path { get; }
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}
