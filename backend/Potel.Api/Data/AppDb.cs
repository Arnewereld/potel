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
    public DbSet<InvoicePayment> InvoicePayments => Set<InvoicePayment>();
    public DbSet<MollieAccount> MollieAccounts => Set<MollieAccount>();
    public DbSet<OnlinePayment> OnlinePayments => Set<OnlinePayment>();
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
    public DbSet<CustomerPortal> CustomerPortals => Set<CustomerPortal>();
    public DbSet<PortalLink> PortalLinks => Set<PortalLink>();
    public DbSet<GitHubSettings> GitHubSettings => Set<GitHubSettings>();
    public DbSet<GitHubAuthor> GitHubAuthors => Set<GitHubAuthor>();
    public DbSet<GitHubCommit> GitHubCommits => Set<GitHubCommit>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<QuoteLine> QuoteLines => Set<QuoteLine>();
    public DbSet<QuoteLink> QuoteLinks => Set<QuoteLink>();
    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();
    public DbSet<BankImport> BankImports => Set<BankImport>();
    public DbSet<BankCsvMapping> BankCsvMappings => Set<BankCsvMapping>();

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
        // Betalingen horen bij hun factuur. Alleen een concept of voorbeeldfactuur kan weg, en een concept heeft geen betalingen.
        // Een Mollie-betaling of banktransactie levert hooguit één betaling op, ook als de webhook twee keer tegelijk komt.
        b.Entity<Invoice>().HasMany(i => i.Payments).WithOne().HasForeignKey(p => p.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<InvoicePayment>().HasIndex(p => p.MolliePaymentId).IsUnique().HasFilter("\"MolliePaymentId\" IS NOT NULL");
        b.Entity<InvoicePayment>().HasIndex(p => p.BankTransactionId).IsUnique().HasFilter("\"BankTransactionId\" IS NOT NULL");
        // De eigen Mollie: één sleutel per werkruimte. Elke iDEAL-betaling staat er één keer, en hoort bij een factuur.
        b.Entity<MollieAccount>().HasIndex(a => a.WorkspaceId).IsUnique();
        b.Entity<OnlinePayment>().HasIndex(p => p.MollieId).IsUnique();
        b.Entity<OnlinePayment>().HasIndex(p => p.ReturnTokenHash).IsUnique();
        b.Entity<OnlinePayment>().HasOne<Invoice>().WithMany().HasForeignKey(p => p.InvoiceId).OnDelete(DeleteBehavior.Cascade);
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
        // Bewust zonder databasekoppeling naar de portaallink: die zou de tabel met links laten herbouwen. De openbare
        // factuurpagina zoekt de portaallink zelf op, en zonder die link werkt de factuurlink niet.
        b.Entity<InvoiceLink>().HasIndex(l => l.PortalLinkId);
        b.Entity<ReminderSettings>().HasIndex(r => r.WorkspaceId).IsUnique();
        // Het klantportaal: de instellingen en de links horen bij een klant en gaan mee als die klant weggaat.
        b.Entity<CustomerPortal>().HasOne<Customer>().WithMany().HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CustomerPortal>().HasIndex(p => p.CustomerId).IsUnique();
        b.Entity<PortalLink>().HasOne<Customer>().WithMany().HasForeignKey(l => l.CustomerId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PortalLink>().HasIndex(l => l.TokenHash).IsUnique();
        // GitHub: één token per werkruimte, één rij met adressen per gebruiker, en elke commit hooguit één keer per werkruimte.
        b.Entity<GitHubSettings>().HasIndex(g => g.WorkspaceId).IsUnique();
        b.Entity<GitHubAuthor>().HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<GitHubAuthor>().HasIndex(a => a.UserId).IsUnique();
        b.Entity<GitHubCommit>().HasOne<Project>().WithMany().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<GitHubCommit>().HasOne<TimeEntry>().WithMany().HasForeignKey(c => c.TimeEntryId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<GitHubCommit>().HasIndex(c => new { c.WorkspaceId, c.Sha }).IsUnique();
        // Offertes: per werkruimte een eigen nummerreeks, de regels en links gaan mee als een concept weggaat. Een klant met
        // offertes kan niet weg; een lead wel (de offerte houdt dan de vastgelegde gegevens). Een project of factuur die uit
        // een offerte kwam, mag weg; de offerte wijst er dan niet meer naar.
        b.Entity<Quote>().HasIndex(q => new { q.WorkspaceId, q.Number }).IsUnique();
        b.Entity<Quote>().HasMany(q => q.Lines).WithOne().HasForeignKey(l => l.QuoteId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Quote>().HasOne(q => q.Customer).WithMany().HasForeignKey(q => q.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Quote>().HasOne(q => q.Lead).WithMany().HasForeignKey(q => q.LeadId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Quote>().HasOne<Project>().WithMany().HasForeignKey(q => q.ProjectId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Quote>().HasOne<Invoice>().WithMany().HasForeignKey(q => q.DepositInvoiceId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Quote>().HasOne<Invoice>().WithMany().HasForeignKey(q => q.InvoiceId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Quote>().OwnsOne(q => q.Seller);
        b.Entity<Quote>().OwnsOne(q => q.Buyer);
        b.Entity<QuoteLink>().HasOne<Quote>().WithMany().HasForeignKey(l => l.QuoteId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<QuoteLink>().HasIndex(l => l.TokenHash).IsUnique();
        // Bankafschriften: elke regel staat er per werkruimte één keer, ook als je hetzelfde afschrift opnieuw inleest. Een
        // uitgave hoort bij hooguit één regel; gaat de uitgave weg, dan staat de regel weer open. Een import ongedaan maken
        // haalt zijn regels mee weg.
        b.Entity<BankTransaction>().HasIndex(t => new { t.WorkspaceId, t.DedupeKey }).IsUnique();
        b.Entity<BankTransaction>().HasIndex(t => new { t.WorkspaceId, t.Date });
        b.Entity<BankTransaction>().HasOne<BankImport>().WithMany().HasForeignKey(t => t.ImportId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<BankTransaction>().HasOne<Expense>().WithMany().HasForeignKey(t => t.ExpenseId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<BankTransaction>().HasIndex(t => t.ExpenseId).IsUnique().HasFilter("\"ExpenseId\" IS NOT NULL");
        b.Entity<BankCsvMapping>().HasIndex(m => m.WorkspaceId).IsUnique();
        FilterByWorkspace(b);
    }

    // Elke tabel met een werkruimte krijgt een filter, zodat een werkruimte nooit gegevens van een ander ziet.
    void FilterByWorkspace(ModelBuilder b)
    {
        Filter<Customer>(b); Filter<Lead>(b); Filter<Invoice>(b); Filter<Appointment>(b); Filter<Project>(b); Filter<TimeEntry>(b);
        Filter<Settings>(b); Filter<CustomModule>(b); Filter<CustomRecord>(b); Filter<Workflow>(b); Filter<Activity>(b);
        Filter<User>(b); Filter<WorkflowRun>(b); Filter<AccountToken>(b); Filter<MolliePayment>(b);
        Filter<Expense>(b); Filter<ExpenseReceipt>(b); Filter<RecurringInvoice>(b);
        Filter<InvoiceMail>(b); Filter<InvoiceLink>(b); Filter<ReminderSettings>(b); Filter<CustomerPortal>(b); Filter<PortalLink>(b);
        Filter<GitHubSettings>(b); Filter<GitHubAuthor>(b); Filter<GitHubCommit>(b);
        Filter<Quote>(b); Filter<QuoteLink>(b);
        Filter<InvoicePayment>(b); Filter<MollieAccount>(b); Filter<OnlinePayment>(b);
        Filter<BankTransaction>(b); Filter<BankImport>(b); Filter<BankCsvMapping>(b);
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
