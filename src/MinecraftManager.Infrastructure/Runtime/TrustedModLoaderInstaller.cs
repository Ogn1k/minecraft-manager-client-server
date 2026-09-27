using System.Diagnostics;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Runtime;

namespace MinecraftManager.Infrastructure.Runtime;

public sealed class TrustedModLoaderInstaller(HttpClient http, IApplicationPaths paths, IModLoaderRuntimeRegistry registry, IJavaRuntimeService java) : IModLoaderInstaller
{
    public async Task<LoaderInstallResult> InstallAsync(LoaderInstallRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var descriptor = registry.Resolve(request.MinecraftVersion, request.Loader);
            if (request.Loader.Type is ModLoaderType.Forge or ModLoaderType.NeoForge)
                return await InstallJvmLoaderAsync(request, descriptor, cancellationToken);
            var uri = request.Loader.Type switch
            {
                ModLoaderType.Fabric => new Uri($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(request.MinecraftVersion)}/{Uri.EscapeDataString(request.Loader.Version)}/profile/json"),
                ModLoaderType.Quilt => new Uri($"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(request.MinecraftVersion)}/{Uri.EscapeDataString(request.Loader.Version)}/profile/json"),
                _ => throw new NotSupportedException($"Automated {request.Loader.Type} installation is not supported yet. Import validated loader metadata manually.")
            };
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (json.Length is 0 or > 4 * 1024 * 1024) throw new InvalidDataException("Loader metadata size is invalid.");
            using var parsed = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions { MaxDepth = 64 });
            if (!parsed.RootElement.TryGetProperty("mainClass", out _) && !parsed.RootElement.TryGetProperty("inheritsFrom", out _)) throw new InvalidDataException("Loader profile is invalid.");
            var directory = Path.Combine(paths.SharedVersionsDirectory, descriptor.ResolvedVersionId);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, descriptor.ResolvedVersionId + ".json");
            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try { await File.WriteAllTextAsync(temporary, json, cancellationToken); File.Move(temporary, destination, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return new(true, descriptor.ResolvedVersionId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, ErrorCode: "loader_install_failed", Message: ex.Message); }
    }

    private async Task<LoaderInstallResult> InstallJvmLoaderAsync(LoaderInstallRequest request, LoaderRuntimeDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (!TryParseMajor(request.MinecraftVersion, out var major) || major < 13)
            return new(false, ErrorCode: "loader_unsupported", Message: "Automatic Forge/NeoForge installation requires Minecraft 1.13 or newer.");
        var requirement = new JavaRuntimeRequirement(RequiredJava(major));
        var javaRuntime = await java.SelectAsync(new JavaSelection(), requirement, RuntimePlatform.Current, cancellationToken)
            ?? await java.ProvisionAsync(requirement, RuntimePlatform.Current, cancellationToken)
            ?? throw new InvalidDataException($"Java {requirement.MajorVersion} could not be installed automatically.");
        var installerUri = InstallerUri(request.Loader.Type, request.MinecraftVersion, request.Loader.Version);
        var installerPath = await DownloadInstallerAsync(request.Loader.Type, installerUri, cancellationToken);
        var staging = Path.Combine(Path.GetTempPath(), "mm-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(staging, "versions"));
        Directory.CreateDirectory(Path.Combine(staging, "libraries"));
        await File.WriteAllTextAsync(Path.Combine(staging, "launcher_profiles.json"), "{\"profiles\":{}}", cancellationToken);
        try
        {
            var installer = await RunInstallerAsync(javaRuntime.ExecutablePath, installerPath, staging, cancellationToken);
            if (installer.ExitCode != 0)
                return new(false, ErrorCode: "loader_install_failed", Message: $"The {request.Loader.Type} installer exited with code {installer.ExitCode}: {InstallerDiagnostic(installer.Output)}");
            var producedId = InstallerVersionId(request.Loader.Type, request.MinecraftVersion, request.Loader.Version);
            var produced = Path.Combine(staging, "versions", producedId);
            if (!Directory.Exists(produced))
                return new(false, ErrorCode: "loader_install_failed", Message: $"The installer did not produce the expected version profile '{producedId}'.");
            var producedJson = Path.Combine(produced, producedId + ".json");
            if (!File.Exists(producedJson)) return new(false, ErrorCode: "loader_install_failed", Message: "The installer produced an invalid version profile.");
            var finalId = descriptor.ResolvedVersionId;
            var finalDirectory = Path.Combine(paths.SharedVersionsDirectory, finalId);
            Directory.CreateDirectory(paths.SharedVersionsDirectory);
            if (Directory.Exists(finalDirectory)) TryDelete(finalDirectory);
            if (producedId != finalId)
            {
                Directory.Move(produced, finalDirectory);
                File.Move(Path.Combine(finalDirectory, producedId + ".json"), Path.Combine(finalDirectory, finalId + ".json"), true);
            }
            else
            {
                Directory.Move(produced, finalDirectory);
            }
            MergeDirectory(Path.Combine(staging, "versions"), paths.SharedVersionsDirectory, cancellationToken);
            MergeDirectory(Path.Combine(staging, "libraries"), paths.SharedLibrariesDirectory, cancellationToken);
            return new(true, finalId);
        }
        finally { TryDelete(staging); }
    }

    private async Task<string> DownloadInstallerAsync(ModLoaderType type, Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Loader installers must be downloaded over HTTPS.");
        var directory = Path.Combine(paths.CacheDirectory, "installers");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, type.ToString().ToLowerInvariant() + "-" + Guid.NewGuid().ToString("N") + ".jar");
        var temporary = destination + ".tmp";
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total = checked(total + read);
                    if (total > 256L * 1024 * 1024) throw new InvalidDataException("Loader installer exceeds the size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<InstallerProcessResult> RunInstallerAsync(string javaPath, string installerPath, string targetRoot, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new()
            {
                FileName = javaPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = targetRoot
            }
        };
        process.StartInfo.ArgumentList.Add("-Djava.awt.headless=true");
        process.StartInfo.ArgumentList.Add("-jar");
        process.StartInfo.ArgumentList.Add(installerPath);
        process.StartInfo.ArgumentList.Add("--installClient");
        process.StartInfo.ArgumentList.Add(targetRoot);
        if (!process.Start()) return new(-1, "The installer process could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) { try { process.Kill(true); } catch { /* Process may already be gone. */ } }
            throw;
        }
        var output = (await outputTask) + Environment.NewLine + (await errorTask);
        return new(process.ExitCode, output);
    }

    private static void MergeDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Loader library path is unsafe.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) File.Move(file, target);
            else File.Delete(file);
        }
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    private static Uri InstallerUri(ModLoaderType type, string minecraftVersion, string loaderVersion) => type switch
    {
        ModLoaderType.Forge => new Uri($"https://maven.minecraftforge.net/net/minecraftforge/forge/{Escape(minecraftVersion)}-{Escape(loaderVersion)}/forge-{Escape(minecraftVersion)}-{Escape(loaderVersion)}-installer.jar"),
        ModLoaderType.NeoForge => new Uri($"https://maven.neoforged.net/releases/net/neoforged/neoforge/{Escape(loaderVersion)}/neoforge-{Escape(loaderVersion)}-installer.jar"),
        _ => throw new NotSupportedException()
    };

    private static string InstallerVersionId(ModLoaderType type, string minecraftVersion, string loaderVersion) => type switch
    {
        ModLoaderType.Forge => $"{minecraftVersion}-forge-{loaderVersion}",
        ModLoaderType.NeoForge => $"neoforge-{loaderVersion}",
        _ => throw new NotSupportedException()
    };

    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static string InstallerDiagnostic(string output)
    {
        var normalized = string.Join(" ", output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(normalized)) return "No diagnostic output was produced.";
        const int maximumLength = 2000;
        return normalized.Length <= maximumLength ? normalized : "…" + normalized[^maximumLength..];
    }
    private static bool TryParseMajor(string minecraftVersion, out int major)
    {
        major = 0;
        if (!Version.TryParse(minecraftVersion.Split('-')[0], out var version)) return false;
        major = version.Major == 1 ? version.Minor : version.Major;
        return major >= 1;
    }
    private static int RequiredJava(int major) => major >= 20 ? 21 : major >= 17 ? 17 : 8;
    private static void TryDelete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* Best effort cleanup only. */ } }
    private sealed record InstallerProcessResult(int ExitCode, string Output);
}
