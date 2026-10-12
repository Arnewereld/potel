using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Telt de verzoeken die uren op een factuur zetten zodra ze binnenkomen, zodat een test weet dat ze echt tegelijk lopen.
public class RaceFactory : PortalFactory
{
    readonly Lock sync = new();
    readonly List<(int Total, TaskCompletionSource Done)> waiting = [];

    public int Arrivals { get; private set; }

    public Task ArrivedAsync(int total)
    {
        lock (sync)
        {
            if (Arrivals >= total) return Task.CompletedTask;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting.Add((total, done));
            return done.Task;
        }
    }

    void Arrive()
    {
        lock (sync)
        {
            Arrivals++;
            foreach (var w in waiting.Where(w => Arrivals >= w.Total).ToList())
            {
                w.Done.TrySetResult();
                waiting.Remove(w);
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(new ArrivalFilter(this)));
    }

    sealed class ArrivalFilter(RaceFactory factory) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (http, go) =>
            {
                var path = http.Request.Path.Value ?? "";
                if (HttpMethods.IsPost(http.Request.Method) && (path.EndsWith("/hours") || path.EndsWith("/invoice"))) factory.Arrive();
                await go();
            });
            next(app);
        };
    }
}

// Dubbelklikken of twee tabbladen: gelijktijdige verzoeken mogen uren nooit twee keer factureren en nooit hetzelfde nummer geven.
public class InvoiceRaceTests(RaceFactory factory) : IClassFixture<RaceFactory>
{
    // De verzoeken komen binnen terwijl de test zelf de schrijfblokkade van SQLite vasthoudt; pas als ze er allemaal zijn,
    // laten we los. Zo lopen ze gegarandeerd tegelijk, in plaats van dat we in een lus op toeval hopen.
    async Task<HttpResponseMessage?[]> TogetherAsync(params Func<Task<HttpResponseMessage>>[] requests)
    {
        await using var hold = new SqliteConnection($"Data Source={factory.DatabasePath}");
        await hold.OpenAsync();
        await ExecAsync(hold, "BEGIN IMMEDIATE");
        var arrived = factory.ArrivedAsync(factory.Arrivals + requests.Length);
        var sent = requests.Select(SafeSend).ToArray();
        try
        {
            // Ruim korter dan de 30 seconden die een verzoek op de blokkade wacht. Komen ze later binnen, dan lopen ze na
            // elkaar; de uitkomst moet dan precies hetzelfde zijn.
            await Task.WhenAny(arrived, Task.Delay(TimeSpan.FromSeconds(10)));
            await Task.Delay(200);
        }
        finally
        {
            await ExecAsync(hold, "ROLLBACK");
        }
        return await Task.WhenAll(sent);
    }

    static async Task ExecAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    static Func<Task<HttpResponseMessage>> AddHours(HttpClient c, int invoiceId, int projectId) =>
        () => c.PostAsJsonAsync($"/api/invoices/{invoiceId}/hours", new { projectId, detailed = false });

    // Precies één verzoek lukt; de ander krijgt een nette melding, geen serverfout en geen time-out.
    static HttpResponseMessage Winner(HttpResponseMessage?[] results)
    {
        Assert.All(results, r => Assert.NotNull(r));
        foreach (var r in results.Where(r => !r!.IsSuccessStatusCode))
            Assert.True(r!.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict, $"Het tweede verzoek gaf {(int)r.StatusCode}");
        return Assert.Single(results, r => r!.IsSuccessStatusCode)!;
    }

    static async Task AssertNoOpenHoursAsync(HttpClient c, int projectId) =>
        Assert.Empty((await c.GetFromJsonAsync<List<System.Text.Json.JsonElement>>($"/api/time?projectId={projectId}&open=true"))!);

