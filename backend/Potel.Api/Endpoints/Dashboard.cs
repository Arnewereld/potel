using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboard(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", async (AppDb db, BusinessClock clock) =>
        {
            await InvoiceEndpoints.MarkOverdue(db, clock);
            // Week, maand, kwartaal en jaar zoals ze in Nederland lopen, niet in de tijdzone van de server.
            var today = clock.Today;
            var invoices = await db.Invoices.Include(i => i.Lines).AsNoTracking().ToListAsync();
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

            // Btw over de facturen van dit kwartaal; de aangifte moet uiterlijk een maand na het kwartaal binnen zijn.
            var quarter = (today.Month - 1) / 3;
            var quarterStart = new DateTime(today.Year, quarter * 3 + 1, 1);
            var quarterInvoices = invoices.Where(i => i.Status != InvoiceStatus.Draft && i.IssueDate >= quarterStart && i.IssueDate < quarterStart.AddMonths(3)).ToList();
            var months = Enumerable.Range(0, 6).Select(i => monthStart.AddMonths(i - 5)).ToList();

            return new
            {
                customers = await db.Customers.CountAsync(),
                openLeads = leads.Count(l => l.Status is not ("gewonnen" or "verloren")),
                pipelineValue = leads.Where(l => l.Status is not ("gewonnen" or "verloren")).Sum(l => l.Value),
                revenueYear = invoices.Where(i => i.Status == "betaald" && i.IssueDate.Year == today.Year).Sum(InvoiceEndpoints.Total),
                outstanding = invoices.Where(i => i.Status is "verzonden" or "verlopen").Sum(InvoiceEndpoints.Total),
                overdue = invoices.Count(i => i.Status == "verlopen"),
                revenueByMonth = months.Select(m => new
                {
                    month = m.ToString("yyyy-MM"),
                    total = invoices.Where(i => i.Status == "betaald" && i.IssueDate.Year == m.Year && i.IssueDate.Month == m.Month).Sum(InvoiceEndpoints.Total)
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
                vat = new
                {
                    label = $"Q{quarter + 1} {today.Year}",
                    amount = quarterInvoices.Sum(i => i.Totals.Vat),
                    revenue = quarterInvoices.Sum(i => i.Totals.Subtotal),
                    dueDate = quarterStart.AddMonths(4).AddDays(-1).ToString("yyyy-MM-dd"),
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
            return Results.Ok(projects.Concat(customers).Concat(leads).Concat(invoices));
        });
    }
}
