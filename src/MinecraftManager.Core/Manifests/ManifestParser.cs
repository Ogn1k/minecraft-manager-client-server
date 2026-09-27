using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftManager.Core.Filesystem;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Runtime;

namespace MinecraftManager.Core.Manifests;

public interface IManifestParser
{
    Task<ManifestValidationResult> ParseAsync(Stream json, Version clientVersion, CancellationToken cancellationToken);
    ManifestValidationResult Validate(PackManifest manifest, Version clientVersion);
}

public sealed class ManifestParser(int maximumBytes = 4 * 1024 * 1024, int maximumFiles = 10_000) : IManifestParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<ManifestValidationResult> ParseAsync(Stream json, Version clientVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            await using var bounded = new MemoryStream();
            var buffer = new byte[81920];
            var total = 0;
            int read;
            while ((read = await json.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > maximumBytes)
                    return Invalid("manifest_too_large", $"Manifest exceeds {maximumBytes} bytes.");
                await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            bounded.Position = 0;
            var manifest = await JsonSerializer.DeserializeAsync<PackManifest>(bounded, Options, cancellationToken);
            return manifest is null ? Invalid("manifest_empty", "Manifest is empty.") : Validate(manifest, clientVersion);
        }
        catch (JsonException)
        {
            return Invalid("manifest_json_invalid", "Manifest JSON is malformed or contains unsupported fields.");
        }
    }

    public ManifestValidationResult Validate(PackManifest manifest, Version clientVersion)
    {
        var errors = new List<ManifestValidationError>();
        if (manifest.SchemaVersion is not (1 or 2)) errors.Add(new("unsupported_schema", "Only manifest schema versions 1 and 2 are supported."));
        ValidateText(manifest.PackId, "packId", 128, errors);
        ValidateText(manifest.PackVersion, "packVersion", 128, errors);
        if (manifest.MinecraftVersion is { } minecraft)
        {
            try { MinecraftVersionResolver.ValidateVersionId(minecraft); }
            catch (InvalidDataException) { errors.Add(new("invalid_minecraft_version", "Minecraft version is invalid.")); }
        }
        if (manifest.ModLoader is { } loader)
        {
            if (!Enum.TryParse<ModLoaderType>(loader.Type, ignoreCase: true, out _))
                errors.Add(new("invalid_mod_loader", "Mod loader type is not supported.", loader.Type));
            if (string.IsNullOrWhiteSpace(loader.Version) || loader.Version.Length > 128 || loader.Version.Any(char.IsWhiteSpace))
                errors.Add(new("invalid_mod_loader_version", "Mod loader version is invalid."));
        }
        if (manifest.MinimumClientVersion is { } minimum &&
            (!Version.TryParse(minimum, out var version) || Compare(version, clientVersion) > 0))
            errors.Add(new("client_version_unsupported", $"This pack requires client version {minimum} or newer."));
        if (manifest.Files.Count > maximumFiles) errors.Add(new("too_many_files", $"Manifest exceeds {maximumFiles} files."));
        if (manifest.ManagedPaths.Count > 256) errors.Add(new("too_many_scopes", "Manifest contains too many managed paths."));

        var scopes = new List<ManagedScope>();
        foreach (var rawScope in manifest.ManagedPaths)
        {
            try { scopes.Add(ManagedScope.Parse(rawScope)); }
            catch (UnsafePathException ex) { errors.Add(new("unsafe_managed_path", ex.SafeMessage, rawScope)); }
        }

        var exact = new HashSet<string>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalSize = 0;
        foreach (var file in manifest.Files)
        {
            RelativeManifestPath path;
            try { path = RelativeManifestPath.Parse(file.Path); }
            catch (UnsafePathException ex)
            {
                errors.Add(new("unsafe_file_path", ex.SafeMessage, file.Path));
                continue;
            }
            if (!exact.Add(path.Value) || !folded.Add(path.Value)) errors.Add(new("duplicate_path", "Manifest paths collide on a supported platform.", path.Value));
            if (file.Size < 0 || file.Size > 2L * 1024 * 1024 * 1024) errors.Add(new("invalid_file_size", "File size is outside supported bounds.", path.Value));
            else
            {
                try { totalSize = checked(totalSize + file.Size); }
                catch (OverflowException) { errors.Add(new("manifest_size_overflow", "Aggregate file size is too large.")); }
            }
            if (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)) errors.Add(new("invalid_sha256", "SHA-256 must contain 64 hexadecimal characters.", path.Value));
            if (scopes.Count > 0 && !scopes.Any(scope => scope.Contains(path))) errors.Add(new("file_outside_managed_scope", "File is outside every managed path.", path.Value));
        }

        if (errors.Count > 0) return new(null, errors);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Options);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(new(manifest, digest), []);
    }

    private static void ValidateText(string value, string field, int maximum, List<ManifestValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            errors.Add(new("invalid_text", $"{field} is required and limited to {maximum} characters."));
    }

    private static ManifestValidationResult Invalid(string code, string message) => new(null, [new(code, message)]);
    private static int Compare(Version left, Version right)
    {
        var leftParts = new[] { left.Major, left.Minor, Math.Max(0, left.Build), Math.Max(0, left.Revision) };
        var rightParts = new[] { right.Major, right.Minor, Math.Max(0, right.Build), Math.Max(0, right.Revision) };
        for (var i = 0; i < leftParts.Length; i++) { var comparison = leftParts[i].CompareTo(rightParts[i]); if (comparison != 0) return comparison; }
        return 0;
    }
}
