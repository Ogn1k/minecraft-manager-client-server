using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MinecraftManager.Server.Application.Abstractions;
using MinecraftManager.Server.Domain.Common;

namespace MinecraftManager.Server.Infrastructure.Imports;

public sealed record RemoteImportResult(StoredObject Blob, Uri Source, string? ProjectId, string? VersionId);

public sealed class ModrinthService(HttpClient client, IFileStorage storage)
{
    public async Task<JsonDocument> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 100) throw new DomainRuleException("invalid_query", "Search query is invalid.");
        using var response = await client.GetAsync($"search?query={Uri.EscapeDataString(query)}&limit=20", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
    }

    public async Task<RemoteImportResult> ImportAsync(Uri url, string? expectedHash, long maxBytes, CancellationToken cancellationToken)
    {
        if (!url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || !(url.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith(".modrinthcdn.com", StringComparison.OrdinalIgnoreCase)))
            throw new DomainRuleException("upstream_not_allowed", "The selected Modrinth file URL is not allowed.");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var blob = await storage.StoreVerifiedAsync(stream, maxBytes, expectedHash, response.Content.Headers.ContentLength, cancellationToken);
        return new(blob, url, null, null);
    }
}
