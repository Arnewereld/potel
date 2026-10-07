using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// Werkstromen die e-mail sturen of een webhook aanroepen, maakt of wijzigt alleen een beheerder.
public class WorkflowRoleTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    static string Graph(string actionType) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "t", type = "trigger.manual", label = "Start", x = 0, y = 0 },
            new { id = "a", type = actionType, label = "Actie", x = 200, y = 0, config = new Dictionary<string, string> { ["to"] = "lek@example.com", ["url"] = "https://example.com/hook" } },
        },
        edges = new[] { new { from = "t", to = "a" } },
    });

    async Task<(HttpClient Admin, HttpClient Employee)> TeamAsync(string prefix)
    {
        var admin = await RegisterAsync(factory, $"{prefix}-a@example.com");
        await CreateUserAsync(admin, $"{prefix}-m@example.com");
        return (admin, await LoginAsync(factory, $"{prefix}-m@example.com"));
    }

    [Theory]
    [InlineData("action.email")]
    [InlineData("action.webhook")]
    public async Task Employee_cannot_create_workflows_that_send_data_out(string type)
    {
        var (admin, employee) = await TeamAsync($"wf-{type.Replace('.', '-')}");
        var res = await employee.PostAsJsonAsync("/api/workflows", new { name = "lek", graphJson = Graph(type) });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("beheerder", await res.ErrorAsync());
        Assert.Empty((await admin.GetFromJsonAsync<List<JsonElement>>("/api/workflows"))!);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/workflows", new { name = "mag wel", graphJson = Graph(type) })).StatusCode);
    }

    [Fact]
    public async Task Employee_cannot_edit_an_email_workflow_but_keeps_other_workflows()
    {
        var (admin, employee) = await TeamAsync("wf-edit");
        var mail = (await (await admin.PostAsJsonAsync("/api/workflows", new { name = "Mail", graphJson = Graph("action.email") })).JsonAsync()).GetProperty("id").GetInt32();

        // Ook het e-mailblok eruit halen of de werkstroom aanzetten mag niet.
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PutAsJsonAsync($"/api/workflows/{mail}", new { name = "Mail", active = true, graphJson = Graph("action.email") })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PutAsJsonAsync($"/api/workflows/{mail}", new { name = "Mail", active = false, graphJson = Graph("action.task") })).StatusCode);

        var task = await employee.PostAsJsonAsync("/api/workflows", new { name = "Taak", graphJson = Graph("action.task") });
        Assert.Equal(HttpStatusCode.Created, task.StatusCode);
        var taskId = (await task.JsonAsync()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await employee.PutAsJsonAsync($"/api/workflows/{taskId}", new { name = "Taak", active = true, graphJson = Graph("action.task") })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PutAsJsonAsync($"/api/workflows/{taskId}", new { name = "Taak", active = true, graphJson = Graph("action.webhook") })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await employee.PostAsJsonAsync($"/api/workflows/{taskId}/run", new { })).StatusCode);
    }
}
