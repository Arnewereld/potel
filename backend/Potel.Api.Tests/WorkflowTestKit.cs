using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Potel.Api.Data;
using Potel.Api.Workflows;

namespace Potel.Api.Tests;

// Hulpjes voor tests van werkstromen: grafen bouwen, runs starten en de echte planner draaien.
static class WorkflowKit
{
    public static object Node(string id, string type, Dictionary<string, string>? config = null) => new { id, type, label = id, x = 0, y = 0, config };

    public static object Edge(string from, string to) => new { from, to };

    public static string GraphJson(object[] nodes, object[] edges) => JsonSerializer.Serialize(new { nodes, edges });

    public static async Task<int> CreateWorkflowAsync(HttpClient c, string name, string graph, bool active = false)
    {
        var res = await c.PostAsJsonAsync("/api/workflows", new { name, graphJson = graph });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = (await res.JsonAsync()).GetProperty("id").GetInt32();
        if (active) (await c.PutAsJsonAsync($"/api/workflows/{id}", new { name, active = true, graphJson = graph })).EnsureSuccessStatusCode();
        return id;
    }

    public record Run(int Id, string Status, List<JsonElement> Log)
    {
        public IEnumerable<string> Messages => Log.Select(e => e.GetProperty("message").GetString()!);
        public string LastMessage => Messages.Last();
        public string LastStatus => Log.Last().GetProperty("status").GetString()!;
    }

    public static Run ParseRun(JsonElement run) => new(
        run.GetProperty("id").GetInt32(),
        run.GetProperty("status").GetString()!,
        JsonSerializer.Deserialize<List<JsonElement>>(run.GetProperty("logJson").GetString()!)!);

    public static async Task<Run> RunAsync(HttpClient c, int workflowId)
    {
        var res = await c.PostAsJsonAsync($"/api/workflows/{workflowId}/run", new { });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return ParseRun(await res.JsonAsync());
    }

    // Maakt een handmatige werkstroom met één webhook en voert hem uit.
    public static async Task<Run> RunWebhookAsync(HttpClient c, string url)
    {
        var graph = GraphJson([Node("s", "trigger.manual"), Node("w", "action.webhook", new() { ["url"] = url })], [Edge("s", "w")]);
        return await RunAsync(c, await CreateWorkflowAsync(c, "Webhook", graph));
    }

    public static T InWorkspace<T>(WebApplicationFactory<Program> f, int workspaceId, Func<AppDb, T> action)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        db.Tenant.WorkspaceId = workspaceId;
        return action(db);
    }

    public static bool HasAppointment(WebApplicationFactory<Program> f, int workspaceId, string title) =>
        InWorkspace(f, workspaceId, db => db.Appointments.Any(a => a.Title == title));

    public static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    // Draait de echte WorkflowScheduler (in PortalFactory staat hij uit) tot de voorwaarde klopt, en stopt hem dan netjes.
    public static async Task<(bool Reached, TimeSpan Elapsed)> RunSchedulerUntil(WebApplicationFactory<Program> f, Func<bool> condition, TimeSpan timeout)
    {
        using var scheduler = ActivatorUtilities.CreateInstance<WorkflowScheduler>(f.Services);
        var sw = Stopwatch.StartNew();
        await scheduler.StartAsync(CancellationToken.None);
        var reached = await WaitUntil(condition, timeout);
        var elapsed = sw.Elapsed;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await scheduler.StopAsync(cts.Token); } catch (OperationCanceledException) { }
        return (reached, elapsed);
    }

    public static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

// Een kleine webserver op 127.0.0.1 die bijhoudt wat er binnenkomt.
sealed class LocalServer : IAsyncDisposable
{
    public int Hits;
    public readonly ConcurrentQueue<string> Paths = new();
    public readonly ConcurrentQueue<string> Cookies = new();
    public string Url { get; private set; } = "";
    WebApplication? app;

    public static async Task<LocalServer> StartAsync(Func<HttpContext, Task>? handler = null)
    {
        var s = new LocalServer();
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        s.app = b.Build();
        s.app.Run(async ctx =>
        {
            Interlocked.Increment(ref s.Hits);
            s.Paths.Enqueue(ctx.Request.Path);
            s.Cookies.Enqueue(ctx.Request.Headers.Cookie.ToString());
            if (handler is not null) await handler(ctx);
            else await ctx.Response.WriteAsync("intern geheim");
        });
        await s.app.StartAsync();
        s.Url = s.app.Urls.First().TrimEnd('/');
        return s;
    }

    public async ValueTask DisposeAsync()
    {
        if (app is null) return;
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

// Een nep-mailserver die net genoeg SMTP spreekt om berichten van SmtpClient te ontvangen.
public sealed class FakeSmtpServer : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    public readonly ConcurrentQueue<string> Messages = new();
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public FakeSmtpServer()
    {
        listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 nep ESMTP");
        var envelope = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            var cmd = line.ToUpperInvariant();
            if (cmd.StartsWith("EHLO") || cmd.StartsWith("HELO")) await writer.WriteLineAsync("250 nep");
            else if (cmd.StartsWith("MAIL FROM") || cmd.StartsWith("RCPT TO")) { envelope.Append(line).Append("\r\n"); await writer.WriteLineAsync("250 OK"); }
            else if (cmd == "DATA")
            {
                await writer.WriteLineAsync("354 Ga je gang");
                var data = new StringBuilder();
                while (await reader.ReadLineAsync() is { } body && body != ".") data.Append(body).Append("\r\n");
                Messages.Enqueue(envelope + "\r\n" + data);
                envelope.Clear();
                await writer.WriteLineAsync("250 OK");
            }
            else if (cmd == "QUIT") { await writer.WriteLineAsync("221 Tot ziens"); return; }
            else await writer.WriteLineAsync("250 OK");
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
    }
}

// Onthoudt welke mail werkstromen zouden versturen, zonder echte mailserver.
public sealed class RecordingEmailSender : IEmailSender
{
    public readonly ConcurrentQueue<OutgoingEmail> Sent = new();
    public bool Configured => true;

    public Task SendAsync(OutgoingEmail mail, CancellationToken ct = default)
    {
        Sent.Enqueue(mail);
        return Task.CompletedTask;
    }
}