    [Fact]
    public async Task Two_requests_adding_the_same_hours_bill_them_only_once()
    {
        var c = await ReadyAsync(factory, "race-hours");
        var customerId = await CustomerAsync(c);

        // Dubbelklik: twee keer dezelfde uren op dezelfde conceptfactuur.
        var projectId = await ProjectAsync(c, customerId, 95, "Dubbelklik");
        await TimeAsync(c, projectId, 90, "2026-10-01", count: 4); // 6 uur
        var invoiceId = await InvoiceAsync(c, customerId, "concept", 1, 100);
        Assert.Equal(HttpStatusCode.OK, Winner(await TogetherAsync(AddHours(c, invoiceId, projectId), AddHours(c, invoiceId, projectId))).StatusCode);
        Assert.Equal(6m, HourQuantity(await GetInvoiceAsync(c, invoiceId)));
        await AssertNoOpenHoursAsync(c, projectId);

        // Twee tabbladen: dezelfde uren tegelijk op twee verschillende concepten.
        projectId = await ProjectAsync(c, customerId, 95, "Twee tabbladen");
        await TimeAsync(c, projectId, 90, "2026-10-01", count: 4);
        var first = await InvoiceAsync(c, customerId, "concept", 1, 100);
        var second = await InvoiceAsync(c, customerId, "concept", 1, 100);
        Winner(await TogetherAsync(AddHours(c, first, projectId), AddHours(c, second, projectId)));
        Assert.Equal(6m, HourQuantity(await GetInvoiceAsync(c, first)) + HourQuantity(await GetInvoiceAsync(c, second)));
        await AssertNoOpenHoursAsync(c, projectId);

        // Uren op een concept zetten terwijl je vanaf het project een nieuwe factuur maakt.
        projectId = await ProjectAsync(c, customerId, 95, "Vanaf het project");
        await TimeAsync(c, projectId, 90, "2026-10-01", count: 4);
        invoiceId = await InvoiceAsync(c, customerId, "concept", 1, 100);
        var winner = Winner(await TogetherAsync(AddHours(c, invoiceId, projectId),
            () => c.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { detailed = false })));
        var billed = HourQuantity(await GetInvoiceAsync(c, invoiceId));
        if (winner.StatusCode == HttpStatusCode.Created) billed += HourQuantity(await GetInvoiceAsync(c, (await Json(winner)).GetProperty("id").GetInt32()));
        Assert.Equal(6m, billed);
        await AssertNoOpenHoursAsync(c, projectId);
    }

    [Fact]
    public async Task Parallel_invoices_from_a_project_bill_each_hour_only_once()
    {
        var c = await ReadyAsync(factory, "race-project");
        var customerId = await CustomerAsync(c);
        string? problem = null;
        for (var round = 0; round < 15 && problem is null; round++)
        {
            var projectId = await ProjectAsync(c, customerId, 95, $"P{round}");
            await TimeAsync(c, projectId, 90, "2026-10-01", count: 20); // 30 uur
            var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
                SafeSend(() => c.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { detailed = false }))));
            if (results.Any(r => r is null || r.StatusCode == HttpStatusCode.InternalServerError)) { problem = "een verzoek gaf een serverfout"; break; }
            var created = results.Where(r => r!.StatusCode == HttpStatusCode.Created).ToList();
            decimal billed = 0;
            foreach (var r in created) billed += HourQuantity(await GetInvoiceAsync(c, (await Json(r!)).GetProperty("id").GetInt32()));
            if (billed != 30m) problem = $"{created.Count} facturen gemaakt met samen {billed} uur voor 30 open uren";
        }
        Assert.True(problem is null, problem);
    }

    [Fact]
    public async Task Parallel_copies_never_fail_with_a_server_error()
    {
        var c = await ReadyAsync(factory, "race-copy");
        var customerId = await CustomerAsync(c);
        var invoiceId = await InvoiceAsync(c, customerId, "verzonden", 1, 100);
        var failures = 0;
        for (var round = 0; round < 5; round++)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                SafeSend(() => c.PostAsync($"/api/invoices/{invoiceId}/duplicate", null))));
            failures += results.Count(r => r is null || r.StatusCode != HttpStatusCode.Created);
        }
        Assert.True(failures == 0, $"{failures} van 20 gelijktijdige kopieën mislukten");
    }

    [Fact]
    public async Task Invoices_sent_at_the_same_time_get_consecutive_numbers_without_errors()
    {
        var c = await ReadyAsync(factory, "race-number");
        var customerId = await CustomerAsync(c);
        var year = DateTime.Today.Year;

        // Meteen verstuurd aanmaken, en concepten tegelijk versturen.
        var direct = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SafeSend(() => PostInvoiceAsync(c, customerId, "verzonden", 1, 100))));
        var concepts = new List<int>();
        for (var i = 0; i < 6; i++) concepts.Add(await InvoiceAsync(c, customerId, "concept", 1, 100));
        var sent = await Task.WhenAll(concepts.Select(id => SafeSend(() => SetStatusAsync(c, id, "verzonden"))));

        Assert.All(direct, r => Assert.Equal(HttpStatusCode.Created, r?.StatusCode));
        Assert.All(sent, r => Assert.Equal(HttpStatusCode.OK, r?.StatusCode));
        var numbers = (await c.GetFromJsonAsync<List<System.Text.Json.JsonElement>>("/api/invoices"))!
            .Select(i => i.GetProperty("number").GetString()).OrderBy(n => n).ToList();
        Assert.Equal(Enumerable.Range(1, 10).Select(n => $"{year}-{n:0000}"), numbers);
    }
}
