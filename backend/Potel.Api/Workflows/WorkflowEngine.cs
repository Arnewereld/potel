using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Potel.Api.Data;
using Potel.Api.Endpoints;

namespace Potel.Api.Workflows;

// Voert werkstromen uit: van trigger, via acties en voorwaarden, tot het einde.
// Een "Wachten"-blok parkeert de run; de WorkflowScheduler pakt hem later weer op.
// Werkruimtes met een verlopen proef draaien niets meer, en elke run blijft binnen de grenzen uit WorkflowLimits.
public class WorkflowEngine(AppDb db, IHttpClientFactory httpFactory, IEmailSender email, IOptions<WorkflowLimits> options, BusinessClock clock, ILogger<WorkflowEngine> logger)
{
    readonly WorkflowLimits limits = options.Value;

    class Outcome
    {
        public string Status = "ok";
        public string Message = "";
        public string? Branch;
        public DateTime? WaitUntil;
        public bool Failed => Status == "fout";
    }

    // Mag deze werkruimte nu werkstromen draaien? Niet na een verlopen proef zonder abonnement.
    public async Task<bool> CanRunAsync() =>
        await db.Workspaces.FindAsync(db.TenantId) is { } w && !w.TrialExpired(DateTime.UtcNow);

    // Start elke actieve werkstroom die op deze trigger wacht. Fouten breken de aanroeper nooit.
    public async Task TriggerAsync(string triggerType, Dictionary<string, string> ctx, string description)
    {
        try
        {
            if (!await CanRunAsync()) return;
            var workflows = await db.Workflows.Where(w => w.Active).ToListAsync();
            foreach (var wf in workflows)
            {
                var starts = Graph.Parse(wf.GraphJson).Nodes.Where(n => n.Type == triggerType).Select(n => n.Id).ToList();
                if (starts.Count > 0) await StartRunAsync(wf, starts, new Dictionary<string, string>(ctx), description);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Werkstroom-trigger {Trigger} mislukt", triggerType);
        }
    }

    // Start een run. Geeft null als de werkruimte geen werkstromen meer mag draaien.
    public async Task<WorkflowRun?> RunAsync(Workflow wf, List<string> startNodeIds, Dictionary<string, string> ctx, string trigger) =>
        await CanRunAsync() ? await StartRunAsync(wf, startNodeIds, ctx, trigger) : null;

    async Task<WorkflowRun> StartRunAsync(Workflow wf, List<string> startNodeIds, Dictionary<string, string> ctx, string trigger)
    {
        var run = new WorkflowRun { WorkflowId = wf.Id, Trigger = trigger, ContextJson = JsonSerializer.Serialize(ctx) };
        db.WorkflowRuns.Add(run);
        await db.SaveChangesAsync();
        await ExecuteAsync(run, wf, startNodeIds, ctx, [], []);
        return run;
    }

    // Wachtende runs waarvan de wachttijd voorbij is, de oudste eerst.
    public async Task<List<int>> DueRunIdsAsync(int max = int.MaxValue)
    {
        var now = DateTime.UtcNow;
        return await db.WorkflowRuns.Where(r => r.Status == "wachtend" && r.ResumeAt <= now)
            .OrderBy(r => r.ResumeAt).ThenBy(r => r.Id).Select(r => r.Id).Take(Math.Max(0, max)).ToListAsync();
    }

    // Zet alle wachtende runs voort waarvan de wachttijd voorbij is. Een kapotte run houdt de rest niet tegen.
    public async Task ResumeDueAsync()
    {
        foreach (var id in await DueRunIdsAsync())
        {
            try
            {
                await ResumeAsync(id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Wachtende run {Run} voortzetten mislukt", id);
                db.ChangeTracker.Clear();
                await FailAsync(id);
            }
        }
    }

    // Zet één wachtende run voort.
    public async Task ResumeAsync(int runId)
    {
        if (!await CanRunAsync()) return;
        var now = DateTime.UtcNow;
        var run = await db.WorkflowRuns.FindAsync(runId);
        if (run is not { Status: "wachtend" } || run.ResumeAt > now) return;
        var wf = await db.Workflows.FindAsync(run.WorkflowId);
        if (wf is null)
        {
            run.Status = "fout"; run.FinishedAt = now; run.ResumeAt = null;
            await db.SaveChangesAsync();
            return;
        }
        var pending = JsonSerializer.Deserialize<List<PendingStep>>(run.PendingJson, Graph.Json) ?? [];
        var due = pending.Where(p => p.ResumeAt <= now).Select(p => p.NodeId).ToList();
        var later = pending.Where(p => p.ResumeAt > now).ToList();
        var ctx = JsonSerializer.Deserialize<Dictionary<string, string>>(run.ContextJson) ?? [];
        var log = JsonSerializer.Deserialize<List<LogEntry>>(run.LogJson, Graph.Json) ?? [];
        run.Status = "bezig";
        await ExecuteAsync(run, wf, due, ctx, log, later);
    }

    // Zet een run die niet meer verder kan op fout, zodat hij niet elke ronde opnieuw vastloopt.
    public async Task FailAsync(int runId)
    {
        var now = DateTime.UtcNow;
        await db.WorkflowRuns.Where(r => r.Id == runId && r.Status != "klaar")
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "fout").SetProperty(r => r.FinishedAt, now).SetProperty(r => r.ResumeAt, (DateTime?)null));
    }

