using System.Net;
using System.Net.Sockets;

namespace MidFD.Services;

public static class NetworkSecurityPolicyService
{
    public const long MaxDownloadSizeBytes = 32 * 1024 * 1024; // 32 MiB
    public const int MaxRedirectHops = 5;

    public static bool IsPublicHttpUri(Uri? uri)
    {
        return IsWellFormedPublicHttpUri(uri);
    }

    public static bool IsWellFormedPublicHttpUri(Uri? uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        string host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (IPAddress.TryParse(host, out IPAddress? directIp))
        {
            return IsPublicIpAddress(directIp);
        }

        return true;
    }

    public static async Task<IPAddress[]> ResolveAndValidatePublicAddressesAsync(
        Uri? uri,
        CancellationToken cancellationToken = default,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null)
    {
        if (!IsWellFormedPublicHttpUri(uri) || uri == null)
        {
            return Array.Empty<IPAddress>();
        }

        string host = uri.Host;
        if (IPAddress.TryParse(host, out IPAddress? directIp))
        {
            if (!IsPublicIpAddress(directIp))
            {
                return Array.Empty<IPAddress>();
            }
            return new[] { GetNormalizedIpAddress(directIp) };
        }

        IPAddress[] resolved;
        try
        {
            if (dnsResolver != null)
            {
                resolved = await dnsResolver(host, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                resolved = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }

        if (resolved == null || resolved.Length == 0)
        {
            return Array.Empty<IPAddress>();
        }

        var validList = new List<IPAddress>(resolved.Length);
        foreach (IPAddress addr in resolved)
        {
            if (!IsPublicIpAddress(addr))
            {
                return Array.Empty<IPAddress>(); // Reject if ANY resolved address is non-public
            }
            validList.Add(GetNormalizedIpAddress(addr));
        }

        return validList.ToArray();
    }

    public static bool IsPublicIpAddress(IPAddress? address)
    {
        if (address == null)
        {
            return false;
        }

        address = GetNormalizedIpAddress(address);

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            byte b0 = bytes[0];
            byte b1 = bytes[1];
            byte b2 = bytes[2];

            // 0.0.0.0/8 (This network / unspecified)
            if (b0 == 0) return false;

            // 127.0.0.0/8 (Loopback)
            if (b0 == 127) return false;

            // 10.0.0.0/8 (Private)
            if (b0 == 10) return false;

            // 172.16.0.0/12 (Private: 172.16.0.0 - 172.31.255.255)
            if (b0 == 172 && b1 >= 16 && b1 <= 31) return false;

            // 192.168.0.0/16 (Private)
            if (b0 == 192 && b1 == 168) return false;

            // 169.254.0.0/16 (Link-local)
            if (b0 == 169 && b1 == 254) return false;

            // 100.64.0.0/10 (CGNAT / Shared address space: 100.64.0.0 - 100.127.255.255)
            if (b0 == 100 && b1 >= 64 && b1 <= 127) return false;

            // 192.0.0.0/24 (IETF Protocol Assignments)
            if (b0 == 192 && b1 == 0 && b2 == 0) return false;

            // 192.0.2.0/24 (TEST-NET-1 / Documentation)
            if (b0 == 192 && b1 == 0 && b2 == 2) return false;

            // 192.88.99.0/24 (6to4 Relay Anycast)
            if (b0 == 192 && b1 == 88 && b2 == 99) return false;

            // 198.18.0.0/15 (Benchmarking: 198.18.0.0 - 198.19.255.255)
            if (b0 == 198 && (b1 == 18 || b1 == 19)) return false;

            // 198.51.100.0/24 (TEST-NET-2 / Documentation)
            if (b0 == 198 && b1 == 51 && b2 == 100) return false;

            // 203.0.113.0/24 (TEST-NET-3 / Documentation)
            if (b0 == 203 && b1 == 0 && b2 == 113) return false;

            // 224.0.0.0/4 (Multicast: 224.0.0.0 - 239.255.255.255)
            if (b0 >= 224 && b0 <= 239) return false;

            // 240.0.0.0/4 (Reserved & Limited broadcast 255.255.255.255)
            if (b0 >= 240) return false;

            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
            {
                return false;
            }

            byte[] bytes = address.GetAddressBytes();

            // 100::/64 (Discard-Only Address Block: 0100:0000:0000:0000::/64)
            if (bytes[0] == 0x01 && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == 0x00 &&
                bytes[4] == 0x00 && bytes[5] == 0x00 && bytes[6] == 0x00 && bytes[7] == 0x00) return false;

            // 2001:db8::/32 (Documentation)
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) return false;

            // IPv6 Link-local: fe80::/10
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) return false;

            // IPv6 Site-local (Deprecated): fec0::/10
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0) return false;

            // IPv6 ULA: fc00::/7 (fc00:: - fdff::)
            if ((bytes[0] & 0xfe) == 0xfc) return false;

            // IPv6 Multicast: ff00::/8
            if (bytes[0] == 0xff) return false;

            return true;
        }

        return false;
    }

    private static IPAddress GetNormalizedIpAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }
        return address;
    }
}
