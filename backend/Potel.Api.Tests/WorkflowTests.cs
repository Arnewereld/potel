using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Workflows;

namespace Potel.Api.Tests;

public class WorkflowTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    record RunDto(int Id, string Status, string LogJson, DateTime? ResumeAt);

    [Fact]
    public async Task Big_lead_takes_the_yes_branch_of_the_demo_workflow()
    {
        var client = await factory.LoginAsync();
        var res = await client.PostAsJsonAsync("/api/leads", new { name = "Grote Klant", company = "Groot BV", value = 12000, status = "nieuw" });
        var lead = await res.Content.ReadFromJsonAsync<Lead>();

        Assert.Equal("contact", lead!.Status);
        Assert.True(factory.WithDb(db => db.Appointments.Any(a => a.Title == "Intakegesprek plannen met Grote Klant (Groot BV)")));
    }

    [Fact]
    public async Task Small_lead_waits_and_continues_after_the_wait()
    {
        var client = await factory.LoginAsync();
        await client.PostAsJsonAsync("/api/leads", new { name = "Kleine Klant", email = "klein@example.com", value = 800, status = "nieuw" });

        var run = factory.WithDb(db => db.WorkflowRuns.OrderByDescending(r => r.Id).First());
        Assert.Equal("wachtend", run.Status);
        Assert.Contains("let op", run.LogJson); // geen mailserver ingesteld
        Assert.True(run.ResumeAt > DateTime.UtcNow.AddDays(2.9));

        // Doe alsof de drie dagen voorbij zijn.
        using (var scope = factory.DefaultWorkspaceScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var r = db.WorkflowRuns.Find(run.Id)!;
            r.ResumeAt = DateTime.UtcNow.AddMinutes(-1);
            r.PendingJson = JsonSerializer.Serialize(new List<PendingStep> { new("n7", DateTime.UtcNow.AddMinutes(-1)) }, Graph.Json);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<WorkflowEngine>().ResumeDueAsync();
        }

        Assert.Equal("klaar", factory.WithDb(db => db.WorkflowRuns.Find(run.Id)!.Status));
        Assert.True(factory.WithDb(db => db.Appointments.Any(a => a.Title == "Nabellen: Kleine Klant")));
    }

    [Fact]
    public async Task Paid_invoice_triggers_workflow_and_manual_run_reports_errors()
    {
        var client = await factory.LoginAsync();
        var graph = new
        {
            nodes = new object[]
            {
                new { id = "a", type = "trigger.paid", label = "Betaald", x = 0, y = 0 },
                new { id = "b", type = "action.task", label = "Bedank", x = 200, y = 0, config = new Dictionary<string, string> { ["title"] = "Bedank {{customer.name}} voor {{invoice.number}}" } },
            },
            edges = new[] { new { from = "a", to = "b" } },
        };
        var wfRes = await client.PostAsJsonAsync("/api/workflows", new { name = "Bedanken", graphJson = JsonSerializer.Serialize(graph) });
        var wf = await wfRes.Content.ReadFromJsonAsync<Workflow>();
        wf!.Active = true;
        await client.PutAsJsonAsync($"/api/workflows/{wf.Id}", wf);

        var invoice = factory.WithDb(db => db.Invoices.First(i => i.Status != "betaald"));
        var full = await client.GetFromJsonAsync<Invoice>($"/api/invoices/{invoice.Id}");
        full!.Status = "betaald";
        (await client.PutAsJsonAsync($"/api/invoices/{invoice.Id}", full)).EnsureSuccessStatusCode();

        var customer = factory.WithDb(db => db.Customers.Find(invoice.CustomerId)!);
        Assert.True(factory.WithDb(db => db.Appointments.Any(a => a.Title == $"Bedank {customer.Name} voor {invoice.Number}")));

        // Handmatig uitvoeren zonder factuur: de taak werkt nog, maar status zetten faalt netjes.
        var bad = new
        {
            nodes = new object[]
            {
                new { id = "a", type = "trigger.manual", label = "Start", x = 0, y = 0 },
                new { id = "b", type = "action.status", label = "Status", x = 200, y = 0, config = new Dictionary<string, string> { ["target"] = "invoice", ["status"] = "betaald" } },
            },
            edges = new[] { new { from = "a", to = "b" } },
        };
        wf.GraphJson = JsonSerializer.Serialize(bad);
        await client.PutAsJsonAsync($"/api/workflows/{wf.Id}", wf);
        var run = await (await client.PostAsJsonAsync($"/api/workflows/{wf.Id}/run", new { })).Content.ReadFromJsonAsync<RunDto>();
        Assert.Equal("fout", run!.Status);
        Assert.Contains("geen factuur", run.LogJson);
    }

    [Theory]
    [InlineData("dag", null, "2026-10-05T10:00", true)]
    [InlineData("dag", "2026-10-05T09:00", "2026-10-05T15:00", false)]
    [InlineData("dag", "2026-10-04T09:00", "2026-10-05T08:00", false)]
    [InlineData("uur", "2026-10-05T09:00", "2026-10-05T10:01", true)]
    [InlineData("week", null, "2026-10-05T10:00", true)]  // maandag
    [InlineData("week", null, "2026-10-06T10:00", false)] // dinsdag
    public void Schedule_is_due_at_the_right_moments(string interval, string? last, string now, bool expected)
    {
        var node = new GraphNode("s", "trigger.schedule", "", 0, 0, new() { ["interval"] = interval, ["hour"] = "9", ["weekday"] = "1" });
        Assert.Equal(expected, WorkflowEngine.IsDue(node, last is null ? null : DateTime.Parse(last), DateTime.Parse(now)));
    }

    [Theory]
    [InlineData("12000.00", ">", "5000", true)]
    [InlineData("800.00", ">", "5.000", false)]
    [InlineData("Website", "=", "website", true)]
    [InlineData("Hendriks Installatie", "bevat", "install", true)]
    [InlineData("", "leeg", "", true)]
    public void Conditions_compare_numbers_and_text(string left, string op, string right, bool expected) =>
        Assert.Equal(expected, WorkflowContext.Compare(left, op, right));
}
