using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// Achter een proxy, met een strenge limiet. De testserver kent geen echt IP-adres; de header X-Test-Peer
// speelt het adres van de machine die direct met de app praat (de proxy of een aanvaller).
public class BehindProxyFactory : PortalFactory
{
    sealed class FakePeer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Parse(ctx.Request.Headers["X-Test-Peer"].FirstOrDefault() ?? "198.51.100.20");
                return nextMiddleware(ctx);
            });
            next(app);
        };
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("BehindProxy", "true");
        builder.UseSetting("RateLimit:AuthPerMinute", "3");
        builder.UseSetting("RateLimit:LoginFailuresPerEmailPerHour", "5");
        builder.UseSetting("KnownProxies:0", "10.0.0.5");
        builder.UseSetting("KnownNetworks:0", "172.16.0.0/12");
        builder.ConfigureServices(s => s.AddSingleton<IStartupFilter, FakePeer>());
    }
}

public class LoginLimitTests(BehindProxyFactory factory) : IClassFixture<BehindProxyFactory>
{
    async Task<List<HttpStatusCode>> FailedLogins(int count, string peer, Func<int, string> forwardedFor, Func<int, string> email)
    {
        var client = factory.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 1; i <= count; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { email = email(i), password = $"fout{i}" }) };
            req.Headers.Add("X-Test-Peer", peer);
            req.Headers.Add("X-Forwarded-For", forwardedFor(i));
            codes.Add((await client.SendAsync(req)).StatusCode);
        }
        return codes;
    }

    // Inloggen via de bekende proxy 127.0.0.1 vanaf het echte IP-adres "from".
    async Task<HttpResponseMessage> LoginVia(string from, string email, string password)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { email, password }) };
        req.Headers.Add("X-Test-Peer", "127.0.0.1");
        req.Headers.Add("X-Forwarded-For", from);
        return await factory.CreateClient().SendAsync(req);
    }

    [Fact]
    public async Task Forwarded_for_from_an_unknown_peer_is_ignored()
    {
        // Elke poging een ander verzonnen IP-adres en een ander e-mailadres: de limiet per IP moet toch gelden.
        var codes = await FailedLogins(6, "198.51.100.20", i => $"203.0.113.{i}", i => $"onbekend{i}@example.com");
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }

    [Fact]
    public async Task Rotating_ip_addresses_cannot_bypass_the_limit_per_email()
    {
        // De eigenaar logde eerder in vanaf 192.0.2.250.
        Assert.Equal(HttpStatusCode.OK, (await LoginVia("192.0.2.250", "admin@potel.nl", "welkom123")).StatusCode);

        // Zelfs via een bekende proxy met steeds een ander echt IP-adres: hetzelfde e-mailadres blokkeert na een paar fouten.
        var codes = await FailedLogins(6, "127.0.0.1", i => $"192.0.2.{i}", _ => "admin@potel.nl");
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);

        // Vanaf een nieuw adres komt dan ook het goede wachtwoord er even niet door.
        var res = await LoginVia("192.0.2.200", " Admin@Potel.nl ", "welkom123");
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Contains("uur", await res.ErrorAsync());

        // Maar wel vanaf een adres waar de eigenaar eerder inlogde: zo sluit niemand hem buiten.
        Assert.Equal(HttpStatusCode.OK, (await LoginVia("192.0.2.250", "admin@potel.nl", "welkom123")).StatusCode);
    }

    [Fact]
    public async Task Known_proxies_are_trusted_but_only_for_the_last_hop()
    {
        // Via een bekende proxy telt het adres dat de proxy zelf toevoegde (het laatste), niet wat de client ervoor zette.
        var spoofed = await FailedLogins(6, "10.0.0.5", i => $"203.0.113.{i}, 198.51.100.77", i => $"hop{i}@example.com");
        Assert.Contains(HttpStatusCode.TooManyRequests, spoofed);

        // Verschillende echte klanten achter de proxy (uit het Docker-netwerk) hebben elk hun eigen limiet.
        var clients = await FailedLogins(6, "172.18.0.2", i => $"198.51.100.{100 + i}", i => $"klant{i}@example.com");
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, clients);
    }
}

// Een vreemde die expres foute wachtwoorden probeert, blokkeert alleen zichzelf, niet de eigenaar van het account.
public class LoginLockoutTests(BehindProxyFactory factory) : IClassFixture<BehindProxyFactory>
{
    static HttpRequestMessage Login(string peer, string email, string password)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { email, password }) };
        req.Headers.Add("X-Test-Peer", peer);
        return req;
    }

    [Fact]
    public async Task A_stranger_cannot_lock_the_owner_out_with_a_few_bad_passwords()
    {
        var attacker = factory.CreateClient();
        for (var i = 0; i < 3; i++) await attacker.SendAsync(Login("198.51.100.66", "admin@potel.nl", $"fout{i}"));
        var blocked = await attacker.SendAsync(Login("198.51.100.66", "admin@potel.nl", "welkom123"));
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Contains("minuut", await blocked.ErrorAsync());

        var owner = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await owner.SendAsync(Login("203.0.113.7", "admin@potel.nl", "welkom123"))).StatusCode);
    }
}
