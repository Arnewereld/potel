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
}

public class Activity
{
    public int Id { get; set; }
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
}
