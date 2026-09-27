using System.Reflection;
using MinecraftManager.Core.Models;

namespace MinecraftManager.Core.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void CoreHasNoPresentationServerOrInfrastructureReferences()
    {
        var names = typeof(MinecraftInstance).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToArray();
        Assert.DoesNotContain(names, x => x is not null &&
            (x.StartsWith("Avalonia", StringComparison.Ordinal) ||
             x.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
             x.Contains("SignalR", StringComparison.Ordinal) ||
             x == "MinecraftManager.Infrastructure"));
    }

    [Fact]
    public void ServerProjectsDoNotReferenceClientInfrastructure()
    {
        var solutionRoot = FindSolutionRoot();
        foreach (var project in Directory.EnumerateFiles(Path.Combine(solutionRoot, "src"), "MinecraftManager.Server.*.csproj", SearchOption.AllDirectories))
        {
            var xml = File.ReadAllText(project);
            Assert.DoesNotContain("MinecraftManager.Infrastructure", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("MinecraftManager.App", xml, StringComparison.Ordinal);
        }
    }

    private static string FindSolutionRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current, "MinecraftManager.slnx")))
            current = Directory.GetParent(current)?.FullName;
        return current ?? throw new InvalidOperationException("Solution root was not found.");
    }
}
