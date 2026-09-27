using MinecraftManager.Core.Models;
using MinecraftManager.Core.Profiles;
using MinecraftManager.Core.Runtime;

namespace MinecraftManager.Core.Launching;

public enum PackLaunchState { None, Current, UpdateAvailable, RequiredUpdate, NotInstalled, Corrupt }
public sealed record PackLaunchStatus(PackLaunchState State, string? InstalledVersion = null, string? AvailableVersion = null);
public interface IPackLaunchStatusService { Task<PackLaunchStatus> GetAsync(MinecraftInstance instance, CancellationToken cancellationToken); }

public enum LaunchIssueSeverity { Information, Warning, Blocking }
public enum LaunchIssueCode
{
    RuntimeNotConfigured, MissingVersionMetadata, MissingLibraries, MissingAssets, MissingNatives,
    MissingJava, IncompatibleJava, OfflineProfileRequired, InvalidOfflineProfile, MissingGameDirectory,
    InvalidModLoader, PackNotInstalled, PackCorrupt, PackUpdateAvailable, RequiredPackUpdate, AlreadyRunning
}
public enum LaunchRemediation { None, ConfigureRuntime, RepairRuntime, SelectJava, ManageProfiles, ReviewUpdate, OpenInstance }
public sealed record LaunchIssue(LaunchIssueCode Code, LaunchIssueSeverity Severity, string Message, LaunchRemediation Remediation);
public sealed record LaunchReadiness(bool CanLaunch, IReadOnlyList<LaunchIssue> Issues)
{
    public static LaunchReadiness From(IEnumerable<LaunchIssue> issues)
    {
        var values = issues.ToArray();
        return new(!values.Any(x => x.Severity == LaunchIssueSeverity.Blocking), values);
    }
}

public sealed record LaunchPlan(
    Guid LaunchId,
    Guid InstanceId,
    DateTimeOffset CreatedAtUtc,
    string JavaExecutable,
    string MainClass,
    string WorkingDirectory,
    string NativesDirectory,
    IReadOnlyList<string> JvmArguments,
    IReadOnlyList<string> GameArguments,
    IReadOnlyList<string> ClasspathEntries,
    IReadOnlyDictionary<string, string> EnvironmentVariables);

public interface ILaunchPlanner
{
    LaunchPlan CreatePlan(MinecraftInstance instance, ResolvedMinecraftVersion runtime, JavaRuntime java, OfflineLaunchIdentity identity, string assetsDirectory, string nativesDirectory);
}

public sealed class LaunchPlanner : ILaunchPlanner
{
    private static readonly string[] ForbiddenUserPrefixes = ["-cp", "-classpath", "-Djava.library.path", "-javaagent", "@", "-Xms", "-Xmx"];

