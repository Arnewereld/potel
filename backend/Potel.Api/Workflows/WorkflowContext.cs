using System.Globalization;
using System.Text.RegularExpressions;
using Potel.Api.Data;

namespace Potel.Api.Workflows;

// De gegevens die door een werkstroom stromen, als platte sleutels zoals "lead.name".
public static partial class WorkflowContext
{
    static string Money(decimal d) => d.ToString("0.00", CultureInfo.InvariantCulture);

    public static void AddLead(Dictionary<string, string> ctx, Lead l)
    {
        ctx["lead.id"] = l.Id.ToString();
        ctx["lead.name"] = l.Name;
        ctx["lead.company"] = l.Company ?? "";
        ctx["lead.email"] = l.Email ?? "";
        ctx["lead.phone"] = l.Phone ?? "";
        ctx["lead.value"] = Money(l.Value);
        ctx["lead.status"] = l.Status;
        ctx["lead.source"] = l.Source ?? "";
        if (l.CustomerId is { } cid) ctx["customer.id"] = cid.ToString();
    }

    public static void AddCustomer(Dictionary<string, string> ctx, Customer c)
    {
        ctx["customer.id"] = c.Id.ToString();
        ctx["customer.name"] = c.Name;
        ctx["customer.company"] = c.Company ?? "";
        ctx["customer.email"] = c.Email ?? "";
        ctx["customer.phone"] = c.Phone ?? "";
        ctx["customer.city"] = c.City ?? "";
    }

    public static void AddInvoice(Dictionary<string, string> ctx, Invoice i)
    {
        ctx["invoice.id"] = i.Id.ToString();
        ctx["invoice.number"] = i.Number ?? "";
        ctx["invoice.status"] = i.Status;
        ctx["invoice.total"] = Money(Endpoints.InvoiceEndpoints.Total(i));
        ctx["invoice.dueDate"] = i.DueDate.ToString("yyyy-MM-dd");
        if (i.Customer is { } c) AddCustomer(ctx, c);
        else ctx["customer.id"] = i.CustomerId.ToString();
    }

    [GeneratedRegex(@"\{\{\s*([\w.]+)\s*\}\}")]
    private static partial Regex Placeholder();

    // Vervangt {{lead.name}} en dergelijke door de waarde uit de context.
    public static string Render(string template, Dictionary<string, string> ctx) =>
        Placeholder().Replace(template, m => ctx.TryGetValue(m.Groups[1].Value, out var v) ? v : "");

    [GeneratedRegex(@"^-?\d{1,3}(\.\d{3})+(,\d+)?$")]
    private static partial Regex DutchThousands();

    // Leest zowel "5000", "5000.50" als de Nederlandse schrijfwijze "5.000,50".
    public static bool TryNumber(string s, out decimal value)
    {
        s = s.Trim();
        s = DutchThousands().IsMatch(s) ? s.Replace(".", "").Replace(',', '.') : s.Replace(',', '.');
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    public static bool Compare(string left, string op, string right)
    {
        var bothNumbers = TryNumber(left, out var l) & TryNumber(right, out var r);
        return op switch
        {
            ">" => bothNumbers && l > r,
            ">=" => bothNumbers && l >= r,
            "<" => bothNumbers && l < r,
            "<=" => bothNumbers && l <= r,
            "!=" => bothNumbers ? l != r : !string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            "bevat" => left.Contains(right, StringComparison.OrdinalIgnoreCase),
            "leeg" => string.IsNullOrWhiteSpace(left),
            "niet leeg" => !string.IsNullOrWhiteSpace(left),
            _ => bothNumbers ? l == r : string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
        };
    }
}
