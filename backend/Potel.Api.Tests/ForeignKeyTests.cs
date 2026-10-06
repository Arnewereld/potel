using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// Elk meegestuurd nummer van een klant, lead, factuur of project moet in de eigen werkruimte bestaan.
public class ForeignKeyTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task Appointments_refuse_a_customer_of_another_workspace()
    {
        var a = await RegisterAsync(factory, "afspraak-a@example.com");
        var b = await RegisterAsync(factory, "afspraak-b@example.com");
        var foreign = await CreateCustomerAsync(b, "Klant van B");
        var own = await CreateCustomerAsync(a, "Klant van A");
        object Body(int? customerId) => new { title = "Afspraak", start = "2026-10-10T09:00:00", end = "2026-10-10T10:00:00", kind = "afspraak", customerId };

        var post = await a.PostAsJsonAsync("/api/appointments", Body(foreign));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Contains("klant", await post.ErrorAsync());

        var created = await (await a.PostAsJsonAsync("/api/appointments", Body(own))).JsonAsync();
        var id = created.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PutAsJsonAsync($"/api/appointments/{id}", Body(foreign))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PutAsJsonAsync($"/api/appointments/{id}", Body(null))).StatusCode);

        var stored = (await a.GetFromJsonAsync<List<JsonElement>>("/api/appointments"))!.Single(x => x.GetProperty("id").GetInt32() == id);
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("customerId").ValueKind);
    }

    [Fact]
    public async Task Leads_refuse_a_customer_of_another_workspace_before_workflows_see_it()
    {
        var a = await RegisterAsync(factory, "lead-a@example.com");
        var b = await RegisterAsync(factory, "lead-b@example.com");
        var foreign = await CreateCustomerAsync(b, "Klant van B");
        var graph = new
        {
            nodes = new object[]
            {
                new { id = "t", type = "trigger.lead", label = "Lead", x = 0, y = 0 },
                new { id = "k", type = "action.task", label = "Opvolgen", x = 200, y = 0, config = new Dictionary<string, string> { ["title"] = "Opvolgen {{lead.name}}" } },
            },
            edges = new[] { new { from = "t", to = "k" } },
        };
        var wf = await (await a.PostAsJsonAsync("/api/workflows", new { name = "Opvolgen", graphJson = JsonSerializer.Serialize(graph) })).JsonAsync();
        (await a.PutAsJsonAsync($"/api/workflows/{wf.GetProperty("id").GetInt32()}", new { name = "Opvolgen", active = true, graphJson = JsonSerializer.Serialize(graph) })).EnsureSuccessStatusCode();

        var post = await a.PostAsJsonAsync("/api/leads", new { name = "Lead X", status = "nieuw", customerId = foreign });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Empty((await a.GetFromJsonAsync<List<JsonElement>>("/api/leads"))!);
        Assert.DoesNotContain((await a.GetFromJsonAsync<List<JsonElement>>("/api/appointments"))!, x => x.GetProperty("title").GetString() == "Opvolgen Lead X");

        // Zonder klant gaat het gewoon door, en de taak van de werkstroom krijgt geen klant.
        Assert.Equal(HttpStatusCode.Created, (await a.PostAsJsonAsync("/api/leads", new { name = "Lead Y", status = "nieuw" })).StatusCode);
        var task = (await a.GetFromJsonAsync<List<JsonElement>>("/api/appointments"))!.Single(x => x.GetProperty("title").GetString() == "Opvolgen Lead Y");
        Assert.Equal(JsonValueKind.Null, task.GetProperty("customerId").ValueKind);
    }

    [Fact]
    public async Task Manual_workflow_run_refuses_records_of_another_workspace()
    {
        var a = await RegisterAsync(factory, "run-a@example.com");
        var b = await RegisterAsync(factory, "run-b@example.com");
        var foreignCustomer = await CreateCustomerAsync(b, "Klant van B");
        var foreignLead = (await (await b.PostAsJsonAsync("/api/leads", new { name = "Lead van B", status = "nieuw" })).JsonAsync()).GetProperty("id").GetInt32();
        var foreignInvoice = (await (await b.PostAsJsonAsync("/api/invoices", new
        {
            customerId = foreignCustomer, issueDate = "2026-10-01", dueDate = "2026-10-15", status = "concept",
            lines = new[] { new { description = "x", quantity = 1, unit = "stuk", unitPrice = 1, vatRate = 21 } },
        })).JsonAsync()).GetProperty("id").GetInt32();

        var graph = new { nodes = new object[] { new { id = "t", type = "trigger.manual", label = "Start", x = 0, y = 0 } }, edges = Array.Empty<object>() };
        var wfId = (await (await a.PostAsJsonAsync("/api/workflows", new { name = "Handmatig", graphJson = JsonSerializer.Serialize(graph) })).JsonAsync()).GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync($"/api/workflows/{wfId}/run", new { customerId = foreignCustomer })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync($"/api/workflows/{wfId}/run", new { leadId = foreignLead })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync($"/api/workflows/{wfId}/run", new { invoiceId = foreignInvoice })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync($"/api/workflows/{wfId}/run", new { })).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_customer_unlinks_its_appointments_so_they_stay_editable()
    {
        var a = await RegisterAsync(factory, "klant-weg@example.com");
        var customer = await CreateCustomerAsync(a, "Vertrekt");
        object Body(int? customerId) => new { title = "Afspraak", start = "2026-10-10T09:00:00", end = "2026-10-10T10:00:00", kind = "afspraak", customerId };
        var id = (await (await a.PostAsJsonAsync("/api/appointments", Body(customer))).JsonAsync()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/customers/{customer}")).StatusCode);

        var stored = (await a.GetFromJsonAsync<List<JsonElement>>("/api/appointments"))!.Single(x => x.GetProperty("id").GetInt32() == id);
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("customerId").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await a.PutAsJsonAsync($"/api/appointments/{id}", Body(null))).StatusCode);
    }

    [Fact]
    public async Task Projects_time_and_invoices_refuse_records_of_another_workspace()
    {
        var a = await RegisterAsync(factory, "fk-a@example.com");
        var b = await RegisterAsync(factory, "fk-b@example.com");
        var foreignCustomer = await CreateCustomerAsync(b);
        var foreignProject = (await (await b.PostAsJsonAsync("/api/projects", new { name = "P", customerId = foreignCustomer, status = "actief", billing = "uur", hourlyRate = 1, color = "#000000" })).JsonAsync()).GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/projects", new { name = "P", customerId = foreignCustomer, status = "actief", billing = "uur", hourlyRate = 1, color = "#000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/time", new { projectId = foreignProject, date = "2026-10-06", minutes = 30, billable = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.PostAsJsonAsync($"/api/modules/{999999}/records", new { dataJson = "{}" })).StatusCode);
    }
}