    // Actieve werkstromen met een Schema-trigger die nu aan de beurt zijn.
    public async Task<List<int>> DueScheduleIdsAsync(DateTime now, int max = int.MaxValue)
    {
        var workflows = await db.Workflows.Where(w => w.Active).OrderBy(w => w.Id).ToListAsync();
        return workflows.Where(wf => DueStarts(wf, now).Count > 0).Select(wf => wf.Id).Take(Math.Max(0, max)).ToList();
    }

    static List<string> DueStarts(Workflow wf, DateTime now) =>
        Graph.Parse(wf.GraphJson).Nodes.Where(n => n.Type == "trigger.schedule" && IsDue(n, wf.LastScheduledAt, now)).Select(n => n.Id).ToList();

    // Start één geplande werkstroom als hij aan de beurt is.
    public async Task RunScheduleAsync(int workflowId, DateTime now)
    {
        if (!await CanRunAsync()) return;
        if (await db.Workflows.FindAsync(workflowId) is not { Active: true } wf) return;
        var due = DueStarts(wf, now);
        if (due.Count == 0) return;
        wf.LastScheduledAt = now;
        await db.SaveChangesAsync();
        await StartRunAsync(wf, due, [], "Schema");
    }

    // Start werkstromen met een Schema-trigger die aan de beurt zijn. Een kapotte werkstroom houdt de rest niet tegen.
    public async Task RunSchedulesAsync(DateTime now)
    {
        foreach (var id in await DueScheduleIdsAsync(now))
        {
            try
            {
                await RunScheduleAsync(id, now);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Geplande werkstroom {Workflow} mislukt", id);
                db.ChangeTracker.Clear();
            }
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

    static bool IsCall(GraphNode node) => node.Type is "action.email" or "action.webhook";

    async Task ExecuteAsync(WorkflowRun run, Workflow wf, List<string> start, Dictionary<string, string> ctx, List<LogEntry> log, List<PendingStep> pending)
    {
        var graph = Graph.Parse(wf.GraphJson);
        var nodes = graph.Nodes.ToDictionary(n => n.Id);
        var queue = new Queue<string>(start);
        var seconds = Math.Clamp(limits.MaxSecondsPerRun, 1, 3600);
        var tooLong = $"Gestopt: deze run liep langer dan {seconds} seconden.";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        string? stopped = null;
        var failed = false;

        while (queue.Count > 0)
        {
            if (run.Steps >= limits.MaxStepsPerRun) { stopped = $"Gestopt na {limits.MaxStepsPerRun} stappen. Zit er een rondje in je werkstroom?"; break; }
            if (timeout.IsCancellationRequested) { stopped = tooLong; break; }
            if (!nodes.TryGetValue(queue.Dequeue(), out var node)) continue;
            if (IsCall(node) && run.Calls >= limits.MaxCallsPerRun)
            {
                stopped = $"Gestopt: een run mag hooguit {limits.MaxCallsPerRun} e-mails en webhooks versturen. Zit er een rondje in je werkstroom?";
                break;
            }
            if (node.Type == "logic.wait" && run.Waits >= limits.MaxWaitsPerRun)
            {
                stopped = $"Gestopt na {limits.MaxWaitsPerRun} keer wachten. Zit er een rondje met een Wachten-blok in je werkstroom?";
                break;
            }
            run.Steps++;
            if (IsCall(node)) run.Calls++;

            Outcome outcome;
            try
            {
                outcome = await ExecuteNodeAsync(node, ctx, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                outcome = new Outcome { Status = "fout", Message = tooLong };
            }
            catch (Exception ex)
            {
                // De technische foutmelding hoort in het serverlog, niet bij de gebruiker.
                logger.LogWarning(ex, "Blok {Node} ({Type}) van werkstroom {Workflow} mislukt", node.Id, node.Type, wf.Id);
                outcome = new Outcome { Status = "fout", Message = "Er ging iets mis bij dit blok. Probeer het later opnieuw." };
            }

            log.Add(new LogEntry(node.Id, node.Label, node.Type, outcome.Status, outcome.Message, DateTime.UtcNow));
            if (outcome.Failed) { failed = true; break; }

            var next = graph.Edges.Where(e => e.From == node.Id && (outcome.Branch is null || e.Branch == outcome.Branch)).Select(e => e.To);
            if (outcome.WaitUntil is { } until)
            {
                run.Waits++;
                pending.AddRange(next.Select(id => new PendingStep(id, until)));
            }
            else
                foreach (var id in next) queue.Enqueue(id);
        }

        if (stopped is not null)
        {
            log.Add(new LogEntry("", "", "", "fout", stopped, DateTime.UtcNow));
            failed = true;
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

    // Telt een e-mail mee voor de daglimiet van deze werkruimte. Onwaar als de limiet voor vandaag al bereikt is.
    // In één update, zodat twee runs tegelijk samen niet over de limiet heen kunnen.
    async Task<bool> ReserveEmailAsync(int limit)
    {
        if (limit <= 0) return false;
        var today = DateTime.UtcNow.Date;
        var id = db.TenantId;
        return await db.Workspaces
            .Where(w => w.Id == id && (w.EmailDay != today || w.EmailsSent < limit))
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.EmailsSent, w => w.EmailDay == today ? w.EmailsSent + 1 : 1)
                .SetProperty(w => w.EmailDay, today)) == 1;
    }

    async Task<Outcome> ExecuteNodeAsync(GraphNode node, Dictionary<string, string> ctx, CancellationToken ct)
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
                var start = clock.Today.AddDays(days).AddHours(9);
                // Alleen een klant uit deze werkruimte koppelen, ook als de context van een oudere run komt.
                var customerId = Id("customer.id") is { } cid && await db.Customers.AnyAsync(c => c.Id == cid) ? cid : (int?)null;
                var appt = new Appointment
                {
                    Title = R("title", "Opvolgen: {{lead.name}}"),
                    Start = start, End = start.AddHours(1), Kind = node.Get("kind", "taak"),
                    CustomerId = customerId, Notes = $"Aangemaakt door werkstroom ({node.Label})",
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
                // Eén adres per blok, en niets in het onderwerp dat een extra kopregel (zoals Bcc) kan maken.
                if (!EmailRules.IsSingleAddress(to)) return Fail($"\"{EmailRules.OneLine(to, 80)}\" is geen geldig adres. Vul precies één e-mailadres in.");
                var subject = EmailRules.OneLine(R("subject", "Bericht van ons"));
                var body = R("body", "Beste {{lead.name}},");
                if (!email.Configured)
                {
                    db.Log("e-mail", $"E-mail \"{subject}\" aan {to} niet verstuurd: geen mailserver ingesteld");
                    await db.SaveChangesAsync();
                    return new Outcome { Status = "let op", Message = $"Niet verstuurd naar {to}: er is nog geen mailserver ingesteld (zie README)" };
                }
                var workspace = await db.Workspaces.FindAsync(db.TenantId);
                var limit = limits.EmailsPerDay(workspace?.Plan ?? Plans.Trial);
                if (!await ReserveEmailAsync(limit))
                    return new Outcome { Status = "let op", Message = $"Niet verstuurd naar {to}: je werkruimte heeft vandaag de daglimiet van {limit} e-mails bereikt. Morgen kan het weer." };
                // Van het adres van het platform, met de bedrijfsnaam als afzender; antwoorden gaan naar de werkruimte zelf.
                var settings = await db.Settings.OrderBy(x => x.Id).FirstOrDefaultAsync();
                try
                {
                    await email.SendAsync(new OutgoingEmail(to, subject, body, settings?.CompanyName ?? workspace?.Name, settings?.Email), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "E-mail van werkstroom niet verstuurd");
                    return Fail($"E-mail aan {to} kon niet worden verstuurd. Probeer het later opnieuw.");
                }
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
                    // Dezelfde regels als in het portaal: versturen controleert alles en geeft een nummer, daarna alleen vooruit.
                    await using var tx = await WriteLock.BeginAsync(db);
                    var inv = Id("invoice.id") is { } iid ? await db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == iid) : null;
                    if (inv is null) return Fail("Er is geen factuur in deze run");
                    var (problem, _) = await InvoiceEndpoints.ChangeStatusAsync(db, clock, inv, status);
                    if (problem is not null) return Fail(problem.Error);
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                    WorkflowContext.AddInvoice(ctx, inv);
                    return Ok($"{InvoiceEndpoints.Label(inv)} staat nu op {status}");
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
                var settings = await SettingsEndpoints.GetAsync(db);
                var regime = VatRegimes.DefaultFor(settings, await db.Customers.FindAsync(customerId.Value));
                var today = clock.Today;
                // Een concept zonder nummer; het nummer komt pas als je hem verstuurt.
                var inv = new Invoice
                {
                    CustomerId = customerId.Value, Status = InvoiceStatus.Draft, IssueDate = today, DueDate = today.AddDays(settings.PaymentTermDays),
                    DeliveryFrom = today, VatRegime = regime, Notes = SettingsEndpoints.DefaultNote,
                    Lines = [new InvoiceLine { Description = R("description", "Diensten"), Quantity = 1, UnitPrice = amount, VatRate = VatRegimes.ZeroVat(regime) ? 0 : 21 }],
                };
                db.Invoices.Add(inv);
                db.Log("factuur", "Conceptfactuur aangemaakt door werkstroom");
                await db.SaveChangesAsync();
                WorkflowContext.AddInvoice(ctx, inv);
                return Ok("Conceptfactuur aangemaakt");
            }

            case "action.webhook":
            {
                var url = R("url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                    return Fail("Vul een geldige webhook-URL in (http of https)");
                if (!limits.AllowPrivateWebhooks && !WebhookGuard.IsAllowedPort(uri.Port))
                    return Fail("Een webhook mag alleen naar poort 80 (http) of 443 (https)");
                var client = httpFactory.CreateClient(WebhookGuard.ClientName);
                using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(ctx) };
                try
                {
                    // Alleen de statuscode telt; het antwoord zelf lezen we niet.
                    using var res = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    var code = (int)res.StatusCode;
                    if (res.IsSuccessStatusCode) return Ok($"Verstuurd naar {uri.Host} ({code})");
                    return Fail(code is >= 300 and < 400 ? $"{uri.Host} stuurde door ({code}); doorverwijzingen volgen we niet" : $"{uri.Host} antwoordde met {code}");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or WebhookBlockedException && !ct.IsCancellationRequested)
                {
                    // Altijd dezelfde melding: geen verschil tussen een dicht, geblokkeerd of traag adres, en geen technische details.
                    logger.LogInformation("Webhook naar {Host} mislukt: {Error}", uri.Host, ex.Message);
                    return Fail($"Webhook naar {uri.Host} mislukt: het adres is niet bereikbaar of niet toegestaan. Alleen openbare adressen kunnen een webhook ontvangen.");
                }
            }

            case "logic.wait":
            {
                // Minstens één: een wachttijd van nul zou een rondje zonder pauze maken.
                var amount = int.TryParse(node.Get("amount", "1"), out var n) ? Math.Max(1, n) : 1;
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
