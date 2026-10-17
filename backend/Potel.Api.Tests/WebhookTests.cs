using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Potel.Api.Workflows;
using static Potel.Api.Tests.TestApi;
using static Potel.Api.Tests.WorkflowKit;

namespace Potel.Api.Tests;

// Het webhookblok mag niet bij de server zelf, het interne netwerk of de cloudmetadata kunnen (SSRF).
public class WebhookTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    const string Blocked = "niet bereikbaar of niet toegestaan";

    [Fact]
    public async Task Webhook_cannot_reach_a_service_on_the_server_itself_or_scan_ports()
    {
        await using var intern = await LocalServer.StartAsync();
        var tenant = await RegisterAsync(factory, "ssrf-loopback@example.com");

        var open = await RunWebhookAsync(tenant, intern.Url + "/admin/flush");
        var closed = await RunWebhookAsync(tenant, $"http://127.0.0.1:{ClosedPort()}/");

        Assert.True(intern.Hits == 0, $"De webhook bereikte een interne dienst. Log open poort: '{open.LastMessage}', dichte poort: '{closed.LastMessage}'");
        Assert.Equal("fout", open.Status);
        // Een open en een dichte poort zien er hetzelfde uit, dus je kunt er geen poorten mee aftasten.
        Assert.Equal(open.LastMessage, closed.LastMessage);
        Assert.Contains("poort 80", open.LastMessage);
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://172.16.5.4/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("https://[fd00:ec2::254]/")]
    public async Task Webhook_refuses_internal_addresses_with_a_plain_message(string url)
    {
        var tenant = await RegisterAsync(factory, $"ssrf-{Guid.NewGuid():N}@example.com");
        var run = await RunWebhookAsync(tenant, url);
        Assert.Equal("fout", run.Status);
        Assert.Contains(Blocked, run.LastMessage);
        Assert.DoesNotContain("refused", run.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timeout", run.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.100.100.200")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("192.0.0.192")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::127.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:a00:1::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("fc00::1")]
    [InlineData("ff02::1")]
    public void Guard_blocks_internal_and_metadata_addresses(string ip) => Assert.True(WebhookGuard.IsBlocked(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.215.14")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2002:808:808::1")]
    public void Guard_allows_public_addresses(string ip) => Assert.False(WebhookGuard.IsBlocked(IPAddress.Parse(ip)));

    [Fact]
    public async Task Guard_checks_the_address_a_host_name_resolves_to()
    {
        using var client = new HttpClient(WebhookGuard.CreateHandler(allowPrivate: false));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://localhost/"));
        Assert.IsType<WebhookBlockedException>(ex.InnerException);
    }

    [Fact]
    public async Task Webhook_only_allows_http_and_https_on_the_standard_ports()
    {
        var tenant = await RegisterAsync(factory, "ssrf-poort@example.com");
        Assert.Contains("poort 80", (await RunWebhookAsync(tenant, "https://example.com:8443/hook")).LastMessage);
        Assert.Contains("http of https", (await RunWebhookAsync(tenant, "ftp://example.com/hook")).LastMessage);
    }
}

// Een eigen server mag webhooks naar het eigen netwerk toestaan; de overige regels blijven gelden.
public class PrivateWebhookFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Workflows:AllowPrivateWebhooks", "true");
    }
}

public class PrivateWebhookTests(PrivateWebhookFactory factory) : IClassFixture<PrivateWebhookFactory>
{
    [Fact]
    public async Task Self_hosters_can_allow_webhooks_to_their_own_network()
    {
        await using var server = await LocalServer.StartAsync();
        var tenant = await RegisterAsync(factory, "eigen-netwerk@example.com");
        var run = await RunWebhookAsync(tenant, server.Url + "/hook");
        Assert.Equal("klaar", run.Status);
        Assert.Equal(1, server.Hits);
        Assert.Equal("Verstuurd naar 127.0.0.1 (200)", run.LastMessage);
    }

    [Fact]
    public async Task Failed_webhook_shows_a_plain_message_without_internal_error_text()
    {
        var tenant = await RegisterAsync(factory, "dichte-poort@example.com");
        var run = await RunWebhookAsync(tenant, $"http://127.0.0.1:{ClosedPort()}/");
        Assert.Equal("fout", run.Status);
        Assert.Contains("niet bereikbaar of niet toegestaan", run.LastMessage);
        Assert.DoesNotContain("refused", run.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Connection", run.LastMessage);
    }

    [Fact]
    public async Task Webhook_does_not_follow_redirects()
    {
        await using var target = await LocalServer.StartAsync();
        await using var redirect = await LocalServer.StartAsync(ctx =>
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers.Location = target.Url + "/geheim";
            return Task.CompletedTask;
        });
        var tenant = await RegisterAsync(factory, "doorsturen@example.com");
        var run = await RunWebhookAsync(tenant, redirect.Url + "/hook");
        Assert.Equal(1, redirect.Hits);
        Assert.Equal(0, target.Hits);
        Assert.Equal("fout", run.Status);
        Assert.Contains("302", run.LastMessage);
    }

    [Fact]
    public async Task Workspaces_do_not_share_webhook_cookies()
    {
        await using var server = await LocalServer.StartAsync(async ctx =>
        {
            ctx.Response.Headers.SetCookie = "sessie=van-werkruimte-a; Path=/";
            await ctx.Response.WriteAsync("ok");
        });
        var a = await RegisterAsync(factory, "koekje-a@example.com");
        var b = await RegisterAsync(factory, "koekje-b@example.com");
        await RunWebhookAsync(a, server.Url + "/hook");
        await RunWebhookAsync(b, server.Url + "/hook");
        Assert.Equal(2, server.Hits);
        Assert.All(server.Cookies, c => Assert.Equal("", c));
    }

    [Fact]
    public async Task Webhook_loop_stops_after_a_few_calls_in_one_run()
    {
        await using var server = await LocalServer.StartAsync();
        var tenant = await RegisterAsync(factory, "webhook-lus@example.com");
        var graph = GraphJson([Node("s", "trigger.manual"), Node("w", "action.webhook", new() { ["url"] = server.Url + "/lus" })], [Edge("s", "w"), Edge("w", "w")]);
        var run = await RunAsync(tenant, await CreateWorkflowAsync(tenant, "Lus", graph));
        Assert.True(server.Hits <= 10, $"Eén run van een webhook-lus deed {server.Hits} verzoeken");
        Assert.Equal("fout", run.Status);
        Assert.Contains("Gestopt", run.LastMessage);
    }
}