    public LaunchPlan CreatePlan(MinecraftInstance instance, ResolvedMinecraftVersion runtime, JavaRuntime java, OfflineLaunchIdentity identity, string assetsDirectory, string nativesDirectory)
    {
        if (instance.Id == Guid.Empty) throw new ArgumentException("Instance ID is invalid.");
        var working = Path.GetFullPath(instance.Location.GameDirectory);
        var classpath = runtime.Libraries.Select(x => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(assetsDirectory)!, "libraries", x.Artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)))).ToList();
        if (runtime.ClientJar is not null) classpath.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(assetsDirectory)!, "versions", runtime.Id, runtime.Id + ".jar")));
        var requiredJvm = runtime.JvmArguments.Select(x => Expand(x.Value, instance, runtime, identity, assetsDirectory, nativesDirectory, classpath)).ToList();
        requiredJvm.Add($"-Xms{instance.Launch.Memory.MinimumMb}M");
        requiredJvm.Add($"-Xmx{instance.Launch.Memory.MaximumMb}M");
        foreach (var argument in instance.Launch.AdditionalJvmArguments)
        {
            ValidateUserArgument(argument);
            requiredJvm.Add(argument);
        }
        var game = runtime.GameArguments.Select(x => Expand(x.Value, instance, runtime, identity, assetsDirectory, nativesDirectory, classpath)).ToList();
        AddWindowArguments(instance.Launch.Window, game);
        return new(Guid.NewGuid(), instance.Id, DateTimeOffset.UtcNow, Path.GetFullPath(java.ExecutablePath), runtime.MainClass, working,
            Path.GetFullPath(nativesDirectory), requiredJvm, game, classpath.Distinct(PathComparer).ToArray(), new Dictionary<string, string>());
    }

    public static void ValidateUserArgument(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument) || argument.Any(char.IsControl)) throw new ArgumentException("JVM argument is empty or contains control characters.");
        if (ForbiddenUserPrefixes.Any(x => argument.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("This JVM argument is controlled by the launcher.");
    }

    private static string Expand(string value, MinecraftInstance instance, ResolvedMinecraftVersion runtime, OfflineLaunchIdentity identity, string assets, string natives, IReadOnlyList<string> classpath) => value
        .Replace("${auth_player_name}", identity.PlayerName, StringComparison.Ordinal)
        .Replace("${auth_uuid}", identity.PlayerUuid.ToString("N"), StringComparison.Ordinal)
        .Replace("${auth_access_token}", "0", StringComparison.Ordinal)
        .Replace("${auth_xuid}", "", StringComparison.Ordinal)
        .Replace("${clientid}", "", StringComparison.Ordinal)
        .Replace("${user_type}", "legacy", StringComparison.Ordinal)
        .Replace("${version_type}", "release", StringComparison.Ordinal)
        .Replace("${version_name}", runtime.Id, StringComparison.Ordinal)
        .Replace("${game_directory}", instance.Location.GameDirectory, StringComparison.Ordinal)
        .Replace("${assets_root}", assets, StringComparison.Ordinal)
        .Replace("${assets_index_name}", runtime.AssetIndex?.Id ?? "legacy", StringComparison.Ordinal)
        .Replace("${natives_directory}", natives, StringComparison.Ordinal)
        .Replace("${library_directory}", Path.Combine(Path.GetDirectoryName(assets)!, "libraries"), StringComparison.Ordinal)
        .Replace("${launcher_name}", "minecraft-manager", StringComparison.Ordinal)
        .Replace("${launcher_version}", "1", StringComparison.Ordinal)
        .Replace("${classpath_separator}", Path.PathSeparator.ToString(), StringComparison.Ordinal)
        .Replace("${classpath}", string.Join(Path.PathSeparator, classpath), StringComparison.Ordinal);

    private static void AddWindowArguments(WindowSettings window, List<string> game)
    {
        if (window.Mode == WindowMode.Fullscreen) game.Add("--fullscreen");
        if (window.Mode == WindowMode.Custom && window.Width is >= 320 && window.Height is >= 240)
        {
            game.Add("--width"); game.Add(window.Width.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            game.Add("--height"); game.Add(window.Height.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

public enum GameProcessState { Ready, Launching, Running, Exited, Failed, Stopping }
public sealed record GameProcessSnapshot(Guid InstanceId, int? ProcessId, GameProcessState State, DateTimeOffset? StartedAtUtc = null, int? ExitCode = null, string? Error = null);
public sealed record RunningGameProcess(Guid InstanceId, int ProcessId, DateTimeOffset StartedAtUtc);
public sealed record StopConfirmation(bool Confirmed);
public sealed record RequestStopResult(bool Accepted, string? Message = null);

public interface ILaunchExecutor { Task<RunningGameProcess> LaunchAsync(LaunchPlan plan, CancellationToken cancellationToken); }
public interface IGameProcessMonitor
{
    event EventHandler<GameProcessSnapshot>? Changed;
    GameProcessSnapshot? Get(Guid instanceId);
    bool IsRunning(Guid instanceId);
    Task<RequestStopResult> RequestStopAsync(Guid instanceId, StopConfirmation confirmation, CancellationToken cancellationToken);
}

public interface ILaunchReadinessService
{
    Task<LaunchReadiness> CheckAsync(MinecraftInstance instance, Guid? profileId, CancellationToken cancellationToken);
}
public sealed record LaunchFailure(string Code, string Message, bool IsRetryable);
public sealed record LaunchResult(bool Started, GameProcessSnapshot? Process, LaunchReadiness Readiness, LaunchFailure? Failure);
public interface IGameLaunchService
{
    Task<LaunchReadiness> CheckReadinessAsync(Guid instanceId, Guid? profileId, CancellationToken cancellationToken);
    Task<LaunchResult> LaunchAsync(Guid instanceId, Guid profileId, CancellationToken cancellationToken);
}

public interface IDesktopFolderService
{
    Task OpenAsync(string directory, CancellationToken cancellationToken);
}
