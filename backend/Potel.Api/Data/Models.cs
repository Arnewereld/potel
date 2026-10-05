namespace Potel.Api.Data;

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Company { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class LeadStatus
{
    public static readonly string[] All = ["nieuw", "contact", "offerte", "gewonnen", "verloren"];
}

public class Lead
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Company { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public decimal Value { get; set; }
    public string Status { get; set; } = "nieuw";
    public string? Source { get; set; }
    public string? Notes { get; set; }
    public int? CustomerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class InvoiceStatus
{
    public static readonly string[] All = ["concept", "verzonden", "betaald", "verlopen"];
}

public class Invoice
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public DateTime IssueDate { get; set; } = DateTime.UtcNow.Date;
    public DateTime DueDate { get; set; } = DateTime.UtcNow.Date.AddDays(14);
    public string Status { get; set; } = "concept";
    public string? Notes { get; set; }
    public List<InvoiceLine> Lines { get; set; } = [];
}

public class InvoiceLine
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; } = 21;
}

public static class ProjectStatus
{
    public static readonly string[] All = ["actief", "gepauzeerd", "afgerond"];
}

public static class Billing
{
    // Per uur: uren worden gefactureerd tegen het uurtarief. Vast: één afgesproken prijs voor het hele project.
    public const string Hourly = "uur";
    public const string Fixed = "vast";
    public static readonly string[] All = [Hourly, Fixed];
}

public class Project
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string Status { get; set; } = "actief";
    public string Billing { get; set; } = Data.Billing.Hourly;
    public decimal HourlyRate { get; set; }
    public decimal FixedPrice { get; set; }
    public decimal? BudgetHours { get; set; }
    public string Color { get; set; } = "#ff6d5a";
    public string? RepoUrl { get; set; }
    public string? Description { get; set; }
    public DateTime? Deadline { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TimeEntry
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow.Date;
    public int Minutes { get; set; }
    public string Description { get; set; } = "";
    public bool Billable { get; set; } = true;
    // Gevuld zodra de uren op een factuur staan; daarna liggen ze vast.
    public int? InvoiceId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Eén rij met de gegevens van de freelancer zelf: voor op de factuur en voor de doelen op het dashboard.
public class Settings
{
    public int Id { get; set; }
    public string CompanyName { get; set; } = "Jouw Naam Development";
    public string? OwnerName { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Website { get; set; }
    public string? Kvk { get; set; }
    public string? Btw { get; set; }
    public string? Iban { get; set; }
    public decimal DefaultHourlyRate { get; set; } = 95;
    public int PaymentTermDays { get; set; } = 14;
    public int WeeklyHoursTarget { get; set; } = 32;
    // Het urencriterium voor de zelfstandigenaftrek.
    public int YearlyHoursTarget { get; set; } = 1225;
}

public class Appointment
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Kind { get; set; } = "afspraak";
    public int? CustomerId { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }
    public bool Done { get; set; }
}

// Eigen modules: de gebruiker bepaalt zelf de velden, records worden als JSON opgeslagen.
public class CustomModule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "box";
    public string Color { get; set; } = "#ff6d5a";
    public string FieldsJson { get; set; } = "[]";
}

public class CustomRecord
{
    public int Id { get; set; }
    public int ModuleId { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Workflow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; }
    public string GraphJson { get; set; } = "{\"nodes\":[],\"edges\":[]}";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastScheduledAt { get; set; }
}

public class Activity
{
    public int Id { get; set; }
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
}

public static class Roles
{
    public const string Admin = "beheerder";
    public const string Employee = "medewerker";
    public static readonly string[] All = [Admin, Employee];
}

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Employee;
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}

public class WorkflowRun
{
    public int Id { get; set; }
    public int WorkflowId { get; set; }
    public string Trigger { get; set; } = "";
    // bezig, wachtend, klaar, fout
    public string Status { get; set; } = "bezig";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    // Vroegste moment waarop een wachtend blok verder mag.
    public DateTime? ResumeAt { get; set; }
    public string ContextJson { get; set; } = "{}";
    public string PendingJson { get; set; } = "[]";
    public string LogJson { get; set; } = "[]";
}
