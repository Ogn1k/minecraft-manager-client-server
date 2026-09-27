using System.Collections.Concurrent;
using System.Diagnostics;
using MinecraftManager.Core.Launching;
using MinecraftManager.Core.Persistence;

namespace MinecraftManager.Infrastructure.Launching;

public sealed class GameProcessMonitor(IApplicationPaths paths) : IGameProcessMonitor, IDisposable
{
    private readonly ConcurrentDictionary<Guid, Process> processes = new();
    private readonly ConcurrentDictionary<Guid, GameProcessSnapshot> snapshots = new();
    public event EventHandler<GameProcessSnapshot>? Changed;
    public GameProcessSnapshot? Get(Guid instanceId) => snapshots.TryGetValue(instanceId, out var value) ? value : null;
    public bool IsRunning(Guid instanceId) => processes.TryGetValue(instanceId, out var process) && !process.HasExited;

    internal RunningGameProcess Register(Guid instanceId, Process process, string nativesDirectory)
    {
        if (!processes.TryAdd(instanceId, process)) { process.Kill(true); process.Dispose(); throw new InvalidOperationException("Minecraft is already running for this instance."); }
        var started = new GameProcessSnapshot(instanceId, process.Id, GameProcessState.Running, DateTimeOffset.UtcNow);
        Publish(started);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnExited(instanceId, process, nativesDirectory);
        _ = CaptureAsync(instanceId, process);
        if (process.HasExited) OnExited(instanceId, process, nativesDirectory);
        return new(instanceId, process.Id, started.StartedAtUtc!.Value);
    }

    public async Task<RequestStopResult> RequestStopAsync(Guid instanceId, StopConfirmation confirmation, CancellationToken cancellationToken)
    {
        if (!confirmation.Confirmed) return new(false, "Stopping Minecraft requires confirmation.");
        if (!processes.TryGetValue(instanceId, out var process) || process.HasExited) return new(false, "Minecraft is not running.");
        Publish(Get(instanceId)! with { State = GameProcessState.Stopping });
        try
        {
            if (!process.CloseMainWindow()) process.Kill(true);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return new(true);
        }
        catch (TimeoutException) { return new(false, "Minecraft did not stop within the timeout."); }
    }

    private async Task CaptureAsync(Guid instanceId, Process process)
    {
        try
        {
            Directory.CreateDirectory(paths.LogDirectory);
            var file = Path.Combine(paths.LogDirectory, $"game-{instanceId:N}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.log");
            await using var writer = new StreamWriter(new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920, true));
            var stdout = PumpAsync(process.StandardOutput, writer); var stderr = PumpAsync(process.StandardError, writer);
            await Task.WhenAll(stdout, stderr);
        }
        catch { /* Minecraft's own logs remain authoritative; capture is best effort. */ }
    }

    private static async Task PumpAsync(StreamReader reader, StreamWriter writer)
    {
        var lines = 0;
        while (lines++ < 100_000 && await reader.ReadLineAsync() is { } line)
            await writer.WriteLineAsync(line.Length > 16_384 ? line[..16_384] : line);
    }

    private void OnExited(Guid instanceId, Process process, string nativesDirectory)
    {
        if (!processes.TryGetValue(instanceId, out var current) || !ReferenceEquals(current, process) || !processes.TryRemove(instanceId, out _)) return;
        var code = process.ExitCode;
        Publish(new(instanceId, process.Id, code == 0 ? GameProcessState.Exited : GameProcessState.Failed, Get(instanceId)?.StartedAtUtc, code,
            code == 0 ? null : $"Minecraft exited unexpectedly with code {code}."));
        process.Dispose();
        TryDeleteNativeWork(nativesDirectory);
    }
    private void TryDeleteNativeWork(string directory)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.NativeWorkDirectory));
            var target = Path.GetFullPath(directory);
            if (target.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && Directory.Exists(target))
                Directory.Delete(target, true);
        }
        catch { /* Locked native files are cleaned by a later retention pass. */ }
    }
    private void Publish(GameProcessSnapshot snapshot) { snapshots[snapshot.InstanceId] = snapshot; Changed?.Invoke(this, snapshot); }
    public void Dispose() { foreach (var process in processes.Values) process.Dispose(); processes.Clear(); }
}

public sealed class LaunchExecutor(GameProcessMonitor monitor) : ILaunchExecutor
{
    public Task<RunningGameProcess> LaunchAsync(LaunchPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow - plan.CreatedAtUtc > TimeSpan.FromMinutes(2)) throw new InvalidOperationException("Launch plan has expired.");
        if (!File.Exists(plan.JavaExecutable)) throw new FileNotFoundException("Java executable is missing.");
        if (!Directory.Exists(plan.WorkingDirectory)) throw new DirectoryNotFoundException("Game directory is missing.");
        if (plan.JvmArguments.Concat(plan.GameArguments).Any(x => x.Any(char.IsControl))) throw new InvalidDataException("Launch arguments contain control characters.");
        var info = new ProcessStartInfo { FileName = Path.GetFullPath(plan.JavaExecutable), WorkingDirectory = Path.GetFullPath(plan.WorkingDirectory), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = false };
        foreach (var argument in plan.JvmArguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add(plan.MainClass);
        foreach (var argument in plan.GameArguments) info.ArgumentList.Add(argument);
        foreach (var variable in plan.EnvironmentVariables) info.Environment[variable.Key] = variable.Value;
        var process = new Process { StartInfo = info };
        if (!process.Start()) { process.Dispose(); throw new InvalidOperationException("Java process did not start."); }
        return Task.FromResult(monitor.Register(plan.InstanceId, process, plan.NativesDirectory));
    }
}
