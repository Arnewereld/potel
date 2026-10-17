using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Integrations;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Een nagebootste GitHub-API: commits in het geheugen, gefilterd op repository, auteur en periode zoals GitHub dat doet.
// Er gaat nooit een verzoek het netwerk op.
public class FakeGitHub : HttpMessageHandler
{
    public record Commit(string Repo, string Sha, string Email, DateTime TimeUtc, string Message, int Parents = 1, bool IgnoresAuthorFilter = false);

    public readonly ConcurrentQueue<Commit> Commits = new();
    public readonly ConcurrentQueue<(Uri Uri, string? Auth, string UserAgent)> Requests = new();
    // Voor één test: een eigen antwoord, of null om gewoon de commits terug te geven.
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var uri = req.RequestUri!;
        Requests.Enqueue((uri, req.Headers.Authorization?.ToString(), req.Headers.UserAgent.ToString()));
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("api.github.com", uri.Host);
        if (Override?.Invoke(req) is { } custom) return Task.FromResult(custom);
        var m = Regex.Match(uri.AbsolutePath, "^/repos/([^/]+)/([^/]+)/commits$");
        if (!m.Success) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        var repo = $"{m.Groups[1].Value}/{m.Groups[2].Value}";
        var q = QueryHelpers.ParseQuery(uri.Query);
        var since = DateTime.Parse(q["since"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var until = DateTime.Parse(q["until"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        string author = q["author"]!;
        var list = Commits
            .Where(c => c.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase) && c.TimeUtc >= since && c.TimeUtc <= until)
            .Where(c => c.IgnoresAuthorFilter || c.Email.Equals(author, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.TimeUtc)
            .Select(c => new JsonObject
            {
                ["sha"] = c.Sha,
                ["html_url"] = $"https://github.com/{c.Repo}/commit/{c.Sha}",
                ["commit"] = new JsonObject
                {
                    ["author"] = new JsonObject { ["name"] = "Iemand", ["email"] = c.Email, ["date"] = c.TimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) },
                    ["message"] = c.Message,
                },
                ["parents"] = new JsonArray(Enumerable.Range(0, c.Parents).Select(i => (JsonNode)new JsonObject { ["sha"] = new string('0', 39) + i }).ToArray()),
            });
        return Task.FromResult(Json(HttpStatusCode.OK, new JsonArray(list.ToArray<JsonNode>())));
    }

    public static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}

public class GitHubFactory : PortalFactory
{
    public FakeGitHub GitHub { get; } = new();
    public MovableClock Clock { get; } = new(new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<BusinessClock>(Clock);
            s.AddHttpClient<GitHubClient>().ConfigurePrimaryHttpMessageHandler(() => GitHub).SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        });
    }
}

// Urenvoorstellen uit GitHub: het token, de repository-links, de schatting van de tijd en dat niets vanzelf geboekt wordt.
public class GitHubTests(GitHubFactory factory) : IClassFixture<GitHubFactory>
{
    const string Token = "github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTwxyz";
    static int shas;

    static string NewSha() => Interlocked.Increment(ref shas).ToString("x").PadLeft(40, 'c');

