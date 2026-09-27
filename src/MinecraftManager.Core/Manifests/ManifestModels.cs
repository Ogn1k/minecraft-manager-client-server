namespace MinecraftManager.Core.Manifests;

public sealed record PackManifest
{
    public required int SchemaVersion { get; init; }
    public required string PackId { get; init; }
    public required string PackVersion { get; init; }
    public string? MinimumClientVersion { get; init; }
    public string? MinecraftVersion { get; init; }
    public ModLoaderRequirement? ModLoader { get; init; }
    public required IReadOnlyList<string> ManagedPaths { get; init; }
    public required IReadOnlyList<ManifestFile> Files { get; init; }
    public ManifestMetadata? Metadata { get; init; }
}

public sealed record ModLoaderRequirement(string Type, string Version);

public sealed record ManifestFile
{
    public string? Id { get; init; }
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public required long Size { get; init; }
    public string? ContentType { get; init; }
    public string? SourcePath { get; init; }
    public ManifestDownload? Download { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed record ManifestDownload(string Type, string? FileId = null);
public sealed record ManifestMetadata(string? DisplayName, string? ReleaseNotes);

public sealed record ValidatedManifest(PackManifest Value, string CanonicalSha256);

public sealed record ManifestValidationError(string Code, string Message, string? Path = null);

public sealed record ManifestValidationResult(
    ValidatedManifest? Manifest,
    IReadOnlyList<ManifestValidationError> Errors)
{
    public bool IsValid => Manifest is not null && Errors.Count == 0;
}
