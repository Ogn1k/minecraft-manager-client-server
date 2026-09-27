using System.Text.Json;
using MinecraftManager.Core.Models;

namespace MinecraftManager.Core.Runtime;

public enum RuntimeOperatingSystem { Windows, Linux }
public enum CpuArchitecture { X64, Arm64 }
public sealed record RuntimePlatform(RuntimeOperatingSystem OperatingSystem, CpuArchitecture Architecture)
{
    public static RuntimePlatform Current => new(
        System.OperatingSystem.IsWindows() ? RuntimeOperatingSystem.Windows : RuntimeOperatingSystem.Linux,
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? CpuArchitecture.Arm64 : CpuArchitecture.X64);
}

public sealed record RuntimeArtifact(Uri Uri, string RelativePath, long? Size, string? Sha1, string? Sha256 = null);
public sealed record ResolvedLibrary(string Name, RuntimeArtifact Artifact);
public sealed record NativeArtifact(string Name, RuntimeArtifact Artifact, IReadOnlyList<string> Excludes);
public sealed record AssetIndexReference(string Id, RuntimeArtifact Artifact);
public sealed record LaunchArgumentRule(string Value);
public sealed record ResolvedMinecraftVersion(
    string Id,
    string MainClass,
    IReadOnlyList<ResolvedLibrary> Libraries,
    AssetIndexReference? AssetIndex,
    IReadOnlyList<LaunchArgumentRule> JvmArguments,
    IReadOnlyList<LaunchArgumentRule> GameArguments,
    IReadOnlyList<NativeArtifact> Natives,
    int RequiredJavaMajor,
    RuntimeArtifact? ClientJar = null);

public interface IRuntimeMetadataSource
{
    Task<string?> GetVersionJsonAsync(string versionId, CancellationToken cancellationToken);
}

public interface IMinecraftVersionResolver
{
    Task<ResolvedMinecraftVersion> ResolveAsync(RuntimeConfiguration configuration, RuntimePlatform platform, CancellationToken cancellationToken);
}

public sealed class MinecraftVersionResolver(IRuntimeMetadataSource source) : IMinecraftVersionResolver
{
    public async Task<ResolvedMinecraftVersion> ResolveAsync(RuntimeConfiguration configuration, RuntimePlatform platform, CancellationToken cancellationToken)
    {
        if (!configuration.IsConfigured) throw new InvalidOperationException("Minecraft version is not configured.");
        ValidateVersionId(configuration.MinecraftVersion!);
        return await ResolveCoreAsync(configuration.MinecraftVersion!, platform, new HashSet<string>(StringComparer.Ordinal), 0, cancellationToken);
    }

    public static void ValidateVersionId(string value)
    {
        if (value.Length is < 1 or > 128 || value.Contains("..", StringComparison.Ordinal) || value.Any(x => !(char.IsAsciiLetterOrDigit(x) || x is '.' or '_' or '+' or '-')))
            throw new InvalidDataException("Minecraft version identifier is invalid.");
    }

    private async Task<ResolvedMinecraftVersion> ResolveCoreAsync(string id, RuntimePlatform platform, HashSet<string> chain, int depth, CancellationToken ct)
    {
        ValidateVersionId(id);
        if (depth > 16 || !chain.Add(id)) throw new InvalidDataException("Minecraft version inheritance is cyclic or too deep.");
        var json = await source.GetVersionJsonAsync(id, ct) ?? throw new FileNotFoundException("Minecraft version metadata is missing.", id);
        if (json.Length > 4 * 1024 * 1024) throw new InvalidDataException("Minecraft version metadata is too large.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        ResolvedMinecraftVersion? parent = null;
        if (root.TryGetProperty("inheritsFrom", out var inherited))
            parent = await ResolveCoreAsync(inherited.GetString() ?? throw new InvalidDataException("Invalid inherited version."), platform, chain, depth + 1, ct);

        var mainClass = String(root, "mainClass") ?? parent?.MainClass ?? throw new InvalidDataException("Minecraft main class is missing.");
        var libraries = parent?.Libraries.ToList() ?? [];
        var natives = parent?.Natives.ToList() ?? [];
        if (root.TryGetProperty("libraries", out var libraryArray) && libraryArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var library in libraryArray.EnumerateArray())
            {
                if (!RulesAllow(library, platform)) continue;
                var name = String(library, "name") ?? throw new InvalidDataException("Library name is missing.");
                if (!library.TryGetProperty("downloads", out var downloads)) continue;
                if (downloads.TryGetProperty("artifact", out var artifact))
                    Replace(libraries, new ResolvedLibrary(name, ParseArtifact(artifact)), x => x.Name == name);
                if (library.TryGetProperty("natives", out var nativeMap))
                {
                    var os = platform.OperatingSystem == RuntimeOperatingSystem.Windows ? "windows" : "linux";
                    if (nativeMap.TryGetProperty(os, out var classifierTemplate) && downloads.TryGetProperty("classifiers", out var classifiers))
                    {
                        var classifier = (classifierTemplate.GetString() ?? "").Replace("${arch}", platform.Architecture == CpuArchitecture.X64 ? "64" : "arm64", StringComparison.Ordinal);
                        if (classifiers.TryGetProperty(classifier, out var nativeArtifact))
                            Replace(natives, new NativeArtifact(name, ParseArtifact(nativeArtifact), ParseExcludes(library)), x => x.Name == name);
                    }
                }
            }
        }

        var jvm = parent?.JvmArguments.ToList() ?? [];
        var game = parent?.GameArguments.ToList() ?? [];
        if (root.TryGetProperty("arguments", out var arguments))
        {
            AddArguments(arguments, "jvm", jvm, platform);
            AddArguments(arguments, "game", game, platform);
        }
        else if (root.TryGetProperty("minecraftArguments", out var legacy))
            game.AddRange(SplitLegacyArguments(legacy.GetString() ?? "").Select(x => new LaunchArgumentRule(x)));

        var java = root.TryGetProperty("javaVersion", out var javaElement) && javaElement.TryGetProperty("majorVersion", out var major)
            ? major.GetInt32() : parent?.RequiredJavaMajor ?? FallbackJava(id);
        var assetIndex = root.TryGetProperty("assetIndex", out var assets)
            ? new AssetIndexReference(String(assets, "id") ?? "legacy", ParseArtifact(assets)) : parent?.AssetIndex;
        RuntimeArtifact? client = parent?.ClientJar;
        if (root.TryGetProperty("downloads", out var rootDownloads) && rootDownloads.TryGetProperty("client", out var clientElement)) client = ParseArtifact(clientElement);
        chain.Remove(id);
        return new(id, mainClass, libraries, assetIndex, jvm, game, natives, java, client);
    }

