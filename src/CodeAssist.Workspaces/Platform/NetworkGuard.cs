using System.Net;
using System.Net.Sockets;

namespace CodeAssist.Workspaces.Platform;

/// <summary>SSRF protection: classifies addresses and builds an HTTP handler that refuses non-public targets at connect time (so DNS rebinding cannot bypass a pre-check).</summary>
public static class NetworkGuard
{
    public static bool IsLocalName(string host)
    {
        var name = host.TrimEnd('.').ToLowerInvariant();
        return name == "localhost" || name.EndsWith(".localhost", StringComparison.Ordinal) ||
               name.EndsWith(".local", StringComparison.Ordinal) || name.EndsWith(".internal", StringComparison.Ordinal);
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10 ||
                     (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                     (b[0] == 192 && b[1] == 168) ||
                     (b[0] == 169 && b[1] == 254) ||
                     (b[0] == 100 && b[1] is >= 64 and <= 127) ||
                     (b[0] == 192 && b[1] == 0 && b[2] == 0) ||
                     b[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC);
        }
        return false;
    }

    /// <summary>Handler that does not follow redirects itself (callers re-validate each hop) and connects only to allowed addresses.</summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivate)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseCookies = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = IPAddress.TryParse(host, out var literal)
                    ? [literal]
                    : await Dns.GetHostAddressesAsync(host, cancellationToken);
                var allowed = addresses.Where(a => allowPrivate || IsPublic(a)).ToArray();
                if (allowed.Length == 0)
                    throw new HttpRequestException($"'{host}' resolves to a non-public address, which is blocked.");

                Exception? last = null;
                foreach (var address in allowed)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                    {
                        socket.Dispose();
                        last = exception;
                        if (exception is OperationCanceledException) throw;
                    }
                }
                throw new HttpRequestException($"Could not connect to '{host}'.", last);
            },
        };
    }
}
