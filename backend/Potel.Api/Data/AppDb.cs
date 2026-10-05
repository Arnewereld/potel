using Microsoft.EntityFrameworkCore;

namespace Potel.Api.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<CustomModule> CustomModules => Set<CustomModule>();
    public DbSet<CustomRecord> CustomRecords => Set<CustomRecord>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<User> Users => Set<User>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();
    public DbSet<Settings> Settings => Set<Settings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Invoice>().HasIndex(i => i.Number).IsUnique();
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();
        b.Entity<WorkflowRun>().HasIndex(r => new { r.Status, r.ResumeAt });
        b.Entity<Invoice>().HasMany(i => i.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Invoice>().HasOne(i => i.Customer).WithMany().HasForeignKey(i => i.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Project>().HasOne(p => p.Customer).WithMany().HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TimeEntry>().HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);
        // Wordt een factuur verwijderd, dan komen de uren weer vrij om te factureren.
        b.Entity<TimeEntry>().HasOne<Invoice>().WithMany().HasForeignKey(t => t.InvoiceId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<TimeEntry>().HasIndex(t => t.Date);
    }

    public void Log(string kind, string text) => Activities.Add(new Activity { Kind = kind, Text = text });
}
