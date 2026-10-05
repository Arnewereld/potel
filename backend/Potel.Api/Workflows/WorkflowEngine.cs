using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Endpoints;

namespace Potel.Api.Workflows;

// Voert werkstromen uit: van trigger, via acties en voorwaarden, tot het einde.
// Een "Wachten"-blok parkeert de run; de WorkflowScheduler pakt hem later weer op.
public class WorkflowEngine(AppDb db, IHttpClientFactory httpFactory, IEmailSender email, ILogger<WorkflowEngine> logger)
{
    const int MaxSteps = 200;

    class Outcome
    {
        public string Status = "ok";
        public string Message = "";
        public string? Branch;
        public DateTime? WaitUntil;
        public bool Failed => Status == "fout";
    }

    // Start elke actieve werkstroom die op deze trigger wacht. Fouten breken de aanroeper nooit.
    public async Task TriggerAsync(string triggerType, Dictionary<string, string> ctx, string description)
    {
        try
        {
            var workflows = await db.Workflows.Where(w => w.Active).ToListAsync();
            foreach (var wf in workflows)
            {
                var starts = Graph.Parse(wf.GraphJson).Nodes.Where(n => n.Type == triggerType).Select(n => n.Id).ToList();
                if (starts.Count > 0) await RunAsync(wf, starts, new Dictionary<string, string>(ctx), description);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Werkstroom-trigger {Trigger} mislukt", triggerType);
        }
    }

    public async Task<WorkflowRun> RunAsync(Workflow wf, List<string> startNodeIds, Dictionary<string, string> ctx, string trigger)
    {
        var run = new WorkflowRun { WorkflowId = wf.Id, Trigger = trigger, ContextJson = JsonSerializer.Serialize(ctx) };
        db.WorkflowRuns.Add(run);
        await db.SaveChangesAsync();
        await ExecuteAsync(run, wf, startNodeIds, ctx, [], []);
        return run;
    }

    // Zet wachtende runs voort waarvan de wachttijd voorbij is.
    public async Task ResumeDueAsync()
    {
        var now = DateTime.UtcNow;
        var runs = await db.WorkflowRuns.Where(r => r.Status == "wachtend" && r.ResumeAt <= now).ToListAsync();
        foreach (var run in runs)
        {
            var wf = await db.Workflows.FindAsync(run.WorkflowId);
            if (wf is null) { run.Status = "fout"; run.FinishedAt = now; continue; }
            var pending = JsonSerializer.Deserialize<List<PendingStep>>(run.PendingJson, Graph.Json) ?? [];
            var due = pending.Where(p => p.ResumeAt <= now).Select(p => p.NodeId).ToList();
            var later = pending.Where(p => p.ResumeAt > now).ToList();
            var ctx = JsonSerializer.Deserialize<Dictionary<string, string>>(run.ContextJson) ?? [];
            var log = JsonSerializer.Deserialize<List<LogEntry>>(run.LogJson, Graph.Json) ?? [];
            run.Status = "bezig";
            await ExecuteAsync(run, wf, due, ctx, log, later);
        }
        await db.SaveChangesAsync();
    }

    // Start werkstromen met een Schema-trigger die aan de beurt zijn.
    public async Task RunSchedulesAsync(DateTime now)
    {
        var workflows = await db.Workflows.Where(w => w.Active).ToListAsync();
        foreach (var wf in workflows)
        {
            var starts = Graph.Parse(wf.GraphJson).Nodes.Where(n => n.Type == "trigger.schedule").ToList();
            var due = starts.Where(n => IsDue(n, wf.LastScheduledAt, now)).Select(n => n.Id).ToList();
            if (due.Count == 0) continue;
            wf.LastScheduledAt = now;
            await db.SaveChangesAsync();
            await RunAsync(wf, due, [], "Schema");
        }
    }

    public static bool IsDue(GraphNode node, DateTime? last, DateTime now)
    {
        var hour = int.TryParse(node.Get("hour", "9"), out var h) ? h : 9;
        return node.Get("interval", "dag") switch
        {
            "uur" => last is null || now - last >= TimeSpan.FromHours(1),
            "week" => (int)now.DayOfWeek == (int.TryParse(node.Get("weekday", "1"), out var d) ? d % 7 : 1)
                      && now.Hour >= hour && (last is null || now - last > TimeSpan.FromDays(6)),
            _ => now.Hour >= hour && (last is null || last.Value.Date < now.Date),
        };
    }

    async Task ExecuteAsync(WorkflowRun run, Workflow wf, List<string> start, Dictionary<string, string> ctx, List<LogEntry> log, List<PendingStep> pending)
    {
        var graph = Graph.Parse(wf.GraphJson);
        var nodes = graph.Nodes.ToDictionary(n => n.Id);
        var queue = new Queue<string>(start);
        var steps = 0;
        var failed = false;

        while (queue.Count > 0 && !failed)
        {
            if (++steps > MaxSteps)
            {
                log.Add(new LogEntry("", "", "", "fout", $"Gestopt na {MaxSteps} stappen. Zit er een rondje in je werkstroom?", DateTime.UtcNow));
                failed = true;
                break;
            }
            if (!nodes.TryGetValue(queue.Dequeue(), out var node)) continue;

            Outcome outcome;
            try
            {
                outcome = await ExecuteNodeAsync(node, ctx);
            }
            catch (Exception ex)
            {
                outcome = new Outcome { Status = "fout", Message = ex.Message };
            }

            log.Add(new LogEntry(node.Id, node.Label, node.Type, outcome.Status, outcome.Message, DateTime.UtcNow));
            if (outcome.Failed) { failed = true; break; }

            var next = graph.Edges.Where(e => e.From == node.Id && (outcome.Branch is null || e.Branch == outcome.Branch)).Select(e => e.To);
            if (outcome.WaitUntil is { } until)
                pending.AddRange(next.Select(id => new PendingStep(id, until)));
            else
                foreach (var id in next) queue.Enqueue(id);
        }

        run.ContextJson = JsonSerializer.Serialize(ctx);
        run.LogJson = JsonSerializer.Serialize(log, Graph.Json);
        run.PendingJson = JsonSerializer.Serialize(failed ? [] : pending, Graph.Json);
        if (failed) { run.Status = "fout"; run.FinishedAt = DateTime.UtcNow; run.ResumeAt = null; }
        else if (pending.Count > 0) { run.Status = "wachtend"; run.ResumeAt = pending.Min(p => p.ResumeAt); }
        else { run.Status = "klaar"; run.FinishedAt = DateTime.UtcNow; run.ResumeAt = null; }

        if (failed) db.Log("werkstroom", $"Werkstroom {wf.Name} is vastgelopen");
        await db.SaveChangesAsync();
    }

    async Task<Outcome> ExecuteNodeAsync(GraphNode node, Dictionary<string, string> ctx)
    {
        string R(string key, string fallback = "") => WorkflowContext.Render(node.Get(key, fallback), ctx);
        int? Id(string key) => ctx.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : null;
        Outcome Ok(string msg) => new() { Message = msg };
        Outcome Fail(string msg) => new() { Status = "fout", Message = msg };

        switch (node.Type)
        {
            case var t when t.StartsWith("trigger."):
                return Ok("Gestart");

            case "action.task":
            {
                var days = int.TryParse(node.Get("days", "1"), out var d) ? d : 1;
                var start = DateTime.Now.Date.AddDays(days).AddHours(9);
                var appt = new Appointment
                {
                    Title = R("title", "Opvolgen: {{lead.name}}"),
                    Start = start, End = start.AddHours(1), Kind = node.Get("kind", "taak"),
                    CustomerId = Id("customer.id"), Notes = $"Aangemaakt door werkstroom ({node.Label})",
                };
                db.Appointments.Add(appt);
                db.Log("planning", $"{appt.Title} ingepland door werkstroom");
                await db.SaveChangesAsync();
                return Ok($"Taak \"{appt.Title}\" ingepland op {start:dd-MM-yyyy HH:mm}");
            }

            case "action.email":
            {
                var to = R("to", "{{lead.email}}").Trim();
                if (to == "") to = WorkflowContext.Render("{{customer.email}}", ctx).Trim();
                if (to == "") return Fail("Geen e-mailadres om naar te sturen");
                var subject = R("subject", "Bericht van ons");
                var body = R("body", "Beste {{lead.name}},");
                if (!email.Configured)
                {
                    db.Log("e-mail", $"E-mail \"{subject}\" aan {to} niet verstuurd: geen mailserver ingesteld");
                    await db.SaveChangesAsync();
                    return new Outcome { Status = "let op", Message = $"Niet verstuurd naar {to}: er is nog geen mailserver ingesteld (zie README)" };
                }
                await email.SendAsync(to, subject, body);
                db.Log("e-mail", $"E-mail \"{subject}\" verstuurd aan {to}");
                await db.SaveChangesAsync();
                return Ok($"E-mail verstuurd aan {to}");
            }

            case "action.status":
            {
                var target = node.Get("target", "lead");
                var status = node.Get("status", target == "lead" ? "contact" : "verzonden");
                if (target == "invoice")
                {
                    if (!InvoiceStatus.All.Contains(status)) return Fail($"Onbekende factuurstatus \"{status}\"");
                    var inv = Id("invoice.id") is { } iid ? await db.Invoices.FindAsync(iid) : null;
                    if (inv is null) return Fail("Er is geen factuur in deze run");
                    inv.Status = status;
                    ctx["invoice.status"] = status;
                    db.Log("factuur", $"Factuur {inv.Number} op {status} gezet door werkstroom");
                    await db.SaveChangesAsync();
                    return Ok($"Factuur {inv.Number} staat nu op {status}");
                }
                if (!LeadStatus.All.Contains(status)) return Fail($"Onbekende leadstatus \"{status}\"");
                var lead = Id("lead.id") is { } lid ? await db.Leads.FindAsync(lid) : null;
                if (lead is null) return Fail("Er is geen lead in deze run");
                lead.Status = status;
                ctx["lead.status"] = status;
                db.Log("lead", $"Lead {lead.Name} op {status} gezet door werkstroom");
                await db.SaveChangesAsync();
                return Ok($"Lead {lead.Name} staat nu op {status}");
            }

            case "action.customer":
            {
                var lead = Id("lead.id") is { } lid ? await db.Leads.FindAsync(lid) : null;
                if (lead is null) return Fail("Er is geen lead om om te zetten");
                var customer = await LeadEndpoints.ConvertAsync(db, lead);
                WorkflowContext.AddCustomer(ctx, customer);
                ctx["lead.status"] = lead.Status;
                return Ok($"{customer.Name} is nu klant");
            }

            case "action.invoice":
            {
                var customerId = Id("customer.id");
                if (customerId is null || !await db.Customers.AnyAsync(c => c.Id == customerId))
                    return Fail("Er is geen klant in deze run. Zet een lead eerst om met \"Klant maken\".");
                var amount = WorkflowContext.TryNumber(R("amount", "{{lead.value}}"), out var a) ? a : 0;
                var inv = new Invoice
                {
                    Number = await InvoiceEndpoints.NextNumber(db), CustomerId = customerId.Value, Status = "concept",
                    Lines = [new InvoiceLine { Description = R("description", "Diensten"), Quantity = 1, UnitPrice = amount, VatRate = 21 }],
                };
                db.Invoices.Add(inv);
                db.Log("factuur", $"Conceptfactuur {inv.Number} aangemaakt door werkstroom");
                await db.SaveChangesAsync();
                WorkflowContext.AddInvoice(ctx, inv);
                return Ok($"Conceptfactuur {inv.Number} aangemaakt");
            }

            case "action.webhook":
            {
                var url = R("url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                    return Fail("Vul een geldige webhook-URL in (http of https)");
                var client = httpFactory.CreateClient("webhooks");
                using var res = await client.PostAsJsonAsync(uri, ctx);
                return res.IsSuccessStatusCode
                    ? Ok($"Verstuurd naar {uri.Host} ({(int)res.StatusCode})")
                    : Fail($"{uri.Host} antwoordde met {(int)res.StatusCode}");
            }

            case "logic.wait":
            {
                var amount = int.TryParse(node.Get("amount", "1"), out var n) ? Math.Max(0, n) : 1;
                var unit = node.Get("unit", "dagen");
                var span = unit switch { "minuten" => TimeSpan.FromMinutes(amount), "uren" => TimeSpan.FromHours(amount), _ => TimeSpan.FromDays(amount) };
                var until = DateTime.UtcNow + span;
                return new Outcome { Status = "wacht", Message = $"Wacht {amount} {unit}, gaat verder rond {until.ToLocalTime():dd-MM HH:mm}", WaitUntil = until };
            }

            case "logic.if":
            {
                var field = node.Get("field", "lead.value");
                var op = node.Get("operator", ">");
                var expected = R("value");
                var actual = ctx.TryGetValue(field, out var v) ? v : "";
                var yes = WorkflowContext.Compare(actual, op, expected);
                return new Outcome { Branch = yes ? "ja" : "nee", Message = $"{field} ({(actual == "" ? "leeg" : actual)}) {op} {expected}: {(yes ? "ja" : "nee")}" };
            }

            default:
                return Fail($"Onbekend blok \"{node.Type}\"");
        }
    }
}
