using System.Net;
using System.Net.Http.Headers;
using MinecraftManager.Core.Filesystem;
using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Updates;

namespace MinecraftManager.Infrastructure.Sources;

public sealed class HttpUpdateSource(
    HttpClient client,
    Uri baseUri,
    bool allowInsecureLan,
    IManifestParser parser,
    Version clientVersion) : IUpdateSource
{
    private readonly Uri normalizedBase = ValidateBase(baseUri, allowInsecureLan);
    public string DisplayName => normalizedBase.Host;
    public string Identity => "http:" + normalizedBase.AbsoluteUri;

    public async Task<ManifestEnvelope> GetManifestAsync(ManifestRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(normalizedBase, "manifest.json"));
        if (request.KnownETag is { } etag) message.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
        if (request.IfModifiedSince is { } modified) message.Headers.IfModifiedSince = modified;
        using var response = await SendWithRetryAsync(message, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified) return new(null, request.KnownETag, request.IfModifiedSince, true);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var parsed = await parser.ParseAsync(stream, clientVersion, cancellationToken);
        if (!parsed.IsValid) throw new InvalidDataException(string.Join("; ", parsed.Errors.Select(x => x.Message)));
        return new(parsed.Manifest, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
    }

    public async Task<Stream> OpenFileAsync(ManifestFile file, CancellationToken cancellationToken)
    {
        var relative = RelativeManifestPath.Parse(file.SourcePath ?? file.Path);
        var encoded = string.Join('/', relative.Value.Split('/').Select(Uri.EscapeDataString));
        var response = await client.GetAsync(new Uri(normalizedBase, "files/" + encoded), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } size && size != file.Size)
        {
            response.Dispose();
            throw new InvalidDataException("HTTP file length does not match manifest.");
        }
        return new ResponseOwnedStream(await response.Content.ReadAsStreamAsync(cancellationToken), response);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage original, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(original.Method, original.RequestUri);
            foreach (var header in original.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            try
            {
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (attempt >= 2 || response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)) return response;
                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < 2) { }
            await Task.Delay(TimeSpan.FromMilliseconds((1 << attempt) * 500 + Random.Shared.Next(250)), ct);
        }
    }

    private static Uri ValidateBase(Uri uri, bool allowInsecureLan)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Length > 0 || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Update source must be an absolute HTTP(S) base URL without credentials, query, or fragment.");
        if (uri.Scheme == "http" && (!allowInsecureLan || !IsPrivateHost(uri.Host)))
            throw new ArgumentException("Plain HTTP is allowed only for an explicitly approved LAN source.");
        var text = uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/";
        return new Uri(text);
    }

    private static bool IsPrivateHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var ip) && (IPAddress.IsLoopback(ip) || ip.GetAddressBytes() is var bytes &&
            (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class ResponseOwnedStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush(); public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => await inner.ReadAsync(buffer, ct);
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); response.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); response.Dispose(); GC.SuppressFinalize(this); }
    }
}
