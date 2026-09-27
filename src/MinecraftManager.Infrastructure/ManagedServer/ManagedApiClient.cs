using System.Net.Http.Headers;
using System.Net.Http.Json;
using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Security;
using MinecraftManager.Core.Updates;
using MinecraftManager.Infrastructure.Sources;

namespace MinecraftManager.Infrastructure.ManagedServer;

public sealed class ManagedApiClient(
    IHttpClientFactory clients,
    IConfigurationStore configuration,
    ISecureCredentialStore credentials,
    IManifestParser parser) : IManagedApiClient, IClientRegistrationService
{
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    public async Task<ManifestEnvelope> GetManifestAsync(ManagedServerSourceSettings settings, ManifestRequest request, CancellationToken ct)
    {
        var (profile, client) = await CreateAuthenticatedClientAsync(settings.ServerProfileId, ct);
        var assignments = await client.GetFromJsonAsync<AssignmentDto[]>(new Uri(profile.BaseUri, "/api/v1/client/assignments"), ct) ?? [];
        var assignment = assignments.FirstOrDefault(x =>
                (settings.PackId is null || x.PackId == settings.PackId) &&
                (settings.ClientInstanceId is null || x.ClientInstanceId == settings.ClientInstanceId))
            ?? throw new InvalidOperationException("No pack is assigned to this instance.");
        using var response = await client.GetAsync(new Uri(profile.BaseUri, $"/api/v1/packs/{Uri.EscapeDataString(assignment.PackId)}/versions/{Uri.EscapeDataString(assignment.PackVersion)}/manifest"), ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await parser.ParseAsync(stream, typeof(ManagedApiClient).Assembly.GetName().Version ?? new Version(1, 0), ct);
        if (!result.IsValid) throw new InvalidDataException(string.Join("; ", result.Errors.Select(x => x.Message)));
        return new(result.Manifest, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified, false, assignment.DeploymentId);
    }

    public async Task<Stream> OpenFileAsync(ManagedServerSourceSettings settings, ManifestFile file, CancellationToken ct)
    {
        var fileId = file.Download?.FileId ?? file.Id ?? throw new InvalidDataException("Managed file has no server file identifier.");
        var (profile, client) = await CreateAuthenticatedClientAsync(settings.ServerProfileId, ct);
        using var response = await client.PostAsync(new Uri(profile.BaseUri, $"/api/v1/files/{Uri.EscapeDataString(fileId)}/download-ticket"), null, ct);
        response.EnsureSuccessStatusCode();
        var ticket = await response.Content.ReadFromJsonAsync<DownloadTicketDto>(cancellationToken: ct) ?? throw new InvalidDataException("Download ticket response is empty.");
        var downloadResponse = await clients.CreateClient("downloads").GetAsync(ticket.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        downloadResponse.EnsureSuccessStatusCode();
        return new OwnedResponseStream(await downloadResponse.Content.ReadAsStreamAsync(ct), downloadResponse);
    }

    public async Task ReportStatusAsync(Guid serverProfileId, Guid deploymentId, long sequence, string status, string? errorCode, CancellationToken ct)
    {
        var profile = await GetProfileAsync(serverProfileId, ct);
        var (_, client) = await CreateAuthenticatedClientAsync(profile.Id, ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(profile.BaseUri, $"/api/v1/client/deployments/{deploymentId}/status"))
        { Content = JsonContent.Create(new { sequence, status, error = errorCode }) };
        request.Headers.Add("Idempotency-Key", $"{deploymentId:N}-{sequence}");
        using var response = await client.SendAsync(request, ct); response.EnsureSuccessStatusCode();
    }

    public async Task<Guid> RegisterAsync(Guid serverProfileId, string registrationCode, string displayName, CancellationToken ct)
    {
        if (!credentials.IsAvailable) throw new PlatformNotSupportedException("Secure credential storage is unavailable.");
        var profile = await GetProfileAsync(serverProfileId, ct);
        var client = clients.CreateClient("managed");
        using var response = await client.PostAsJsonAsync(new Uri(profile.BaseUri, "/api/v1/client-registrations/complete"), new { registrationCode, displayName }, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RegistrationDto>(cancellationToken: ct) ?? throw new InvalidDataException("Registration response is empty.");
        await credentials.SetAsync(serverProfileId, new(result.AccessToken, result.AccessTokenExpiresAtUtc, result.RefreshCredential), ct);
        return result.MachineId;
    }

    public async Task RevokeAsync(Guid serverProfileId, CancellationToken ct) => await credentials.RemoveAsync(serverProfileId, ct);

    private async Task<(ServerProfile Profile, HttpClient Client)> CreateAuthenticatedClientAsync(Guid id, CancellationToken ct)
    {
        await refreshGate.WaitAsync(ct);
        try
        {
            var profile = await GetProfileAsync(id, ct);
            var credential = await credentials.GetAsync(id, ct) ?? throw new UnauthorizedAccessException("This server profile is not registered.");
            if (credential.AccessTokenExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                var refreshClient = clients.CreateClient("managed");
                using var response = await refreshClient.PostAsJsonAsync(new Uri(profile.BaseUri, "/api/v1/client-sessions/token"), new { refreshCredential = credential.RefreshCredential }, ct);
                response.EnsureSuccessStatusCode();
                var refreshed = await response.Content.ReadFromJsonAsync<TokenDto>(cancellationToken: ct) ?? throw new InvalidDataException("Token response is empty.");
                credential = new(refreshed.AccessToken, refreshed.AccessTokenExpiresAtUtc, refreshed.RefreshCredential);
                await credentials.SetAsync(id, credential, ct);
            }
            var client = clients.CreateClient("managed");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
            return (profile, client);
        }
        finally { refreshGate.Release(); }
    }

    private async Task<ServerProfile> GetProfileAsync(Guid id, CancellationToken ct)
    {
        var profile = (await configuration.LoadAsync(ct)).ServerProfiles.SingleOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Server profile was not found.");
        if (!profile.BaseUri.IsAbsoluteUri || profile.BaseUri.Scheme != "https" || profile.BaseUri.UserInfo.Length > 0 || profile.BaseUri.Query.Length > 0 || profile.BaseUri.Fragment.Length > 0)
            throw new InvalidOperationException("Managed server profiles require a clean HTTPS base URL.");
        return profile;
    }

    private sealed record AssignmentDto(Guid ClientInstanceId, string PackId, string PackVersion, Guid? DeploymentId);
    private sealed record DownloadTicketDto(Uri Url, DateTimeOffset ExpiresAtUtc, long Size, string Sha256);
    private sealed record RegistrationDto(Guid MachineId, string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc, string RefreshCredential);
    private sealed record TokenDto(string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc, string RefreshCredential);

    private sealed class OwnedResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush(); public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c);
        public override long Seek(long o, SeekOrigin origin) => inner.Seek(o, origin); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) => inner.ReadAsync(b, ct);
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); response.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); response.Dispose(); GC.SuppressFinalize(this); }
    }
}
