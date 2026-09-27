using System.Text;

namespace MinecraftManager.Core.Filesystem;

public sealed class UnsafePathException(string path, string message) : Exception(message)
{
    public string PathValue { get; } = path;
    public string SafeMessage { get; } = message;
}

public readonly record struct RelativeManifestPath
{
    public string Value { get; }
    private RelativeManifestPath(string value) => Value = value;

    public static RelativeManifestPath Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 1024 || input[0] is '/' or '\\' || input.Contains(':') || input.Contains('\0'))
            throw new UnsafePathException(input, "The path is not a safe relative path.");
        var normalized = input.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        var segments = normalized.Split('/');
        if (segments.Length > 64 || segments.Any(IsInvalidSegment))
            throw new UnsafePathException(input, "The path contains an unsafe segment.");
        return new(string.Join('/', segments));
    }

    private static bool IsInvalidSegment(string segment)
    {
        if (segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') || segment.Any(char.IsControl)) return true;
        var stem = segment.Split('.')[0];
        return WindowsReserved.Contains(stem);
    }

    private static readonly HashSet<string> WindowsReserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    public override string ToString() => Value;
}

public readonly record struct TrustedRoot(string FullPath)
{
    public static TrustedRoot Create(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new UnsafePathException(path, "The selected root is empty.");
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if ((File.Exists(fullPath) || Directory.Exists(fullPath)) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new UnsafePathException(path, "The selected root must not be a symbolic link or reparse point.");
        return new(fullPath);
    }
}

public readonly record struct ResolvedPath(string FullPath, RelativeManifestPath RelativePath);

public readonly record struct ManagedScope
{
    private static readonly string[] Protected = ["saves", "screenshots", "logs", "crash-reports", "options.txt", "servers.dat"];
    public string Value { get; }
    public bool IsDirectory { get; }
    private ManagedScope(string value, bool directory) { Value = value; IsDirectory = directory; }

    public static ManagedScope Parse(string raw)
    {
        var isDirectory = raw.EndsWith('/') || raw.EndsWith('\\');
        var path = RelativeManifestPath.Parse(raw.TrimEnd('/', '\\'));
        if (Protected.Any(p => path.Value.Equals(p, StringComparison.OrdinalIgnoreCase) || path.Value.StartsWith(p + '/', StringComparison.OrdinalIgnoreCase)))
            throw new UnsafePathException(raw, "The managed path overlaps protected user content.");
        return new(path.Value, isDirectory);
    }

    public bool Contains(RelativeManifestPath path) => IsDirectory
        ? path.Value.StartsWith(Value + '/', StringComparison.OrdinalIgnoreCase)
        : path.Value.Equals(Value, StringComparison.OrdinalIgnoreCase);
}

public interface ISafePathResolver
{
    ResolvedPath ResolveFile(TrustedRoot root, RelativeManifestPath path);
    void VerifyNoLinks(TrustedRoot root, ResolvedPath path);
}

public sealed class SafePathResolver : ISafePathResolver
{
    public ResolvedPath ResolveFile(TrustedRoot root, RelativeManifestPath path)
    {
        var segments = path.Value.Split('/');
        var combined = Path.GetFullPath(Path.Combine([root.FullPath, .. segments]));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!combined.StartsWith(root.FullPath + Path.DirectorySeparatorChar, comparison))
            throw new UnsafePathException(path.Value, "The path resolves outside the trusted root.");
        return new(combined, path);
    }

    public void VerifyNoLinks(TrustedRoot root, ResolvedPath path)
    {
        var current = root.FullPath;
        foreach (var segment in path.RelativePath.Value.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnsafePathException(path.RelativePath.Value, "The path crosses a symbolic link or reparse point.");
        }
    }
}
