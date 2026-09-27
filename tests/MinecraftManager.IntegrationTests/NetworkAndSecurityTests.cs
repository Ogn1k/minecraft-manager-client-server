using System.Net;
using System.Text;
using MinecraftManager.Core.Filesystem;
using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Security;
using MinecraftManager.Infrastructure.Persistence;
using MinecraftManager.Infrastructure.Security;
using MinecraftManager.Infrastructure.Sources;

namespace MinecraftManager.IntegrationTests;

public sealed class NetworkAndSecurityTests
{
    [Fact]
    public void StaticSourceRejectsPublicPlainHttp()
    {
        using var client = new HttpClient(new FixtureHandler("{}"));
        Assert.Throws<ArgumentException>(() => new HttpUpdateSource(client, new Uri("http://example.com/pack/"), false, new ManifestParser(), new Version(1, 0)));
    }

    [Fact]
    public async Task StaticSourceParsesHttpsManifestAndEncodesFileSegments()
    {
        const string json = """
        {"schemaVersion":1,"packId":"main","packVersion":"1.0","managedPaths":["mods/"],"files":[{"path":"mods/a file.jar","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":3}]}
        """;
        var handler = new FixtureHandler(json);
        using var client = new HttpClient(handler);
        await using var source = new HttpUpdateSource(client, new Uri("https://example.com/pack/"), false, new ManifestParser(), new Version(1, 0));
        var manifest = await source.GetManifestAsync(new(), default);
        Assert.NotNull(manifest.Manifest);
        await using var content = await source.OpenFileAsync(manifest.Manifest.Value.Files[0], default);
        Assert.Equal("/pack/files/mods/a%20file.jar", handler.LastRequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task CredentialStoreIsProtectedOrFailsClosed()
    {
        using var temp = new TemporaryDirectory();
        ISecureCredentialStore store = new PlatformCredentialStore(new ApplicationPaths(temp.Path));
        var id = Guid.NewGuid();
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(store.IsAvailable);
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() => store.SetAsync(id, new("access", DateTimeOffset.UtcNow, "refresh"), default));
            return;
        }
        await store.SetAsync(id, new("access", DateTimeOffset.UtcNow.AddMinutes(5), "refresh"), default);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(temp.Path, "credentials", id.ToString("N") + ".bin"));
        Assert.DoesNotContain("refresh", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal("access", (await store.GetAsync(id, default))!.AccessToken);
        await store.RemoveAsync(id, default);
        Assert.Null(await store.GetAsync(id, default));
    }

    private sealed class FixtureHandler(string manifest) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            var isManifest = request.RequestUri!.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal);
            var content = new StringContent(isManifest ? manifest : "abc", Encoding.UTF8, isManifest ? "application/json" : "application/octet-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }
}
