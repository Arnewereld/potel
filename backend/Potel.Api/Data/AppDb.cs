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

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Invoice>().HasIndex(i => i.Number).IsUnique();
        b.Entity<Invoice>().HasMany(i => i.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Invoice>().HasOne(i => i.Customer).WithMany().HasForeignKey(i => i.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Log(string kind, string text) => Activities.Add(new Activity { Kind = kind, Text = text });
}
