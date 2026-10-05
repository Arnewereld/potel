using Microsoft.EntityFrameworkCore;

namespace Potel.Api.Data;

public class AppDb(DbContextOptions<AppDb> options, Tenant tenant) : DbContext(options)
{
    // Wordt in de queryfilters gebruikt; EF leest hem per query opnieuw uit.
    public int TenantId => tenant.WorkspaceId;
    public Tenant Tenant => tenant;

    public DbSet<Workspace> Workspaces => Set<Workspace>();
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
        b.Entity<Invoice>().HasIndex(i => new { i.WorkspaceId, i.Number }).IsUnique();
        b.Entity<Settings>().HasIndex(x => x.WorkspaceId).IsUnique();
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();
        b.Entity<WorkflowRun>().HasIndex(r => new { r.Status, r.ResumeAt });
        b.Entity<Invoice>().HasMany(i => i.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Invoice>().HasOne(i => i.Customer).WithMany().HasForeignKey(i => i.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Project>().HasOne(p => p.Customer).WithMany().HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TimeEntry>().HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);
        // Wordt een factuur verwijderd, dan komen de uren weer vrij om te factureren.
        b.Entity<TimeEntry>().HasOne<Invoice>().WithMany().HasForeignKey(t => t.InvoiceId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<TimeEntry>().HasIndex(t => t.Date);
        FilterByWorkspace(b);
    }

    // Elke tabel met een werkruimte krijgt een filter, zodat een werkruimte nooit gegevens van een ander ziet.
    void FilterByWorkspace(ModelBuilder b)
    {
        Filter<Customer>(b); Filter<Lead>(b); Filter<Invoice>(b); Filter<Appointment>(b); Filter<Project>(b); Filter<TimeEntry>(b);
        Filter<Settings>(b); Filter<CustomModule>(b); Filter<CustomRecord>(b); Filter<Workflow>(b); Filter<Activity>(b);
        Filter<User>(b); Filter<WorkflowRun>(b);
    }

    void Filter<T>(ModelBuilder b) where T : class, IWorkspaceOwned
    {
        b.Entity<T>().HasQueryFilter(e => e.WorkspaceId == TenantId);
        b.Entity<T>().HasIndex(e => e.WorkspaceId);
    }

    // Nieuwe records horen altijd bij de huidige werkruimte, wat de client ook meestuurt,
    // en een bestaand record kan nooit naar een andere werkruimte verhuizen.
    void StampWorkspace()
    {
        foreach (var entry in ChangeTracker.Entries<IWorkspaceOwned>())
        {
            if (entry.State == EntityState.Added)
            {
                if (TenantId == 0) throw new InvalidOperationException("Geen werkruimte bekend voor dit nieuwe record");
                entry.Entity.WorkspaceId = TenantId;
            }
            else if (entry.State == EntityState.Modified && entry.Property(e => e.WorkspaceId).IsModified)
            {
                entry.Property(e => e.WorkspaceId).CurrentValue = entry.Property(e => e.WorkspaceId).OriginalValue;
                entry.Property(e => e.WorkspaceId).IsModified = false;
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampWorkspace();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampWorkspace();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public void Log(string kind, string text) => Activities.Add(new Activity { Kind = kind, Text = text });
}
