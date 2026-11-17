using System.Net;
using System.Net.Sockets;

namespace Carteiro.Dispatch;

/// <summary>
/// Thrown from the connect callback when a webhook URL resolves to an address we refuse to call.
/// </summary>
public sealed class BlockedDestinationException(string host, IPAddress address)
    : Exception($"Destination blocked: '{host}' resolves to {address}, which is not a public address.")
{
}

/// <summary>
/// Builds the HTTP handler used for outgoing webhook requests.
/// </summary>
internal static class WebhookHttpHandler
{
    public static SocketsHttpHandler Create(DeliveryOptions options)
    {
        var allowPrivateNetworks = options.AllowPrivateNetworks;

        return new SocketsHttpHandler
        {
            // A redirect is not a successful delivery, and following one would let a public URL
            // bounce us to an internal one.
            AllowAutoRedirect = false,

            // With a proxy in between, the connect callback would see the proxy's address and
            // the destination check would be meaningless.
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = options.RequestTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, cancellationToken) =>
                ConnectAsync(context.DnsEndPoint, allowPrivateNetworks, cancellationToken),
        };
    }

    /// <summary>
    /// Resolves the host, validates every resolved address, then connects to exactly the
    /// addresses that were validated.
    /// </summary>
    /// <remarks>
    /// Validating the URL when the endpoint is registered is not enough: DNS can change afterwards,
    /// or deliberately answer with a public address first and a private one later (DNS rebinding).
    /// Doing the check here, on the address the socket is about to connect to, closes that gap.
    /// </remarks>
    private static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endpoint,
        bool allowPrivateNetworks,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);

        if (!allowPrivateNetworks)
        {
            // One bad address is enough to refuse. Connecting only to the public ones would
            // also be safe, but a host name that resolves to both is suspicious in itself.
            foreach (var address in addresses)
            {
                if (!SsrfGuard.IsPublicAddress(address))
                {
                    throw new BlockedDestinationException(endpoint.Host, address);
                }
            }
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
