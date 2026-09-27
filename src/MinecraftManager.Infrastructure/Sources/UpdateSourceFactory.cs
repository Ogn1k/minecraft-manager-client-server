using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Updates;
using MinecraftManager.Core.Filesystem;

namespace MinecraftManager.Infrastructure.Sources;

public sealed class UpdateSourceFactory(
    IHttpClientFactory clients,
    IManifestParser parser,
    ISafePathResolver paths,
    MinecraftManager.Core.Persistence.IApplicationPaths applicationPaths,
    IManagedApiClient managedApi,
    Version clientVersion) : IUpdateSourceFactory
{
    public IUpdateSource Create(UpdateSourceSettings settings) => settings switch
    {
        LocalFolderSourceSettings local => new LocalFolderUpdateSource(local.FolderPath, parser, paths, clientVersion),
        LocalArchiveSourceSettings archive => new LocalArchiveUpdateSource(archive.ArchivePath, parser, paths, applicationPaths, clientVersion),
        StaticHttpSourceSettings http => new HttpUpdateSource(clients.CreateClient("updates"), http.BaseUri, http.AllowInsecureLan, parser, clientVersion),
        ManagedServerSourceSettings managed => new ManagedServerUpdateSource(managed, managedApi),
        _ => throw new NotSupportedException("The update source type is not supported.")
    };
}

public interface IManagedApiClient
{
    Task<ManifestEnvelope> GetManifestAsync(ManagedServerSourceSettings settings, ManifestRequest request, CancellationToken cancellationToken);
    Task<Stream> OpenFileAsync(ManagedServerSourceSettings settings, ManifestFile file, CancellationToken cancellationToken);
    Task ReportStatusAsync(Guid serverProfileId, Guid deploymentId, long sequence, string status, string? errorCode, CancellationToken cancellationToken);
}

public sealed class ManagedServerUpdateSource(ManagedServerSourceSettings settings, IManagedApiClient api) : IUpdateSource
{
    public string DisplayName => "Managed server";
    public string Identity => $"managed:{settings.ServerProfileId:N}:{settings.PackId}";
    public Task<ManifestEnvelope> GetManifestAsync(ManifestRequest request, CancellationToken ct) => api.GetManifestAsync(settings, request, ct);
    public Task<Stream> OpenFileAsync(ManifestFile file, CancellationToken ct) => api.OpenFileAsync(settings, file, ct);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
