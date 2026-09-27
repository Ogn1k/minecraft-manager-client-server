using System.Diagnostics;
using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Runtime;

namespace MinecraftManager.Infrastructure.Runtime;

public sealed partial class JavaRuntimeService(IJavaCompatibilityPolicy policy, HttpClient http, IApplicationPaths paths) : IJavaRuntimeService
{
    private readonly SemaphoreSlim provisionGate = new(1, 1);

    public async Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<(string Path, JavaRuntimeOrigin Origin)>();
        var managedRoot = Path.Combine(paths.RuntimeStateDirectory, "java");
        if (Directory.Exists(managedRoot))
            foreach (var directory in Directory.EnumerateDirectories(managedRoot, "*", SearchOption.TopDirectoryOnly).Take(16))
                candidates.Add((ManagedJavaPath(directory), JavaRuntimeOrigin.Managed));
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) candidates.Add((Path.Combine(javaHome, "bin", JavaName), JavaRuntimeOrigin.JavaHome));
        foreach (var item in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            candidates.Add((Path.Combine(item, JavaName), JavaRuntimeOrigin.Path));
        if (OperatingSystem.IsWindows())
        {
            foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(Directory.Exists))
                foreach (var vendorRoot in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Amazon Corretto", "Zulu" }.Select(x => Path.Combine(programFiles, x)).Where(Directory.Exists))
                    foreach (var directory in Directory.EnumerateDirectories(vendorRoot, "*", SearchOption.TopDirectoryOnly).Take(64))
                        candidates.Add((Path.Combine(directory, "bin", JavaName), JavaRuntimeOrigin.KnownLocation));
        }
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var runtimes = new List<JavaRuntime>();
        foreach (var candidate in candidates.Where(x => File.Exists(x.Path)).DistinctBy(x => Path.GetFullPath(x.Path), comparer).Take(64))
        {
            var runtime = await InspectAsync(candidate.Path, candidate.Origin, cancellationToken);
            if (runtime is not null) runtimes.Add(runtime);
        }
        return runtimes;
    }

    public Task<JavaRuntimeValidationResult> ValidateAsync(JavaRuntime runtime, JavaRuntimeRequirement requirement, RuntimePlatform platform, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(runtime.ExecutablePath)) return Task.FromResult(new JavaRuntimeValidationResult(false, "missing_java", "Java executable does not exist."));
        if (OperatingSystem.IsLinux() && !IsUnixExecutable(runtime.ExecutablePath)) return Task.FromResult(new JavaRuntimeValidationResult(false, "java_not_executable", "Java does not have execute permission."));
        return Task.FromResult(policy.Validate(runtime, requirement, platform));
    }

    public async Task<JavaRuntime?> SelectAsync(JavaSelection selection, JavaRuntimeRequirement requirement, RuntimePlatform platform, CancellationToken cancellationToken)
    {
        if (selection.Mode == JavaSelectionMode.Custom)
        {
            if (string.IsNullOrWhiteSpace(selection.CustomPath) || !File.Exists(selection.CustomPath) || OperatingSystem.IsLinux() && !IsUnixExecutable(selection.CustomPath)) return null;
            var custom = await InspectAsync(Path.GetFullPath(selection.CustomPath), JavaRuntimeOrigin.Custom, cancellationToken);
            return custom is not null && policy.Validate(custom, requirement, platform).IsCompatible ? custom : null;
        }
        return (await DiscoverAsync(cancellationToken)).Where(x => policy.Validate(x, requirement, platform).IsCompatible)
            .OrderBy(x => x.Origin).ThenBy(x => x.ExecutablePath, StringComparer.Ordinal).FirstOrDefault();
    }

    public async Task<JavaRuntime?> ProvisionAsync(JavaRuntimeRequirement requirement, RuntimePlatform platform, CancellationToken cancellationToken)
    {
        await provisionGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await SelectAsync(new JavaSelection(), requirement, platform, cancellationToken);
            if (existing is not null) return existing;

            var os = platform.OperatingSystem == RuntimeOperatingSystem.Windows ? "windows" : "linux";
            var architecture = platform.Architecture == CpuArchitecture.Arm64 ? "aarch64" : "x64";
            var metadataUri = new Uri($"https://api.adoptium.net/v3/assets/latest/{requirement.MajorVersion}/hotspot?architecture={architecture}&image_type=jre&os={os}&vendor=eclipse");
            using var metadataResponse = await http.GetAsync(metadataUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            metadataResponse.EnsureSuccessStatusCode();
            await using var metadataStream = await metadataResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var metadata = await JsonDocument.ParseAsync(metadataStream, new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
            JsonElement? selectedPackage = null;
            foreach (var release in metadata.RootElement.EnumerateArray())
            {
                if (!release.TryGetProperty("binary", out var binary) || binary.ValueKind != JsonValueKind.Object) continue;
                if (!binary.TryGetProperty("image_type", out var imageType) || imageType.GetString() != "jre") continue;
                if (binary.TryGetProperty("package", out var candidate) && candidate.ValueKind == JsonValueKind.Object)
                {
                    selectedPackage = candidate;
                    break;
                }
            }
            var package = selectedPackage ?? throw new InvalidDataException($"No Java {requirement.MajorVersion} runtime is available for {os}/{architecture}.");
            if (!package.TryGetProperty("link", out var link) || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var downloadUri))
                throw new InvalidDataException("Java package URL is missing or invalid.");
            if (!package.TryGetProperty("checksum", out var checksum)) throw new InvalidDataException("Java package checksum is missing.");
            var expectedHash = checksum.GetString() ?? throw new InvalidDataException("Java package checksum is missing.");
            if (expectedHash.Length != 64 || expectedHash.Any(x => !Uri.IsHexDigit(x))) throw new InvalidDataException("Java package checksum is invalid.");

            var javaRoot = Path.Combine(paths.RuntimeStateDirectory, "java");
            var finalDirectory = Path.Combine(javaRoot, $"{requirement.MajorVersion}-{os}-{architecture}");
            var staging = Path.Combine(javaRoot, ".installing-" + Guid.NewGuid().ToString("N"));
            var archivePath = staging + (platform.OperatingSystem == RuntimeOperatingSystem.Windows ? ".zip" : ".tar.gz");
            Directory.CreateDirectory(javaRoot);
            try
            {
                await DownloadAsync(downloadUri, archivePath, expectedHash, cancellationToken);
                Directory.CreateDirectory(staging);
                ExtractArchive(archivePath, staging, platform, cancellationToken);
                var executable = FindJavaExecutable(staging);
                if (executable is null) throw new InvalidDataException("The Java archive does not contain a runtime executable.");
                var extractedRoot = Directory.GetParent(Directory.GetParent(executable)!.FullName)!.FullName;
                if (Directory.Exists(finalDirectory)) TryDelete(finalDirectory);
                Directory.Move(extractedRoot, finalDirectory);
                var runtime = await InspectAsync(ManagedJavaPath(finalDirectory), JavaRuntimeOrigin.Managed, cancellationToken);
                if (runtime is null || !policy.Validate(runtime, requirement, platform).IsCompatible)
                {
                    TryDelete(finalDirectory);
                    throw new InvalidDataException("The installed Java runtime failed compatibility validation.");
                }
                return runtime;
            }
            finally
            {
                TryDelete(staging);
                try { if (File.Exists(archivePath)) File.Delete(archivePath); } catch { /* Best effort cleanup. */ }
            }
        }
        finally { provisionGate.Release(); }
    }

    private async Task DownloadAsync(Uri uri, string destination, string expectedHash, CancellationToken cancellationToken)
    {
        for (var redirects = 0; ; redirects++)
        {
            if (redirects > 5 || uri.Scheme != Uri.UriSchemeHttps || !TrustedJavaHost(uri.Host))
                throw new InvalidDataException("Java download redirected to an untrusted location.");
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                uri = response.Headers.Location is { IsAbsoluteUri: true } location ? location
                    : new Uri(uri, response.Headers.Location ?? throw new InvalidDataException("Java download redirect is missing a location."));
                continue;
            }
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total = checked(total + read);
                    if (total > 512L * 1024 * 1024) throw new InvalidDataException("Java package exceeds the size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            await using var downloaded = File.OpenRead(destination);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellationToken));
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Java package checksum verification failed.");
            return;
        }
    }

    private static void ExtractArchive(string archivePath, string destination, RuntimePlatform platform, CancellationToken cancellationToken)
    {
        if (platform.OperatingSystem == RuntimeOperatingSystem.Windows)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = SafeExtractionPath(destination, entry.FullName);
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, false);
            }
            return;
        }
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        TarEntry? item;
        while ((item = tar.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink) throw new InvalidDataException("Java archive contains a link.");
            var target = SafeExtractionPath(destination, item.Name);
            if (item.EntryType is TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
            if (item.EntryType is not TarEntryType.RegularFile) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            item.ExtractToFile(target, false);
        }
    }

    private static string SafeExtractionPath(string root, string entry)
    {
        var target = Path.GetFullPath(Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Java archive contains an unsafe path.");
        return target;
    }

    private static string? FindJavaExecutable(string root) => Directory.EnumerateFiles(root, JavaName, SearchOption.AllDirectories)
        .FirstOrDefault(x => string.Equals(Path.GetFileName(Path.GetDirectoryName(x)), "bin", StringComparison.OrdinalIgnoreCase));
    private static string ManagedJavaPath(string root) => Path.Combine(root, "bin", JavaName);
    private static bool TrustedJavaHost(string host) => host is "api.adoptium.net" or "github.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com";
    private static void TryDelete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* Best effort cleanup. */ } }

    private static async Task<JavaRuntime?> InspectAsync(string path, JavaRuntimeOrigin origin, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new() { FileName = path, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true } };
        var started = false;
        process.StartInfo.ArgumentList.Add("-version");
        try
        {
            if (!process.Start()) return null;
            started = true;
            var outputTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            var output = (await outputTask) + " " + (await stdoutTask);
            if (output.Length > 64 * 1024) return null;
            var match = VersionPattern().Match(output);
            if (!match.Success) return null;
            var raw = match.Groups[1].Value;
            var major = raw.StartsWith("1.", StringComparison.Ordinal) ? int.Parse(raw.Split('.')[1], System.Globalization.CultureInfo.InvariantCulture) : int.Parse(raw.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
            var architecture = output.Contains("aarch64", StringComparison.OrdinalIgnoreCase) || output.Contains("arm64", StringComparison.OrdinalIgnoreCase) ? CpuArchitecture.Arm64 : CpuArchitecture.X64;
            var vendor = output.Contains("openjdk", StringComparison.OrdinalIgnoreCase) ? "OpenJDK" : "Java";
            return new(Path.GetFullPath(path), major, vendor, architecture, origin);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        finally { if (started && !process.HasExited) process.Kill(true); }
    }

    private static string JavaName => OperatingSystem.IsWindows() ? "java.exe" : "java";
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static bool IsUnixExecutable(string path)
    {
        var mode = File.GetUnixFileMode(path);
        return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
    [GeneratedRegex("version\\s+\"([0-9]+(?:\\.[0-9]+)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
