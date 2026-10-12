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
    public DbSet<PlatformCounter> PlatformCounters => Set<PlatformCounter>();
    public DbSet<AccountToken> AccountTokens => Set<AccountToken>();
    public DbSet<MolliePayment> MolliePayments => Set<MolliePayment>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<ExpenseReceipt> ExpenseReceipts => Set<ExpenseReceipt>();
    public DbSet<RecurringInvoice> RecurringInvoices => Set<RecurringInvoice>();
    public DbSet<RecurringInvoiceLine> RecurringInvoiceLines => Set<RecurringInvoiceLine>();
    public DbSet<InvoiceMail> InvoiceMails => Set<InvoiceMail>();
    public DbSet<InvoiceLink> InvoiceLinks => Set<InvoiceLink>();
    public DbSet<ReminderSettings> ReminderSettings => Set<ReminderSettings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Invoice>().HasIndex(i => new { i.WorkspaceId, i.Number }).IsUnique();
        b.Entity<Settings>().HasIndex(x => x.WorkspaceId).IsUnique();
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();
        b.Entity<WorkflowRun>().HasIndex(r => new { r.Status, r.ResumeAt });
        b.Entity<Invoice>().HasMany(i => i.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Invoice>().HasOne(i => i.Customer).WithMany().HasForeignKey(i => i.CustomerId).OnDelete(DeleteBehavior.Restrict);
        // Een creditnota wijst naar de factuur die hij corrigeert; die verstuurde factuur kan toch al niet weg.
        b.Entity<Invoice>().HasOne(i => i.CreditFor).WithMany(i => i.Credits).HasForeignKey(i => i.CreditForInvoiceId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Invoice>().OwnsOne(i => i.Seller);
        b.Entity<Invoice>().OwnsOne(i => i.Buyer);
        b.Entity<Invoice>().Property(i => i.VatRegime).HasDefaultValue(VatRegimes.Normal);
        b.Entity<Customer>().Property(c => c.Country).HasDefaultValue(Countries.Netherlands);
        b.Entity<Settings>().Property(s => s.VatRegime).HasDefaultValue(VatRegimes.Normal);
        b.Entity<Project>().HasOne(p => p.Customer).WithMany().HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TimeEntry>().HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);
        // Wordt een factuur verwijderd, dan komen de uren weer vrij om te factureren.
        b.Entity<TimeEntry>().HasOne<Invoice>().WithMany().HasForeignKey(t => t.InvoiceId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<TimeEntry>().HasOne<InvoiceLine>().WithMany().HasForeignKey(t => t.InvoiceLineId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<TimeEntry>().HasIndex(t => t.Date);
        b.Entity<TimeEntry>().HasIndex(t => new { t.WorkspaceId, t.ClientId }).IsUnique();
        // Tellers voor het hele platform; de rij bestaat altijd, zodat ophogen in één update kan.
        b.Entity<PlatformCounter>().HasKey(c => c.Key);
        b.Entity<PlatformCounter>().HasData(new PlatformCounter { Key = PlatformCounter.TrialEmails });
        // Een link uit een mail vinden we terug aan de hash van het token; weg met de gebruiker.
        b.Entity<AccountToken>().HasIndex(t => t.TokenHash).IsUnique();
        b.Entity<AccountToken>().HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        // Een werkruimte vinden we terug aan zijn klant bij Mollie, voor de incasso's van het abonnement.
        b.Entity<Workspace>().HasIndex(w => w.MollieCustomerId);
        // Een uitgave mag aan een project of klant hangen; gaat die weg, dan blijft de uitgave staan zonder koppeling.
        b.Entity<Expense>().HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Expense>().HasOne<Customer>().WithMany().HasForeignKey(e => e.CustomerId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Expense>().HasIndex(e => new { e.WorkspaceId, e.Date });
        // Hooguit één bonnetje per uitgave, en het gaat mee als de uitgave weggaat.
        b.Entity<ExpenseReceipt>().HasOne<Expense>().WithMany().HasForeignKey(r => r.ExpenseId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ExpenseReceipt>().HasIndex(r => r.ExpenseId).IsUnique();
        // Terugkerende facturen: per klant, met hun regels. Elke periode wordt hooguit één factuur, ook als de planner
        // twee keer tegelijk draait of na een herstart opnieuw begint. Gaat de terugkerende factuur weg, dan blijven de facturen staan.
        b.Entity<RecurringInvoice>().HasOne(r => r.Customer).WithMany().HasForeignKey(r => r.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<RecurringInvoice>().HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.RecurringInvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<RecurringInvoice>().HasIndex(r => new { r.Active, r.NextRunDate });
        b.Entity<Invoice>().HasIndex(i => new { i.RecurringInvoiceId, i.RecurringPeriodStart }).IsUnique();
        // Mails en links horen bij een factuur en gaan mee als die weggaat (alleen een concept of voorbeeld kan weg).
        b.Entity<InvoiceMail>().HasOne<Invoice>().WithMany().HasForeignKey(m => m.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<InvoiceMail>().HasIndex(m => m.InvoiceId);
        b.Entity<InvoiceMail>().HasIndex(m => new { m.InvoiceId, m.Step }).IsUnique()
            .HasFilter($"\"Automatic\" = 1 AND \"Status\" IN ('{MailStatus.Sending}', '{MailStatus.Sent}')")
            .HasDatabaseName("IX_InvoiceMails_OneAutomaticPerStep");
        b.Entity<InvoiceLink>().HasOne<Invoice>().WithMany().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<InvoiceLink>().HasIndex(l => l.TokenHash).IsUnique();
        b.Entity<ReminderSettings>().HasIndex(r => r.WorkspaceId).IsUnique();
        FilterByWorkspace(b);
    }

    // Elke tabel met een werkruimte krijgt een filter, zodat een werkruimte nooit gegevens van een ander ziet.
    void FilterByWorkspace(ModelBuilder b)
    {
        Filter<Customer>(b); Filter<Lead>(b); Filter<Invoice>(b); Filter<Appointment>(b); Filter<Project>(b); Filter<TimeEntry>(b);
        Filter<Settings>(b); Filter<CustomModule>(b); Filter<CustomRecord>(b); Filter<Workflow>(b); Filter<Activity>(b);
        Filter<User>(b); Filter<WorkflowRun>(b); Filter<AccountToken>(b); Filter<MolliePayment>(b);
        Filter<Expense>(b); Filter<ExpenseReceipt>(b); Filter<RecurringInvoice>(b);
        Filter<InvoiceMail>(b); Filter<InvoiceLink>(b); Filter<ReminderSettings>(b);
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
