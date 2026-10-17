using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Notifications;
using Potel.Api.Workflows;

namespace Potel.Api.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboard(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", async (AppDb db, BusinessClock clock, WorkflowEngine engine, Notifier notifier, ClaimsPrincipal user) =>
        {
            await InvoiceEndpoints.MarkOverdue(db, clock, engine, notifier);
            // Week, maand, kwartaal en jaar zoals ze in Nederland lopen, niet in de tijdzone van de server.
            var today = clock.Today;
            var invoices = await db.Invoices.Include(i => i.Lines).Include(i => i.Credits).ThenInclude(c => c.Lines).Include(i => i.Payments).AsSplitQuery().AsNoTracking().ToListAsync();
            var leads = await db.Leads.AsNoTracking().ToListAsync();

            var monthStart = new DateTime(today.Year, today.Month, 1);

            // Uren: deze week (maandag t/m zondag) en dit kalenderjaar tot en met vandaag, voor het urencriterium.
            var settings = await SettingsEndpoints.GetAsync(db);
            var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var yearStart = new DateTime(today.Year, 1, 1);
            var from = weekStart < yearStart ? weekStart : yearStart;
            var until = weekStart.AddDays(7) > today ? weekStart.AddDays(7) : today.AddDays(1);
            var entries = await db.TimeEntries.Where(t => t.Date >= from && t.Date < until).AsNoTracking().ToListAsync();
            var year = entries.Where(t => t.Date >= yearStart && t.Date <= today).ToList();
            var week = entries.Where(t => t.Date >= weekStart && t.Date < weekStart.AddDays(7)).ToList();
            var projects = await ProjectEndpoints.ListAsync(db);
            var unbilledMinutes = projects.Sum(p => p.MinutesUnbilled);

            // De btw-aangifte van dit kwartaal, met dezelfde som als de pagina Btw-aangifte.
            // Met de KOR doe je geen btw-aangifte, dus dan is er ook geen datum.
            var vatReturn = await VatReturn.CalculateAsync(db, clock, today.Year, VatReturn.QuarterOf(today));
            var months = Enumerable.Range(0, 6).Select(i => monthStart.AddMonths(i - 5)).ToList();
            // Ontvangen: betaalde facturen, min verrekende creditnota's. Een creditnota op een factuur die nooit betaald is
            // (omdat hij helemaal gecrediteerd is) telt niet mee; die factuur telde ook niet mee.
            var paidIds = invoices.Where(i => i.Status == InvoiceStatus.Paid).Select(i => i.Id).ToHashSet();
            var received = invoices.Where(i => i.Status == InvoiceStatus.Paid && (i.CreditForInvoiceId is not { } original || paidIds.Contains(original))).ToList();

            // Offertes: wat er openstaat en hoeveel van de offertes van de afgelopen 12 maanden geaccepteerd zijn. Ingetrokken
            // offertes tellen niet mee in dat percentage; daar heeft je klant niet over besloten.
            var quotes = await db.Quotes.Include(q => q.Lines).AsSplitQuery().AsNoTracking().Where(q => q.Status != QuoteStatus.Draft).ToListAsync();
            var openQuotes = quotes.Where(q => QuoteStatus.Of(q, today) == QuoteStatus.Sent).ToList();
            var decided = quotes.Where(q => q.IssueDate >= today.AddMonths(-12)).Select(q => QuoteStatus.Of(q, today))
                .Where(s => s is QuoteStatus.Accepted or QuoteStatus.Rejected or QuoteStatus.Expired).ToList();

            // De belastingpot gaat ook over je loon en je voorlopige aanslag, dus die ziet alleen een beheerder.
            var reserve = user.IsInRole(Roles.Admin) ? await TaxReserve.CalculateAsync(db, clock, today.Year) : null;
            // Wat er deze maand naar verwachting binnenkomt, uit de cashflow-prognose op de pagina Inzichten. Ook alleen voor een beheerder.
            (decimal ExpectedIn, decimal Late)? expected = user.IsInRole(Roles.Admin) ? await Cashflow.ThisMonthAsync(db, clock) : null;

            return new
            {
                customers = await db.Customers.CountAsync(),
                openLeads = leads.Count(l => l.Status is not ("gewonnen" or "verloren")),
                pipelineValue = leads.Where(l => l.Status is not ("gewonnen" or "verloren")).Sum(l => l.Value),
                revenueYear = received.Where(i => i.IssueDate.Year == today.Year).Sum(InvoiceEndpoints.Total),
                // Wat klanten nog moeten betalen: per factuur het totaal min wat er al gecrediteerd is.
                outstanding = invoices.Sum(i => i.OpenAmount),
                overdue = invoices.Count(i => i.Status == "verlopen" && i.OpenAmount > 0),
                revenueByMonth = months.Select(m => new
                {
                    month = m.ToString("yyyy-MM"),
                    total = received.Where(i => i.IssueDate.Year == m.Year && i.IssueDate.Month == m.Month).Sum(InvoiceEndpoints.Total)
                }),
                leadsByStatus = LeadStatus.All.Select(s => new { status = s, count = leads.Count(l => l.Status == s), value = leads.Where(l => l.Status == s).Sum(l => l.Value) }),
                hours = new
                {
                    week = week.Sum(t => t.Minutes),
                    weekBillable = week.Where(t => t.Billable).Sum(t => t.Minutes),
                    weekTarget = settings.WeeklyHoursTarget,
                    year = year.Sum(t => t.Minutes),
                    yearTarget = settings.YearlyHoursTarget,
                    // Wat je per week moet halen om het jaardoel nog te halen.
                    weeksLeft = Math.Max(1, (int)Math.Ceiling((new DateTime(today.Year, 12, 31) - today).TotalDays / 7)),
                    byDay = Enumerable.Range(0, 7).Select(d => weekStart.AddDays(d)).Select(d => new { date = d.ToString("yyyy-MM-dd"), minutes = week.Where(t => t.Date == d).Sum(t => t.Minutes) }),
                },
                unbilled = new { minutes = unbilledMinutes, value = projects.Sum(p => p.UnbilledValue) },
                quotes = new
                {
                    open = openQuotes.Count,
                    // Zonder btw, zoals de waarde van leads.
                    openValue = openQuotes.Sum(q => q.Totals.Subtotal),
                    expiringSoon = openQuotes.Count(q => q.ValidUntil.Date <= today.AddDays(7)),
                    accepted = decided.Count(s => s == QuoteStatus.Accepted),
                    decided = decided.Count,
                    acceptanceRate = decided.Count == 0 ? (int?)null : (int)Math.Round(100m * decided.Count(s => s == QuoteStatus.Accepted) / decided.Count, MidpointRounding.AwayFromZero),
                    recent = quotes.Where(q => q.DecidedAt != null).OrderByDescending(q => q.DecidedAt).Take(3)
                        .Select(q => new { q.Id, q.Number, q.Status, q.DecidedAt, q.DecidedName, buyer = q.Buyer?.Name, subtotal = q.Totals.Subtotal }),
                },
                vat = new
                {
                    label = vatReturn.Label,
                    year = vatReturn.Year,
                    quarter = vatReturn.Quarter,
                    // Te betalen (positief) of terug te vragen (negatief): rubriek 5a min 5b.
                    amount = vatReturn.Total,
                    revenue = vatReturn.Revenue,
                    kor = vatReturn.Kor,
                    dueDate = vatReturn.Kor ? null : vatReturn.DueDate.ToString("yyyy-MM-dd"),
                },
                taxReserve = reserve is null ? null : new
                {
                    reserve.Year, reserve.Available, reserve.Missing, reserve.Mode, reserve.Kor,
                    vat = reserve.VatTotal,
                    // Inkomstenbelasting en Zvw: voor het hele jaar, wat de voorlopige aanslag dekt, wat je nog opzij zet en per maand.
                    reserve.Total, reserve.Provisional, reserve.Remaining, reserve.PerMonth, reserve.MonthsLeft,
                },
                cashflow = expected is not { } c ? null : new
                {
                    month = monthStart.ToString("yyyy-MM"),
                    expectedIn = c.ExpectedIn,
                    late = c.Late,
                    // Voorbeelddata telt in de prognose niet mee, dus bij alleen voorbeelden staat hier nul.
                    demoData = await db.Invoices.AnyAsync(i => i.IsDemo),
                },
                projects = projects.Where(p => p.Status == "actief").OrderByDescending(p => p.LastEntry).Take(6),
                upcoming = await db.Appointments.Where(a => a.End >= clock.Now && !a.Done).OrderBy(a => a.Start).Take(5).ToListAsync(),
                activity = await db.Activities.OrderByDescending(a => a.Id).Take(12).ToListAsync(),
            };
        });

        api.MapGet("/search", async (AppDb db, string q) =>
        {
            q = q.Trim().ToLower();
            if (q.Length < 2) return Results.Ok(Array.Empty<object>());
            var customers = await db.Customers
                .Where(c => c.Name.ToLower().Contains(q) || (c.Company ?? "").ToLower().Contains(q) || (c.Email ?? "").ToLower().Contains(q))
                .Take(5).Select(c => new { type = "klant", id = c.Id, title = c.Name, subtitle = c.Company }).ToListAsync();
            var leads = await db.Leads
                .Where(l => l.Name.ToLower().Contains(q) || (l.Company ?? "").ToLower().Contains(q))
                .Take(5).Select(l => new { type = "lead", id = l.Id, title = l.Name, subtitle = l.Company }).ToListAsync();
            var invoices = await db.Invoices.Include(i => i.Customer)
                .Where(i => (i.Number ?? "").ToLower().Contains(q) || i.Customer!.Name.ToLower().Contains(q))
                .Take(5).Select(i => new { type = "factuur", id = i.Id, title = i.Number ?? "Concept", subtitle = (string?)i.Customer!.Name }).ToListAsync();
            var projects = await db.Projects.Include(p => p.Customer)
                .Where(p => p.Name.ToLower().Contains(q) || (p.Customer!.Company ?? p.Customer!.Name).ToLower().Contains(q))
                .Take(5).Select(p => new { type = "project", id = p.Id, title = p.Name, subtitle = (string?)(p.Customer!.Company ?? p.Customer!.Name) }).ToListAsync();
            var quotes = await db.Quotes
                .Where(x => (x.Number ?? "").ToLower().Contains(q) || (x.Subject ?? "").ToLower().Contains(q)
                            || (x.Customer != null && (x.Customer.Company ?? x.Customer.Name).ToLower().Contains(q))
                            || (x.Lead != null && (x.Lead.Company ?? x.Lead.Name).ToLower().Contains(q)))
                .OrderByDescending(x => x.Id).Take(5)
                .Select(x => new { type = "offerte", id = x.Id, title = x.Number ?? "Conceptofferte", subtitle = x.Customer != null ? (x.Customer.Company ?? x.Customer.Name) : x.Lead != null ? (x.Lead.Company ?? x.Lead.Name) : null }).ToListAsync();
            return Results.Ok(projects.Concat(customers).Concat(leads).Concat(invoices).Concat(quotes));
        });
    }
}