    private static RuntimeArtifact ParseArtifact(JsonElement element)
    {
        var url = String(element, "url") ?? throw new InvalidDataException("Artifact URL is missing.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !IsTrustedRuntimeHost(uri.Host))
            throw new InvalidDataException("Runtime artifact URL is not from an allowed HTTPS host.");
        var path = String(element, "path") ?? uri.AbsolutePath.TrimStart('/');
        if (Path.IsPathRooted(path) || path.Split('/', '\\').Any(x => x is "" or "." or "..")) throw new InvalidDataException("Runtime artifact path is unsafe.");
        long? length = element.TryGetProperty("size", out var size) ? size.GetInt64() : null;
        if (length is < 0 or > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Runtime artifact size is invalid.");
        return new(uri, path.Replace('\\', '/'), length, String(element, "sha1"));
    }

    private static bool RulesAllow(JsonElement item, RuntimePlatform platform)
    {
        if (!item.TryGetProperty("rules", out var rules)) return true;
        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var matches = true;
            if (rule.TryGetProperty("os", out var os) && os.TryGetProperty("name", out var name))
                matches = name.GetString() == (platform.OperatingSystem == RuntimeOperatingSystem.Windows ? "windows" : "linux");
            if (matches && rule.TryGetProperty("os", out os) && os.TryGetProperty("arch", out var arch))
            {
                var expected = arch.GetString();
                matches = platform.Architecture == CpuArchitecture.Arm64
                    ? expected is "arm64" or "aarch64"
                    : expected is "x86_64" or "amd64" or "x64";
            }
            if (matches && rule.TryGetProperty("features", out var features))
                matches = features.EnumerateObject().All(x => x.Value.ValueKind == JsonValueKind.False);
            if (matches) allowed = String(rule, "action") == "allow";
        }
        return allowed;
    }

    private static void AddArguments(JsonElement arguments, string name, List<LaunchArgumentRule> target, RuntimePlatform platform)
    {
        if (!arguments.TryGetProperty(name, out var array)) return;
        foreach (var value in array.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String) target.Add(new(value.GetString()!));
            else if (value.ValueKind == JsonValueKind.Object && RulesAllow(value, platform) && value.TryGetProperty("value", out var conditional))
            {
                if (conditional.ValueKind == JsonValueKind.String) target.Add(new(conditional.GetString()!));
                else if (conditional.ValueKind == JsonValueKind.Array) target.AddRange(conditional.EnumerateArray().Select(x => new LaunchArgumentRule(x.GetString()!)));
            }
        }
    }

    private static IEnumerable<string> SplitLegacyArguments(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static IReadOnlyList<string> ParseExcludes(JsonElement library) => library.TryGetProperty("extract", out var extract) && extract.TryGetProperty("exclude", out var excludes) ? excludes.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
    private static string? String(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.GetString() : null;
    private static int FallbackJava(string id) => Version.TryParse(id.Split('-')[0], out var version) && version >= new Version(1, 20, 5) ? 21 : Version.TryParse(id.Split('-')[0], out version) && version >= new Version(1, 17) ? 17 : 8;
    private static bool IsTrustedRuntimeHost(string host) => host.EndsWith(".mojang.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".minecraft.net", StringComparison.OrdinalIgnoreCase) || host is "maven.fabricmc.net" or "maven.neoforged.net" or "maven.minecraftforge.net" or "maven.quiltmc.org";
    private static void Replace<T>(List<T> items, T value, Func<T, bool> predicate) { items.RemoveAll(x => predicate(x)); items.Add(value); }
}

public enum RuntimeFileState { Ready, Missing, Corrupt, Unverified }
public sealed record RuntimeValidationResult(bool IsReady, IReadOnlyList<string> Missing, IReadOnlyList<string> Corrupt);
public interface IRuntimeManager
{
    Task<RuntimeValidationResult> ValidateAsync(ResolvedMinecraftVersion version, CancellationToken cancellationToken);
    Task<RuntimeRepairResult> RepairAsync(ResolvedMinecraftVersion version, IProgress<RuntimeProgress>? progress, CancellationToken cancellationToken);
    Task<string> PrepareNativesAsync(ResolvedMinecraftVersion version, Guid launchId, CancellationToken cancellationToken);
}
public sealed record RuntimeProgress(string Stage, string? Artifact, long CompletedBytes, long? TotalBytes);
public sealed record RuntimeRepairResult(bool Succeeded, string? ErrorCode = null, string? Message = null);
