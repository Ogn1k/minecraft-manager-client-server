using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using MinecraftManager.Core.Launching;
using MinecraftManager.Core.Runtime;
using MinecraftManager.Infrastructure.Launching;
using MinecraftManager.Infrastructure.Persistence;
using MinecraftManager.Infrastructure.Runtime;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Persistence;

namespace MinecraftManager.IntegrationTests;

public sealed class LauncherInfrastructureTests
{
    [Fact]
    public async Task RuntimeArtifactsAreVerifiedAndReused()
    {
        using var data = new TemporaryDirectory();
        var handler = new BytesHandler([]); using var http = new HttpClient(handler);
        var artifact = new RuntimeArtifact(new("https://libraries.minecraft.net/empty.jar"), "test/empty.jar", 0, Sha1([]));
        var version = new ResolvedMinecraftVersion("test", "Main", [new("empty", artifact)], null, [], [], [], 21);
        var manager = new MinecraftRuntimeManager(http, new ApplicationPaths(data.Path));

        Assert.True((await manager.RepairAsync(version, null, default)).Succeeded);
        Assert.True((await manager.RepairAsync(version, null, default)).Succeeded);
        Assert.Equal(1, handler.Requests);
        Assert.True((await manager.ValidateAsync(version, default)).IsReady);
    }

    [Fact]
    public async Task NativeExtractionRejectsTraversal()
    {
        using var data = new TemporaryDirectory();
        var archive = Zip(("../escape.dll", "bad"));
        var handler = new BytesHandler(archive); using var http = new HttpClient(handler);
        var artifact = new RuntimeArtifact(new("https://libraries.minecraft.net/native.jar"), "test/native.jar", archive.Length, Sha1(archive));
        var version = new ResolvedMinecraftVersion("test", "Main", [], null, [], [], [new("native", artifact, [])], 21);
        var manager = new MinecraftRuntimeManager(http, new ApplicationPaths(data.Path));
        Assert.True((await manager.RepairAsync(version, null, default)).Succeeded);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.PrepareNativesAsync(version, Guid.NewGuid(), default));
        Assert.False(File.Exists(Path.Combine(data.Path, "escape.dll")));
    }

    [Fact]
    public async Task LaunchExecutorStartsHarmlessProcessAndReportsExit()
    {
        using var data = new TemporaryDirectory();
        var executable = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe") : "/usr/bin/true";
        if (!File.Exists(executable)) return;
        using var monitor = new GameProcessMonitor(new ApplicationPaths(data.Path));
        var executor = new LaunchExecutor(monitor);
        var instanceId = Guid.NewGuid();
        var plan = new LaunchPlan(Guid.NewGuid(), instanceId, DateTimeOffset.UtcNow, executable,
            OperatingSystem.IsWindows() ? "cmd.exe" : "ignored", data.Path, data.Path, [], [], [], new Dictionary<string, string>());

        var running = await executor.LaunchAsync(plan, default);
        Assert.True(running.ProcessId > 0);
        for (var i = 0; i < 100 && monitor.Get(instanceId)?.State == GameProcessState.Running; i++) await Task.Delay(20);
        Assert.True(monitor.Get(instanceId)!.State is GameProcessState.Exited or GameProcessState.Failed);
    }

    [Fact]
    public async Task RequiredPackPolicyBlocksWhenAnAvailableVersionIsRecorded()
    {
        var id = Guid.NewGuid();
        var state = new InstanceState(new("pack", "1.0.0", new string('a', 64), DateTimeOffset.UtcNow), [], AvailablePackVersion: "2.0.0");
        var service = new PackLaunchStatusService(new MemoryStateStore(id, state));
        var instance = new MinecraftInstance
        {
            Id = id, DisplayName = "Managed", Location = new(Path.GetTempPath(), InstanceOwnership.ManagedExternal), Runtime = new(),
            Pack = new(new LocalFolderSourceSettings(Path.GetTempPath()), PackUpdatePolicy.RequiredBeforeLaunch), Launch = LaunchConfiguration.Default
        };
        Assert.Equal(PackLaunchState.RequiredUpdate, (await service.GetAsync(instance, default)).State);
    }

    private static string Sha1(byte[] value) => Convert.ToHexString(SHA1.HashData(value)).ToLowerInvariant();
    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var item in entries) { var entry = zip.CreateEntry(item.Name); using var writer = new StreamWriter(entry.Open()); writer.Write(item.Content); }
        return stream.ToArray();
    }
    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
    private sealed class MemoryStateStore(Guid id, InstanceState state) : IInstanceStateStore
    {
        public Task<InstanceState> LoadAsync(Guid instanceId, CancellationToken cancellationToken) => Task.FromResult(instanceId == id ? state : new InstanceState(null, []));
        public Task SaveAsync(Guid instanceId, InstanceState value, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
