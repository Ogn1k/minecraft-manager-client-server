using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using MinecraftManager.Core.Diagnostics;
using MinecraftManager.Core.Persistence;

namespace MinecraftManager.Infrastructure.Diagnostics;

public sealed class DiagnosticsService(IApplicationPaths paths, IConfigurationStore configuration) : IDiagnosticsService
{
    public async Task<string> ExportAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(paths.DataDirectory, "diagnostics"));
        var output = Path.Combine(paths.DataDirectory, "diagnostics", $"minecraft-manager-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
        await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
        var config = await configuration.LoadAsync(ct);
        var sanitized = new
        {
            config.SchemaVersion,
            instances = config.Instances.Select(x => new { x.Id, x.DisplayName, rootPath = "<redacted>", sourceType = x.Pack?.Source.GetType().Name }),
            serverProfiles = config.ServerProfiles.Select(x => new { x.Id, x.DisplayName, server = x.BaseUri.GetLeftPart(UriPartial.Authority), x.DeviceId }),
            config.Settings
        };
        await WriteJsonAsync(archive, "sanitized-config.json", sanitized, ct);
        await WriteJsonAsync(archive, "environment.json", new
        {
            clientVersion = typeof(DiagnosticsService).Assembly.GetName().Version?.ToString(),
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            runtime = RuntimeInformation.FrameworkDescription
        }, ct);
        if (Directory.Exists(paths.LogDirectory))
        {
            foreach (var log in Directory.EnumerateFiles(paths.LogDirectory, "*.log").Take(5))
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry("logs/" + Path.GetFileName(log), CompressionLevel.Fastest);
                await using var destination = entry.Open(); await using var source = File.OpenRead(log);
                await source.CopyToAsync(destination, ct);
            }
        }
        return output;
    }

    private static async Task WriteJsonAsync<T>(ZipArchive archive, string name, T value, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open(); await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }, ct);
    }
}