    // Een moment op een dag in oktober 2026, in Nederlandse tijd (zomertijd, UTC+2).
    static DateTime Local(int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, day, hour, minute, 0, DateTimeKind.Unspecified), BusinessClock.Zone);

    FakeGitHub.Commit Add(string repo, string email, DateTime utc, string message, int parents = 1, bool sneaky = false)
    {
        var c = new FakeGitHub.Commit(repo, NewSha(), email, utc, message, parents, sneaky);
        factory.GitHub.Commits.Enqueue(c);
        return c;
    }

    record Setup(HttpClient Admin, int Workspace, string Email, int Customer, string Repo);

    async Task<Setup> SetupAsync(string prefix, bool token = true)
    {
        factory.GitHub.Override = null;
        var email = $"{prefix}-{Guid.NewGuid():N}@example.com";
        var c = await TestApi.RegisterAsync(factory, email);
        var ws = await TestApi.WorkspaceIdAsync(c);
        var customer = await CustomerAsync(c);
        if (token) (await c.PutAsJsonAsync("/api/github/token", new { token = Token })).EnsureSuccessStatusCode();
        return new Setup(c, ws, email, customer, $"acme-{Guid.NewGuid():N}"[..20]);
    }

    static async Task<int> ProjectAsync(HttpClient c, int customer, string name, string? repoUrl, string status = "actief", string billing = "uur")
    {
        var res = await c.PostAsJsonAsync("/api/projects", new { name, customerId = customer, status, billing, hourlyRate = 95, fixedPrice = 1000, color = "#000000", repoUrl });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.JsonAsync()).GetProperty("id").GetInt32();
    }

    static Task<HttpResponseMessage> SuggestAsync(HttpClient c, string from, string to, int? projectId = null) =>
        c.GetAsync($"/api/github/suggestions?from={from}&to={to}{(projectId is { } p ? $"&projectId={p}" : "")}");

    static async Task<List<JsonElement>> SuggestionsAsync(HttpClient c, string from, string to, int? projectId = null)
    {
        var res = await SuggestAsync(c, from, to, projectId);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.JsonAsync()).GetProperty("suggestions").EnumerateArray().ToList();
    }

    static string[] Shas(JsonElement suggestion) => suggestion.GetProperty("commits").EnumerateArray().Select(c => c.GetProperty("sha").GetString()!).ToArray();

    [Theory]
    [InlineData("https://github.com/fietsplein/exact-sync", "fietsplein", "exact-sync")]
    [InlineData("https://github.com/fietsplein/exact-sync.git", "fietsplein", "exact-sync")]
    [InlineData("https://github.com/Fiets-Plein/exact.sync/", "Fiets-Plein", "exact.sync")]
    [InlineData("https://www.github.com/a/b", "a", "b")]
    [InlineData("  https://GitHub.com/a/b_c  ", "a", "b_c")]
    public void A_github_link_names_one_repository(string url, string owner, string name)
    {
        var repo = GitHubRepo.Parse(url);
        Assert.NotNull(repo);
        Assert.Equal(owner, repo.Owner);
        Assert.Equal(name, repo.Name);
    }

    [Theory]
    [InlineData("http://github.com/a/b")]
    [InlineData("https://github.com/a")]
    [InlineData("https://github.com/a/b/tree/main")]
    [InlineData("https://github.com/a/b?tab=readme")]
    [InlineData("https://github.com/a/b#readme")]
    [InlineData("https://github.com.evil.example/a/b")]
    [InlineData("https://evil.example/github.com/a/b")]
    [InlineData("https://user:wachtwoord@github.com/a/b")]
    [InlineData("https://github.com:8443/a/b")]
    [InlineData("https://github.com/a/..")]
    [InlineData("https://github.com/../b")]
    [InlineData("https://github.com/-a/b")]
    [InlineData("https://github.com/a--b/c")]
    [InlineData("https://api.github.com/repos/a/b")]
    [InlineData("https://github.com/a/b%2F..%2F..")]
    [InlineData("https://github.com/a/.git")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void Anything_else_is_not_a_github_repository(string url) => Assert.Null(GitHubRepo.Parse(url));

    [Fact]
    public async Task Projects_only_accept_plain_repository_links()
    {
        var s = await SetupAsync("github-links", token: false);
        async Task<HttpStatusCode> Create(string? url) =>
            (await s.Admin.PostAsJsonAsync("/api/projects", new { name = "P", customerId = s.Customer, status = "actief", billing = "uur", hourlyRate = 95, color = "#000000", repoUrl = url })).StatusCode;
        Assert.Equal(HttpStatusCode.Created, await Create("https://github.com/acme/webshop"));
        Assert.Equal(HttpStatusCode.Created, await Create("https://dev.azure.com/acme/portaal")); // geen GitHub, wel een gewone link
        Assert.Equal(HttpStatusCode.Created, await Create(null));
        Assert.Equal(HttpStatusCode.Created, await Create("  "));
        foreach (var bad in new[] { "javascript:alert(1)", "data:text/html,hoi", "ftp://acme.example/repo", "https://github.com/acme", "https://github.com/acme/webshop/tree/main", "https://iemand:geheim@gitlab.example/x/y", "geen link" })
            Assert.Equal(HttpStatusCode.BadRequest, await Create(bad));
        var res = await s.Admin.PostAsJsonAsync("/api/projects", new { name = "P", customerId = s.Customer, status = "actief", billing = "uur", hourlyRate = 95, color = "#000000", repoUrl = "https://github.com/acme/webshop/pulls" });
        Assert.Contains("https://github.com/eigenaar/repository", await res.ErrorAsync());

        // Een oude link van voor deze regels houdt het opslaan van de rest van het project niet tegen, zolang hij niet verandert.
        var old = await ProjectAsync(s.Admin, s.Customer, "Oud", null);
        WorkflowKit.InWorkspace(factory, s.Workspace, db => db.Projects.Where(p => p.Id == old).ExecuteUpdate(x => x.SetProperty(p => p.RepoUrl, "github.com/acme/oud")));
        object Update(string name, string? url) => new { name, customerId = s.Customer, status = "actief", billing = "uur", hourlyRate = 95, color = "#000000", repoUrl = url };
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PutAsJsonAsync($"/api/projects/{old}", Update("Oud en vernieuwd", "github.com/acme/oud"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PutAsJsonAsync($"/api/projects/{old}", Update("Oud en vernieuwd", "github.com/acme/ander"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PutAsJsonAsync($"/api/projects/{old}", Update("Oud en vernieuwd", "https://github.com/acme/oud"))).StatusCode);
    }

    [Fact]
    public void Duration_counts_the_time_between_commits_with_a_minimum_per_block_and_a_cap()
    {
        DateTime At(int h, int m = 0) => new(2026, 10, 12, h, m, 0, DateTimeKind.Utc);
        Assert.Equal(0, CommitDuration.Estimate([]));
        Assert.Equal(30, CommitDuration.Estimate([At(9)])); // één commit: het minimum
        Assert.Equal(105, CommitDuration.Estimate([At(10, 10), At(9), At(9, 40)])); // 30 + 40 + 30 = 100, naar boven op een kwartier
        Assert.Equal(150, CommitDuration.Estimate([At(9), At(11)])); // precies 2 uur telt nog mee
        Assert.Equal(90, CommitDuration.Estimate([At(9), At(9, 30), At(13)])); // meer dan 2 uur: een nieuw blok van 30 minuten
        Assert.Equal(45, CommitDuration.Estimate([At(9), At(9, 1), At(9, 2)])); // 32 minuten wordt 45
        Assert.Equal(CommitDuration.MaxMinutesPerDay, CommitDuration.Estimate(Enumerable.Range(6, 18).Select(h => At(h)))); // nooit meer dan 10 uur
    }

    [Fact]
    public void Description_uses_the_first_line_of_each_commit_once()
    {
        Assert.Equal("Checkout bouwen; Fix totaal", GitHubEndpoints.Describe(["Checkout bouwen\n\nLange uitleg", "Fix totaal\r\nmeer", "Checkout bouwen", "  "]));
        Assert.Equal(GitHubEndpoints.MaxDescription, GitHubEndpoints.Describe(Enumerable.Range(0, 200).Select(i => $"Commit nummer {i}")).Length);
    }

    [Fact]
    public async Task The_token_is_stored_encrypted_and_never_shown_again()
    {
        var s = await SetupAsync("github-token", token: false);
        var res = await s.Admin.PutAsJsonAsync("/api/github/token", new { token = $"  {Token} " });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Token, body);
        Assert.Equal("wxyz", (await res.JsonAsync()).GetProperty("token").GetProperty("last4").GetString());

        var settings = await (await s.Admin.GetAsync("/api/github/settings")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Token, settings);
        Assert.DoesNotContain("protected", settings, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wxyz", settings);

        // In de database staat het versleuteld; alleen de server kan het met zijn sleutels weer lezen.
        var stored = WorkflowKit.InWorkspace(factory, s.Workspace, db => db.GitHubSettings.Single());
        Assert.DoesNotContain(Token, stored.ProtectedToken);
        Assert.DoesNotContain(Token[^20..], stored.ProtectedToken);
        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(GitHubEndpoints.ProtectorPurpose);
        Assert.Equal(Token, protector.Unprotect(stored.ProtectedToken));

        // Niet in de export (wel dat er een token is), niet in het logboek.
        var export = await (await s.Admin.GetAsync("/api/workspace/export")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Token, export);
        Assert.DoesNotContain(stored.ProtectedToken, export);
        var exported = JsonDocument.Parse(export).RootElement;
        Assert.Equal("wxyz", exported.GetProperty("github").GetProperty("tokenLast4").GetString());
        Assert.Equal(JsonValueKind.Array, exported.GetProperty("githubCommits").ValueKind);
        Assert.DoesNotContain(WorkflowKit.InWorkspace(factory, s.Workspace, db => db.Activities.Select(a => a.Text).ToList()), t => t.Contains(Token[^8..]));

        // Bij GitHub komt het als Bearer-token aan.
        while (factory.GitHub.Requests.TryDequeue(out _)) { }
        await ProjectAsync(s.Admin, s.Customer, "Webshop", $"https://github.com/{s.Repo}/webshop");
        await SuggestionsAsync(s.Admin, "2026-10-12", "2026-10-12");
        var request = Assert.Single(factory.GitHub.Requests);
        Assert.Equal($"Bearer {Token}", request.Auth);
        Assert.StartsWith("Potel", request.UserAgent);

        foreach (var bad in new[] { "", "kort", "met spaties erin 1234567890abcdef", "ghp_" + new string('a', 300), "ghp_abc\r\nX-Extra: 1234567890abcdef" })
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PutAsJsonAsync("/api/github/token", new { token = bad })).StatusCode);

        var removed = await (await s.Admin.DeleteAsync("/api/github/token")).JsonAsync();
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("token").ValueKind);
        Assert.Equal(0, WorkflowKit.InWorkspace(factory, s.Workspace, db => db.GitHubSettings.Count()));
        var none = await SuggestAsync(s.Admin, "2026-10-12", "2026-10-12");
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("nog geen GitHub-token", await none.ErrorAsync());
    }

    [Fact]
    public async Task Commits_become_one_suggestion_per_project_per_day_and_nothing_is_booked_by_itself()
    {
        var s = await SetupAsync("github-voorstel");
        (await s.Admin.PutAsJsonAsync("/api/github/emails", new { emails = new[] { "Ik@Example.com", "ik@werk.example" } })).EnsureSuccessStatusCode();
        var webshop = await ProjectAsync(s.Admin, s.Customer, "Webshop", $"https://github.com/{s.Repo}/webshop");
        var api = await ProjectAsync(s.Admin, s.Customer, "API", $"https://github.com/{s.Repo}/api.git");
        await ProjectAsync(s.Admin, s.Customer, "Oud", $"https://github.com/{s.Repo}/oud", status: "afgerond");
        await ProjectAsync(s.Admin, s.Customer, "Azure", "https://dev.azure.com/acme/portaal");

        var night = Add($"{s.Repo}/webshop", "ik@example.com", Local(12, 0, 30), "Nachtwerk"); // 00:30 Nederlandse tijd: nog 12 oktober
        Add($"{s.Repo}/webshop", "ik@example.com", Local(12, 9), "Checkout bouwen\n\nMet een lange uitleg");
        Add($"{s.Repo}/webshop", "ik@werk.example", Local(12, 9, 40), "Fix totaal");
        Add($"{s.Repo}/webshop", "ik@example.com", Local(12, 10, 10), "Checkout bouwen");
        Add($"{s.Repo}/webshop", "ik@example.com", Local(12, 10, 20), "Merge pull request #4", parents: 2);
        Add($"{s.Repo}/webshop", "collega@example.com", Local(12, 10, 30), "Werk van een collega", sneaky: true);
        var nextDay = Add($"{s.Repo}/webshop", "ik@example.com", Local(13, 0, 30), "Na middernacht");
        Add($"{s.Repo}/api", "ik@example.com", Local(12, 14), "API-sleutel roteren");
        Add($"{s.Repo}/oud", "ik@example.com", Local(12, 14), "Oud project");
        while (factory.GitHub.Requests.TryDequeue(out _)) { }

        var list = await SuggestionsAsync(s.Admin, "2026-10-12", "2026-10-12");
        Assert.Equal(2, list.Count);
        var shop = list.Single(x => x.GetProperty("projectId").GetInt32() == webshop);
        Assert.Equal("2026-10-12T00:00:00", shop.GetProperty("date").GetString());
        // 00:30, dan na meer dan 2 uur een nieuw blok om 09:00, 09:40 en 10:10: 30 + 30 + 40 + 30 = 130, afgerond 135.
        Assert.Equal(135, shop.GetProperty("minutes").GetInt32());
        Assert.Equal("Nachtwerk; Checkout bouwen; Fix totaal", shop.GetProperty("description").GetString());
        Assert.Equal(4, Shas(shop).Length);
        Assert.Contains(night.Sha, Shas(shop));
        Assert.DoesNotContain(nextDay.Sha, Shas(shop));
        var apiSuggestion = list.Single(x => x.GetProperty("projectId").GetInt32() == api);
        Assert.Equal(30, apiSuggestion.GetProperty("minutes").GetInt32());
        Assert.Equal($"{s.Repo}/api", apiSuggestion.GetProperty("repo").GetString());

        // Alleen api.github.com, alleen de lopende GitHub-projecten, per adres, met de dag in Nederlandse tijd.
        var requests = factory.GitHub.Requests.ToList();
        Assert.Equal(4, requests.Count); // 2 repositories × 2 adressen
        Assert.All(requests, r => Assert.Equal("api.github.com", r.Uri.Host));
        Assert.Contains(requests, r => r.Uri.AbsolutePath == $"/repos/{s.Repo}/api/commits");
        Assert.DoesNotContain(requests, r => r.Uri.AbsolutePath.Contains("/oud/") || r.Uri.AbsolutePath.Contains("azure"));
        var query = QueryHelpers.ParseQuery(requests[0].Uri.Query);
        Assert.Equal("2026-10-11T22:00:00Z", query["since"]);
        Assert.Equal("2026-10-12T22:00:00Z", query["until"]);
        Assert.Equal("100", query["per_page"]);
        Assert.Contains(query["author"].ToString(), new[] { "ik@example.com", "ik@werk.example" });

        // Niets is vanzelf geboekt.
        Assert.Empty((await s.Admin.GetFromJsonAsync<List<JsonElement>>("/api/time"))!);

        // Aangepast boeken: andere tijd en omschrijving, de commits gaan mee.
        var accept = await s.Admin.PostAsJsonAsync("/api/github/accept", new
        {
            projectId = webshop, date = "2026-10-12", minutes = 120, description = "Checkout gebouwd", billable = true, shas = Shas(shop),
        });
        Assert.Equal(HttpStatusCode.Created, accept.StatusCode);
        var entry = Assert.Single((await s.Admin.GetFromJsonAsync<List<JsonElement>>("/api/time"))!);
        Assert.Equal(120, entry.GetProperty("minutes").GetInt32());
        Assert.Equal("Checkout gebouwd", entry.GetProperty("description").GetString());
        Assert.Equal(webshop, entry.GetProperty("projectId").GetInt32());
        Assert.Equal(4, WorkflowKit.InWorkspace(factory, s.Workspace, db => db.GitHubCommits.Count(c => c.TimeEntryId == entry.GetProperty("id").GetInt32())));

        // Nog een keer dezelfde commits boeken kan niet, ook niet in een andere combinatie.
        var again = await s.Admin.PostAsJsonAsync("/api/github/accept", new { projectId = api, date = "2026-10-12", minutes = 30, description = "", billable = true, shas = new[] { Shas(shop)[0], Shas(apiSuggestion)[0] } });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Single((await s.Admin.GetFromJsonAsync<List<JsonElement>>("/api/time"))!);

        // Overslaan: geen uren, en de commits komen niet meer terug.
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsJsonAsync("/api/github/skip", new { projectId = api, shas = Shas(apiSuggestion) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsJsonAsync("/api/github/skip", new { projectId = api, shas = Shas(apiSuggestion) })).StatusCode); // nog eens: niets aan de hand
        Assert.Empty(await SuggestionsAsync(s.Admin, "2026-10-12", "2026-10-12"));
        Assert.Single((await s.Admin.GetFromJsonAsync<List<JsonElement>>("/api/time"))!);

        // De hele week: alleen wat nog over is.
        var week = await SuggestionsAsync(s.Admin, "2026-10-12", "2026-10-18");
        var left = Assert.Single(week);
        Assert.Equal("2026-10-13T00:00:00", left.GetProperty("date").GetString());
        Assert.Equal(new[] { nextDay.Sha }, Shas(left));

        // Eén project.
        Assert.Single(await SuggestionsAsync(s.Admin, "2026-10-12", "2026-10-18", webshop));
        Assert.Empty(await SuggestionsAsync(s.Admin, "2026-10-12", "2026-10-18", api));
    }

    [Fact]
    public async Task Requests_are_checked_before_anything_happens()
    {
        var s = await SetupAsync("github-controle");
        var project = await ProjectAsync(s.Admin, s.Customer, "Webshop", $"https://github.com/{s.Repo}/webshop");
        var azure = await ProjectAsync(s.Admin, s.Customer, "Azure", "https://dev.azure.com/acme/portaal");
        var other = await TestApi.RegisterAsync(factory, $"github-ander-{Guid.NewGuid():N}@example.com");
        var foreign = await ProjectAsync(other, await CustomerAsync(other), "Van een ander", $"https://github.com/{s.Repo}/ander");
        while (factory.GitHub.Requests.TryDequeue(out _)) { }

        Assert.Equal(HttpStatusCode.BadRequest, (await SuggestAsync(s.Admin, "2026-10-13", "2026-10-12")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SuggestAsync(s.Admin, "2026-10-12", "2026-10-19")).StatusCode); // 8 dagen
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.GetAsync("/api/github/suggestions")).StatusCode);
        var noRepo = await SuggestAsync(s.Admin, "2026-10-12", "2026-10-12", azure);
        Assert.Equal(HttpStatusCode.BadRequest, noRepo.StatusCode);
        Assert.Contains("geen GitHub-repository", await noRepo.ErrorAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await SuggestAsync(s.Admin, "2026-10-12", "2026-10-12", foreign)).StatusCode);
        Assert.Empty(factory.GitHub.Requests);

        var sha = NewSha();
        async Task<HttpStatusCode> Accept(object body) => (await s.Admin.PostAsJsonAsync("/api/github/accept", body)).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Accept(new { projectId = foreign, date = "2026-10-12", minutes = 60, billable = true, shas = new[] { sha } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Accept(new { projectId = project, minutes = 60, billable = true, shas = new[] { sha } })); // geen datum
        Assert.Equal(HttpStatusCode.BadRequest, await Accept(new { projectId = project, date = "2026-10-12", minutes = 0, billable = true, shas = new[] { sha } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Accept(new { projectId = project, date = "2026-10-12", minutes = 24 * 60 + 1, billable = true, shas = new[] { sha } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Accept(new { projectId = project, date = "2026-10-12", minutes = 60, billable = true, shas = new[] { "niet-een-sha" } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Accept(new { projectId = project, date = "2026-10-12", minutes = 60, billable = true, shas = Array.Empty<string>() }));
        Assert.Equal(HttpStatusCode.BadRequest, await Accept(new { projectId = project, date = "2026-10-12", minutes = 60, billable = true, description = new string('x', 1001), shas = new[] { sha } }));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PostAsJsonAsync("/api/github/skip", new { projectId = foreign, shas = new[] { sha } })).StatusCode);
        Assert.Empty((await s.Admin.GetFromJsonAsync<List<JsonElement>>("/api/time"))!);

        foreach (var bad in new[] { new[] { "geen-adres" }, new[] { "a@b.nl\r\nBcc: c@d.nl" }, new[] { "een@twee@drie.nl" }, Enumerable.Range(0, 6).Select(i => $"a{i}@example.com").ToArray() })
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PutAsJsonAsync("/api/github/emails", new { emails = bad })).StatusCode);
    }

    [Fact]
    public async Task Problems_at_github_become_plain_dutch_messages()
    {
        var s = await SetupAsync("github-fouten");
        await ProjectAsync(s.Admin, s.Customer, "Webshop", $"https://github.com/{s.Repo}/webshop");
        await ProjectAsync(s.Admin, s.Customer, "API", $"https://github.com/{s.Repo}/api");
        Add($"{s.Repo}/api", s.Email, Local(12, 11), "Werkt gewoon");

        async Task<(HttpStatusCode Status, JsonElement Json)> With(Func<HttpRequestMessage, HttpResponseMessage?> respond)
        {
            factory.GitHub.Override = req => req.RequestUri!.AbsolutePath.Contains("/webshop/") ? respond(req) : null;
            var res = await SuggestAsync(s.Admin, "2026-10-12", "2026-10-12");
            factory.GitHub.Override = null;
            return (res.StatusCode, await res.JsonAsync());
        }
        static HttpResponseMessage Status(HttpStatusCode code, params (string Name, string Value)[] headers)
        {
            var r = FakeGitHub.Json(code, new JsonObject { ["message"] = "nee" });
            foreach (var (name, value) in headers) r.Headers.TryAddWithoutValidation(name, value);
            return r;
        }

        // Problemen met het token of de limiet: meteen stoppen, met een duidelijke melding.
        var (status, json) = await With(_ => Status(HttpStatusCode.Unauthorized));
        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Contains("accepteert het token niet", json.GetProperty("error").GetString());

        var reset = new DateTimeOffset(2026, 10, 14, 12, 15, 0, TimeSpan.Zero).ToUnixTimeSeconds(); // 14:15 Nederlandse tijd
        (status, json) = await With(_ => Status(HttpStatusCode.Forbidden, ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", reset.ToString())));
        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Contains("limiet van GitHub", json.GetProperty("error").GetString());
        Assert.Contains("na 14:15 uur", json.GetProperty("error").GetString());

        (status, json) = await With(_ => Status(HttpStatusCode.TooManyRequests, ("Retry-After", "120")));
        Assert.Contains("na 12:02 uur", json.GetProperty("error").GetString()); // de klok staat op 10:00 UTC, 12:00 in Nederland

        (status, json) = await With(_ => Status(HttpStatusCode.BadGateway));
        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Contains("storing", json.GetProperty("error").GetString());

        (status, json) = await With(_ => throw new TaskCanceledException("te traag"));
        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Contains("niet op tijd", json.GetProperty("error").GetString());

        // Problemen met één repository: de rest gaat gewoon door.
        async Task OneRepo(Func<HttpRequestMessage, HttpResponseMessage?> respond, string expected)
        {
            var (code, body) = await With(respond);
            Assert.Equal(HttpStatusCode.OK, code);
            var problem = Assert.Single(body.GetProperty("problems").EnumerateArray());
            Assert.Contains(expected, problem.GetProperty("message").GetString());
            Assert.Equal("Webshop", problem.GetProperty("project").GetString());
            Assert.Single(body.GetProperty("suggestions").EnumerateArray());
        }
        await OneRepo(_ => Status(HttpStatusCode.NotFound), "bestaat niet");
        await OneRepo(_ => Status(HttpStatusCode.Forbidden), "geen toegang");
        await OneRepo(_ => Status(HttpStatusCode.UnprocessableEntity), "foutcode 422");
        await OneRepo(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{ geen json", Encoding.UTF8, "application/json") }, "niet kan lezen");
        await OneRepo(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string(' ', GitHubClient.MaxResponseBytes + 10) + "[]") }, "te groot");

        // Een doorverwijzing wordt niet gevolgd, ook niet naar een ander domein.
        while (factory.GitHub.Requests.TryDequeue(out _)) { }
        await OneRepo(_ =>
        {
            var r = Status(HttpStatusCode.MovedPermanently);
            r.Headers.Location = new Uri("https://evil.example/repos/x/y/commits");
            return r;
        }, "verhuisd");
        Assert.All(factory.GitHub.Requests, r => Assert.Equal("api.github.com", r.Uri.Host));

        // Een lege repository is geen probleem.
        var (emptyStatus, emptyJson) = await With(_ => Status(HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.OK, emptyStatus);
        Assert.Empty(emptyJson.GetProperty("problems").EnumerateArray());

        // Een token dat de server niet meer kan lezen, bijvoorbeeld na een verhuizing zonder de sleutels.
        WorkflowKit.InWorkspace(factory, s.Workspace, db => db.GitHubSettings.ExecuteUpdate(x => x.SetProperty(g => g.ProtectedToken, "CfDJ8kapot")));
        var unreadable = await SuggestAsync(s.Admin, "2026-10-12", "2026-10-12");
        Assert.Equal(HttpStatusCode.BadRequest, unreadable.StatusCode);
        Assert.Contains("niet meer te lezen", await unreadable.ErrorAsync());
    }

    [Fact]
    public void The_real_handler_follows_no_redirects_and_the_client_has_limits()
    {
        using var handler = GitHubClient.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        var http = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GitHubClient));
        Assert.Equal(GitHubClient.Timeout, http.Timeout);
        Assert.Equal(GitHubClient.MaxResponseBytes, http.MaxResponseContentBufferSize);
    }

    [Fact]
    public async Task Roles_read_only_workspaces_and_other_workspaces_are_respected()
    {
        var s = await SetupAsync("github-rollen");
        var project = await ProjectAsync(s.Admin, s.Customer, "Webshop", $"https://github.com/{s.Repo}/webshop");
        var employeeEmail = $"github-medewerker-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(s.Admin, employeeEmail);
        var employee = await TestApi.LoginAsync(factory, employeeEmail);
        var mine = Add($"{s.Repo}/webshop", employeeEmail, Local(12, 9), "Werk van de medewerker");
        var boss = Add($"{s.Repo}/webshop", s.Email, Local(12, 9), "Werk van de beheerder");

        // Een medewerker ziet alleen dat er een token is, en krijgt de voorstellen bij zijn eigen adres.
        var settings = await (await employee.GetAsync("/api/github/settings")).JsonAsync();
        Assert.Equal("wxyz", settings.GetProperty("token").GetProperty("last4").GetString());
        Assert.Equal(employeeEmail, settings.GetProperty("accountEmail").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PutAsJsonAsync("/api/github/token", new { token = Token })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.DeleteAsync("/api/github/token")).StatusCode);
        var own = Assert.Single(await SuggestionsAsync(employee, "2026-10-12", "2026-10-12"));
        Assert.Equal(new[] { mine.Sha }, Shas(own));
        Assert.Equal(new[] { boss.Sha }, Shas(Assert.Single(await SuggestionsAsync(s.Admin, "2026-10-12", "2026-10-12"))));
        // Eigen adressen gelden alleen voor jezelf.
        (await employee.PutAsJsonAsync("/api/github/emails", new { emails = new[] { s.Email } })).EnsureSuccessStatusCode();
        Assert.Empty((await (await s.Admin.GetAsync("/api/github/settings")).JsonAsync()).GetProperty("emails").EnumerateArray());
        (await employee.PutAsJsonAsync("/api/github/emails", new { emails = Array.Empty<string>() })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Created, (await employee.PostAsJsonAsync("/api/github/accept", new { projectId = project, date = "2026-10-12", minutes = 30, description = "Werk", billable = true, shas = Shas(own) })).StatusCode);

        // Een andere werkruimte ziet het token niet, kan er niets mee en heeft eigen geboekte commits.
        var other = await TestApi.RegisterAsync(factory, $"github-buur-{Guid.NewGuid():N}@example.com");
        Assert.Equal(JsonValueKind.Null, (await (await other.GetAsync("/api/github/settings")).JsonAsync()).GetProperty("token").ValueKind);
        Assert.Contains("nog geen GitHub-token", await (await SuggestAsync(other, "2026-10-12", "2026-10-12")).ErrorAsync());
        Assert.Equal(HttpStatusCode.OK, (await other.DeleteAsync("/api/github/token")).StatusCode);
        Assert.Equal(1, WorkflowKit.InWorkspace(factory, s.Workspace, db => db.GitHubSettings.Count()));
        (await other.PutAsJsonAsync("/api/github/token", new { token = Token })).EnsureSuccessStatusCode();
        (await other.PutAsJsonAsync("/api/github/emails", new { emails = new[] { employeeEmail } })).EnsureSuccessStatusCode();
        await ProjectAsync(other, await CustomerAsync(other), "Zelfde repo", $"https://github.com/{s.Repo}/webshop");
        Assert.Equal(new[] { mine.Sha }, Shas(Assert.Single(await SuggestionsAsync(other, "2026-10-12", "2026-10-12"))));

        // Alleen-lezen: bekijken en het token weghalen mag, de rest niet.
        await TestApi.ExpireTrialAsync(factory, s.Workspace);
        Assert.Equal(HttpStatusCode.OK, (await SuggestAsync(s.Admin, "2026-10-12", "2026-10-12")).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await s.Admin.PutAsJsonAsync("/api/github/token", new { token = Token })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await s.Admin.PutAsJsonAsync("/api/github/emails", new { emails = new[] { "x@example.com" } })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await s.Admin.PostAsJsonAsync("/api/github/accept", new { projectId = project, date = "2026-10-12", minutes = 30, billable = true, shas = new[] { boss.Sha } })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await s.Admin.PostAsJsonAsync("/api/github/skip", new { projectId = project, shas = new[] { boss.Sha } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.DeleteAsync("/api/github/token")).StatusCode);
        Assert.Equal(0, WorkflowKit.InWorkspace(factory, s.Workspace, db => db.GitHubSettings.Count()));
    }

    [Fact]
    public async Task Deleting_the_workspace_takes_the_github_data_with_it()
    {
        var s = await SetupAsync("github-weg");
        var project = await ProjectAsync(s.Admin, s.Customer, "Webshop", $"https://github.com/{s.Repo}/webshop");
        (await s.Admin.PutAsJsonAsync("/api/github/emails", new { emails = new[] { "ik@example.com" } })).EnsureSuccessStatusCode();
        (await s.Admin.PostAsJsonAsync("/api/github/skip", new { projectId = project, shas = new[] { NewSha() } })).EnsureSuccessStatusCode();
        // Eerst nog in de export.
        var export = JsonDocument.Parse(await (await s.Admin.GetAsync("/api/workspace/export")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ik@example.com", Assert.Single(export.GetProperty("githubAuthors").EnumerateArray()).GetProperty("emails").GetString());
        Assert.True(Assert.Single(export.GetProperty("githubCommits").EnumerateArray()).GetProperty("skipped").GetBoolean());
        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "geheim123" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.SendAsync(delete)).StatusCode);
        Assert.Equal(0, WorkflowKit.InWorkspace(factory, s.Workspace, db => db.GitHubSettings.Count() + db.GitHubAuthors.Count() + db.GitHubCommits.Count()));
    }
}
