using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboard(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", async (AppDb db) =>
        {
            await InvoiceEndpoints.MarkOverdue(db);
            var today = DateTime.UtcNow.Date;
            var invoices = await db.Invoices.Include(i => i.Lines).AsNoTracking().ToListAsync();
            var leads = await db.Leads.AsNoTracking().ToListAsync();

            var monthStart = new DateTime(today.Year, today.Month, 1);
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
                upcoming = await db.Appointments.Where(a => a.End >= DateTime.Now && !a.Done).OrderBy(a => a.Start).Take(5).ToListAsync(),
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
                .Where(i => i.Number.ToLower().Contains(q) || i.Customer!.Name.ToLower().Contains(q))
                .Take(5).Select(i => new { type = "factuur", id = i.Id, title = i.Number, subtitle = (string?)i.Customer!.Name }).ToListAsync();
            return Results.Ok(customers.Concat(leads).Concat(invoices));
        });
    }
}
