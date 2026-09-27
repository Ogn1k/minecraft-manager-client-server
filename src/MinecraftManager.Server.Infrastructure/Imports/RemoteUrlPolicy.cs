using System.Net;
using System.Net.Sockets;
using MinecraftManager.Server.Domain.Common;

namespace MinecraftManager.Server.Infrastructure.Imports;

public sealed class RemoteUrlPolicy
{
    public async Task ValidateAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) throw new DomainRuleException("import_url_invalid", "Only credential-free HTTP(S) import URLs are allowed.");
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(IsPrivate)) throw new DomainRuleException("import_url_blocked", "The import URL resolves to a private or local address.");
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] is 10 or 127 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] >= 224;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.Equals(IPAddress.IPv6Loopback) || (address.GetAddressBytes()[0] & 0xfe) == 0xfc;
        return true;
    }
}
