using System.Net;
using System.Net.Sockets;

namespace Carteiro.Dispatch;

/// <summary>
/// Decides whether an IP address is a legitimate webhook destination.
/// </summary>
/// <remarks>
/// Carteiro makes HTTP requests to URLs supplied by API clients. Without a check, a client could
/// register <c>http://169.254.169.254/</c> or <c>http://localhost:5432/</c> and use the service
/// as a proxy into the network it runs in (server-side request forgery). Anything that is not a
/// globally routable unicast address is refused unless private networks are explicitly allowed.
/// </remarks>
public static class SsrfGuard
{
    private static readonly (IPAddress Network, int PrefixLength)[] BlockedNetworks =
    [
        // IPv4
        (IPAddress.Parse("0.0.0.0"), 8),        // "this network"
        (IPAddress.Parse("10.0.0.0"), 8),       // private
        (IPAddress.Parse("100.64.0.0"), 10),    // carrier-grade NAT
        (IPAddress.Parse("127.0.0.0"), 8),      // loopback
        (IPAddress.Parse("169.254.0.0"), 16),   // link-local, cloud metadata services
        (IPAddress.Parse("172.16.0.0"), 12),    // private
        (IPAddress.Parse("192.0.0.0"), 24),     // IETF protocol assignments
        (IPAddress.Parse("192.0.2.0"), 24),     // documentation
        (IPAddress.Parse("192.168.0.0"), 16),   // private
        (IPAddress.Parse("198.18.0.0"), 15),    // benchmarking
        (IPAddress.Parse("198.51.100.0"), 24),  // documentation
        (IPAddress.Parse("203.0.113.0"), 24),   // documentation
        (IPAddress.Parse("224.0.0.0"), 4),      // multicast
        (IPAddress.Parse("240.0.0.0"), 4),      // reserved, includes broadcast

        // IPv6
        (IPAddress.Parse("::"), 127),           // unspecified (::) and loopback (::1)
        (IPAddress.Parse("64:ff9b::"), 96),     // NAT64, can map to private IPv4
        (IPAddress.Parse("2001:db8::"), 32),    // documentation
        (IPAddress.Parse("fc00::"), 7),         // unique local
        (IPAddress.Parse("fe80::"), 10),        // link-local
        (IPAddress.Parse("ff00::"), 8),         // multicast
    ];

    /// <summary>
    /// True if the address is publicly routable and therefore safe to deliver to by default.
    /// </summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // ::ffff:127.0.0.1 is still loopback. Judge the IPv4 address it wraps.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return false;
        }

        foreach (var (network, prefixLength) in BlockedNetworks)
        {
            if (network.AddressFamily == address.AddressFamily && IsInNetwork(address, network, prefixLength))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Cheap check done when an endpoint is registered, to reject obviously internal URLs early
    /// with a clear error. It only looks at the URL text. Host names that resolve to private
    /// addresses are caught later, at connect time, which is the check that actually matters.
    /// </summary>
    public static bool IsObviouslyInternal(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (url.IsLoopback)
        {
            return true;
        }

        var host = url.Host.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && IPAddress.TryParse(url.Host.Trim('[', ']'), out var address)
            && !IsPublicAddress(address);
    }

    private static bool IsInNetwork(IPAddress address, IPAddress network, int prefixLength)
    {
        Span<byte> addressBytes = stackalloc byte[16];
        Span<byte> networkBytes = stackalloc byte[16];
        address.TryWriteBytes(addressBytes, out _);
        network.TryWriteBytes(networkBytes, out _);

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        if (!addressBytes[..fullBytes].SequenceEqual(networkBytes[..fullBytes]))
        {
            return false;
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }
}
