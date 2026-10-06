using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Endpoints;
using Potel.Api.Workflows;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// Een proef is gratis: met veel proefwerkruimtes vermenigvuldigt niemand de daglimiet voor e-mail.
public class TrialMailCapFactory : PortalFactory
{
    public RecordingEmailSender Mail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Workflows:EmailsPerDayTrial", "2");
        builder.UseSetting("Workflows:EmailsPerDayAllTrials", "3");
        builder.ConfigureTestServices(s => s.AddSingleton<IEmailSender>(Mail));
    }
}

public class TrialMailCapTests(TrialMailCapFactory factory) : IClassFixture<TrialMailCapFactory>
{
    static string TwoMails(string to) => WorkflowKit.GraphJson(
        [WorkflowKit.Node("s", "trigger.manual"),
         WorkflowKit.Node("m1", "action.email", new() { ["to"] = to, ["subject"] = "Hoi", ["body"] = "x" }),
         WorkflowKit.Node("m2", "action.email", new() { ["to"] = to, ["subject"] = "Hoi", ["body"] = "x" })],
        [WorkflowKit.Edge("s", "m1"), WorkflowKit.Edge("m1", "m2")]);

    int SentTo(string to) => factory.Mail.Sent.Count(m => m.To == to);

    [Fact]
    public async Task Many_free_workspaces_together_stay_under_the_platform_trial_limit()
    {
        WorkflowKit.Run? last = null;
        for (var i = 0; i < 4; i++)
        {
            var tenant = await RegisterAsync(factory, $"sybil{i}@example.com");
            last = await WorkflowKit.RunAsync(tenant, await WorkflowKit.CreateWorkflowAsync(tenant, "Mail", TwoMails("slachtoffer@example.org")));
        }
        Assert.Equal(3, SentTo("slachtoffer@example.org"));
        Assert.Contains(last!.Messages, m => m.Contains("proefperiode"));

        // Een werkruimte met een abonnement telt niet mee voor de proeflimiet en kan gewoon mailen.
        var paid = await RegisterAsync(factory, "betaald@example.com");
        await SetPlanAsync(factory, await WorkspaceIdAsync(paid), "team");
        await WorkflowKit.RunAsync(paid, await WorkflowKit.CreateWorkflowAsync(paid, "Mail", TwoMails("klant@example.org")));
        Assert.Equal(2, SentTo("klant@example.org"));
    }
}

// Aanmelden vanaf één netwerk: na een paar werkruimtes per uur is het even op, en het hele platform heeft ook een plafond.
public class SignupNetworkFactory : PortalFactory
{
    sealed class FakePeer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nextMiddleware) =>
            {
                if (ctx.Request.Headers["X-Test-Peer"].FirstOrDefault() is { } peer) ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                return nextMiddleware(ctx);
            });
            next(app);
        };
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimit:SignupsPerHour", "2");
        builder.UseSetting("RateLimit:SignupsPerHourTotal", "6");
        builder.ConfigureServices(s => s.AddSingleton<IStartupFilter, FakePeer>());
    }
}

public class SignupLimitTests(SignupNetworkFactory factory) : IClassFixture<SignupNetworkFactory>
{
    async Task<HttpResponseMessage> SignupFrom(string peer)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new { company = "Bedrijf", name = "Eigenaar", email = $"aanmelding-{Guid.NewGuid():N}@example.com", password = "geheim123", demoData = false }),
        };
        req.Headers.Add("X-Test-Peer", peer);
        return await factory.CreateClient().SendAsync(req);
    }

    [Fact]
    public async Task One_network_can_only_create_a_few_workspaces_per_hour_and_the_platform_has_a_ceiling()
    {
        // Twee per netwerk: een ander adres in dezelfde /24 of /64 telt mee.
        Assert.Equal(HttpStatusCode.Created, (await SignupFrom("198.51.100.10")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SignupFrom("198.51.100.11")).StatusCode);
        var refused = await SignupFrom("198.51.100.200");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains("netwerk", await refused.ErrorAsync());

        Assert.Equal(HttpStatusCode.Created, (await SignupFrom("2001:db8:1:2::1")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SignupFrom("2001:db8:1:2:ffff::9")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SignupFrom("2001:db8:1:2:abcd::5")).StatusCode);

        // Andere netwerken mogen nog, tot het plafond van het hele platform (6 per uur) bereikt is.
        Assert.Equal(HttpStatusCode.Created, (await SignupFrom("203.0.113.5")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SignupFrom("192.0.2.5")).StatusCode);
        var full = await SignupFrom("100.64.0.5");
        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        Assert.Contains("tegelijk", await full.ErrorAsync());
    }

    [Theory]
    [InlineData("198.51.100.7", "198.51.100.0/24")]
    [InlineData("::ffff:198.51.100.7", "198.51.100.0/24")]
    [InlineData("2001:db8:1:2:3:4:5:6", "20010DB800010002/64")]
    [InlineData(null, "onbekend")]
    public void Addresses_are_grouped_per_network(string? ip, string network) =>
        Assert.Equal(network, SignupThrottle.Network(ip is null ? null : IPAddress.Parse(ip)));
}
