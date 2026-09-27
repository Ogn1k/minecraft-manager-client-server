using System.Net;
using MinecraftManager.Server.Application.Abstractions;
using MinecraftManager.Server.Domain.Common;

namespace MinecraftManager.Server.Infrastructure.Imports;

public sealed class CustomUrlImporter(HttpClient client, RemoteUrlPolicy policy, IFileStorage storage)
{
    public async Task<RemoteImportResult> ImportAsync(Uri original, string? expectedHash, long maximumBytes, CancellationToken cancellationToken)
    {
        var current = original;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            await policy.ValidateAsync(current, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (response.Headers.Location is null) throw new DomainRuleException("import_redirect_invalid", "The import redirect is invalid.");
                current = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximumBytes) throw new DomainRuleException("file_too_large", "The remote file exceeds the configured limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var blob = await storage.StoreVerifiedAsync(stream, maximumBytes, expectedHash, response.Content.Headers.ContentLength, cancellationToken);
            return new(blob, current, null, null);
        }
        throw new DomainRuleException("too_many_redirects", "The import URL redirected too many times.");
    }
}
