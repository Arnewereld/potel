using System.Net;
using System.Net.Sockets;

namespace Potel.Api.Workflows;

// Webhooks gaan alleen naar openbare adressen op poort 80 of 443: nooit naar de server zelf, het interne netwerk
// of de metadata van een cloud (SSRF). Het IP-adres wordt gecontroleerd ná het opzoeken van de naam, en de
// verbinding gaat naar precies dat adres, zodat een naam die later anders uitkomt er niet langs kan.
public static class WebhookGuard
{
    public const string ClientName = "webhooks";

    public static bool IsAllowedPort(int port) => port is 80 or 443;

    public static SocketsHttpHandler CreateHandler(bool allowPrivate) => new()
    {
        // Geen doorverwijzingen volgen, geen cookies tussen werkruimtes delen en geen proxy: we verbinden zelf met het gecontroleerde adres.
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MaxResponseHeadersLength = 16, // in KB
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, allowPrivate, ct),
    };

    static async ValueTask<Stream> ConnectAsync(DnsEndPoint target, bool allowPrivate, CancellationToken ct)
    {
        if (!allowPrivate && !IsAllowedPort(target.Port)) throw new WebhookBlockedException();
        var host = target.Host.Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0 || (!allowPrivate && addresses.Any(IsBlocked))) throw new WebhookBlockedException();

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, target.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // Adressen waar een webhook nooit heen mag.
    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] switch
            {
                0 => true,                                     // "dit netwerk", ook 0.0.0.0
                10 => true,                                    // privé
                127 => true,                                   // de server zelf
                100 => b[1] is >= 64 and <= 127,               // CGNAT, ook de metadata van Alibaba (100.100.100.200)
                169 => b[1] == 254,                            // link-local, ook de cloudmetadata op 169.254.169.254
                172 => b[1] is >= 16 and <= 31,                // privé
                192 => b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2), // privé, IETF, documentatie
                198 => b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100), // benchmark, documentatie
                203 => b[1] == 0 && b[2] == 113,               // documentatie
                >= 224 => true,                                // multicast, gereserveerd en broadcast
                _ => false,
            };
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return true;

        if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback)) return true;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
        if ((b[0] & 0xfe) == 0xfc) return true;                // unique local (fc00::/7), ook de AWS-metadata fd00:ec2::254
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return true; // documentatie
        // Een IPv4-adres verpakt in IPv6: ::a.b.c.d, NAT64 (64:ff9b::/96) en 6to4 (2002::/16).
        if (b.Take(12).All(x => x == 0)) return IsBlocked(new IPAddress(b[12..]));
        if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0)) return IsBlocked(new IPAddress(b[12..]));
        if (b[0] == 0x20 && b[1] == 0x02) return IsBlocked(new IPAddress(b[2..6]));
        return false;
    }
}

public class WebhookBlockedException() : Exception("Dit adres is niet toegestaan voor een webhook");
