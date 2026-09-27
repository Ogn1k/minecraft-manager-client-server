using System.Text.Json.Serialization;

namespace MinecraftManager.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LocalFolderSourceSettings), "localFolder")]
[JsonDerivedType(typeof(LocalArchiveSourceSettings), "localArchive")]
[JsonDerivedType(typeof(StaticHttpSourceSettings), "staticHttp")]
[JsonDerivedType(typeof(ManagedServerSourceSettings), "managedServer")]
public abstract record UpdateSourceSettings;

public sealed record LocalFolderSourceSettings(string FolderPath) : UpdateSourceSettings;
public sealed record LocalArchiveSourceSettings(string ArchivePath) : UpdateSourceSettings;
public sealed record StaticHttpSourceSettings(Uri BaseUri, bool AllowInsecureLan = false) : UpdateSourceSettings;
public sealed record ManagedServerSourceSettings(Guid ServerProfileId, string? PackId, Guid? ClientInstanceId = null) : UpdateSourceSettings;

public sealed record InstalledPackState(
    string PackId,
    string PackVersion,
    string ManifestSha256,
    DateTimeOffset InstalledAtUtc);

public sealed record ManagedFileRecord(string Path, string Sha256, long Size);

public sealed record InstanceState(
    InstalledPackState? InstalledPack,
    IReadOnlyList<ManagedFileRecord> ManagedFiles,
    DateTimeOffset? LastCheckedAtUtc = null,
    string? AvailablePackVersion = null,
    bool PackIntegrityFailed = false);

public enum InstanceOwnership { ApplicationOwned, ManagedExternal }
public enum ModLoaderType { Fabric, NeoForge, Forge, Quilt }
public enum PackUpdatePolicy { Optional, RequiredBeforeLaunch }
public enum JavaSelectionMode { Automatic, Custom }
public enum WindowMode { Default, Custom, Fullscreen }

public sealed record InstanceLocation(string GameDirectory, InstanceOwnership Ownership);
public sealed record ModLoaderConfiguration(ModLoaderType Type, string Version);
public sealed record JavaRuntimeRequirement(int MajorVersion, string? Architecture = null);
public sealed record RuntimeConfiguration(
    string? MinecraftVersion = null,
    ModLoaderConfiguration? ModLoader = null,
    JavaRuntimeRequirement? Java = null)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(MinecraftVersion);
}
public sealed record PackConfiguration(UpdateSourceSettings Source, PackUpdatePolicy UpdatePolicy = PackUpdatePolicy.Optional);
public sealed record JavaSelection(JavaSelectionMode Mode = JavaSelectionMode.Automatic, string? CustomPath = null);
public sealed record MemorySettings(int MinimumMb = 1024, int MaximumMb = 4096);
public sealed record WindowSettings(WindowMode Mode = WindowMode.Default, int? Width = null, int? Height = null);
public sealed record LaunchConfiguration(
    JavaSelection Java,
    MemorySettings Memory,
    WindowSettings Window,
    IReadOnlyList<string> AdditionalJvmArguments)
{
    public static LaunchConfiguration Default { get; } = new(new(), new(), new(), []);
}

public sealed record MinecraftInstance
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public required InstanceLocation Location { get; init; }
    public required RuntimeConfiguration Runtime { get; init; }
    public PackConfiguration? Pack { get; init; }
    public required LaunchConfiguration Launch { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public enum ThemePreference { System, Light, Dark }
public enum LanguagePreference { English, Russian }

public sealed record ApplicationSettings(
    ThemePreference Theme = ThemePreference.System,
    int MaxConcurrentDownloads = 4,
    int DownloadTimeoutSeconds = 60,
    int BackupRetentionCount = 3,
    int BackupRetentionDays = 14,
    bool CheckOnStartup = true,
    LanguagePreference Language = LanguagePreference.English);

public sealed record ServerProfile(
    Guid Id,
    string DisplayName,
    Uri BaseUri,
    Guid? DeviceId = null);

public sealed record ClientConfiguration(
    int SchemaVersion,
    IReadOnlyList<MinecraftInstance> Instances,
    IReadOnlyList<ServerProfile> ServerProfiles,
    ApplicationSettings Settings,
    Guid? SelectedInstanceId = null)
{
    public static ClientConfiguration Empty { get; } =
        new(2, [], [], new ApplicationSettings());
}
