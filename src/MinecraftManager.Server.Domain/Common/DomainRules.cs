using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MinecraftManager.Server.Contracts.V1;

namespace MinecraftManager.Server.Domain.Common;

public sealed class DomainRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public readonly record struct Sha256Digest
{
    private static readonly Regex Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    public string Value { get; }
    public Sha256Digest(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (!Pattern.IsMatch(value)) throw new DomainRuleException("invalid_sha256", "SHA-256 must contain 64 hexadecimal characters.");
        Value = value;
    }
    public override string ToString() => Value;
}

public readonly record struct RelativeManifestPath
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
    public string Value { get; }
    public RelativeManifestPath(string value, bool directory = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value[0] is '/' or '\\' || value.Contains('\\') || value.Contains(':') || value.Any(char.IsControl))
            throw new DomainRuleException("unsafe_path", "The path is not a safe relative manifest path.");
        var trailing = value.EndsWith('/');
        var parts = value.TrimEnd('/').Split('/');
        if (parts.Length > 64 || parts.Any(x => x is "" or "." or ".." || x.EndsWith('.') || x.EndsWith(' ') || Reserved.Contains(Path.GetFileNameWithoutExtension(x))))
            throw new DomainRuleException("unsafe_path", "The path contains an unsafe segment.");
        Value = string.Join('/', parts.Select(x => x.Normalize(NormalizationForm.FormC))) + (directory || trailing ? "/" : "");
    }
    public override string ToString() => Value;
}

public readonly record struct ManagedScope
{
    private static readonly string[] Protected = ["saves/", "screenshots/", "logs/", "crash-reports/", "options.txt", "servers.dat"];
    public string Value { get; }
    public ManagedScope(string value)
    {
        var path = new RelativeManifestPath(value, value.EndsWith('/')).Value;
        if (Protected.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || path is "")
            throw new DomainRuleException("protected_scope", "The managed scope includes protected user content.");
        Value = path;
    }
    public bool Contains(RelativeManifestPath path) => Value.EndsWith('/') ? path.Value.StartsWith(Value, StringComparison.OrdinalIgnoreCase) : path.Value.Equals(Value, StringComparison.OrdinalIgnoreCase);
}

public static class ManifestCanonicalizer
{
    public static (byte[] Bytes, Sha256Digest Digest) Build(string packId, string version, string? minimumClientVersion, IEnumerable<ManagedScope> scopes, IEnumerable<(Guid Id, RelativeManifestPath Path, long Size, Sha256Digest Hash, string? ContentType)> files, string? minecraftVersion = null, (string Type, string Version)? modLoader = null)
    {
        var orderedScopes = scopes.Select(x => x.Value).Order(StringComparer.Ordinal).ToArray();
        var orderedFiles = files.OrderBy(x => x.Path.Value, StringComparer.Ordinal).Select(x => new ManifestFileV1(x.Id, x.Path.Value, x.Size, x.Hash.Value, x.ContentType, new("server", x.Id))).ToArray();
        EnsureNoCollisions(orderedFiles.Select(x => x.Path));
        if (orderedFiles.Any(f => !orderedScopes.Any(s => new ManagedScope(s).Contains(new RelativeManifestPath(f.Path)))))
            throw new DomainRuleException("file_outside_scope", "Every file must be contained in a managed scope.");
        var schema = minecraftVersion is null && modLoader is null ? 1 : 2;
        var manifest = new ManifestV1(schema, packId, version, minimumClientVersion, orderedScopes, orderedFiles, null, minecraftVersion, modLoader is { } loader ? new(loader.Type, loader.Version) : null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ContractJson.Options);
        return (bytes, new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
    }

    public static void EnsureNoCollisions(IEnumerable<string> paths)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Select(x => x.Normalize(NormalizationForm.FormC)))
            if (!normalized.Add(path)) throw new DomainRuleException("path_collision", "Manifest paths collide on a supported target.");
    }
}
