using Potel.Api.Data;

namespace Potel.Api.Importing;

// Een veld in Potel waar een kolom uit het bestand naartoe gaat. Names zijn kolomnamen uit bekende exports (Nederlands en Engels),
// in volgorde van voorkeur: de eerste die in het bestand staat, wordt de kolom voor dit veld.
public record ImportField(string Key, string Label, bool Required, string[] Names);

public static class ImportFields
{
    public const string Name = "name", Contact = "contact", Email = "email", Phone = "phone", Address = "address", Postcode = "postcode",
        City = "city", Country = "country", Kvk = "kvk", Vat = "vat", Iban = "iban", Language = "language",
        Customer = "customer", Project = "project", Rate = "rate", Fixed = "fixed",
        Date = "date", Description = "description", Duration = "duration", Billable = "billable",
        Number = "number", Subtotal = "subtotal", VatAmount = "vatAmount", Total = "total", Paid = "paid";

    static readonly string[] CustomerNames = ["klant", "klantnaam", "client", "customer", "opdrachtgever", "relatie", "debiteur", "bedrijf", "bedrijfsnaam", "company"];

    public static readonly Dictionary<string, ImportField[]> ByKind = new()
    {
        [ImportKinds.Customers] =
        [
            new(Name, "Naam van de klant", true, ["bedrijfsnaam", "company name", "company", "bedrijf", "organisatie", "organization", "klantnaam", "klant",
                "client", "customer", "relatie", "naam", "name"]),
            new(Contact, "Contactpersoon", false, ["contactpersoon", "contact person", "naam contactpersoon", "contact", "t.a.v.", "naam", "name"]),
            new(Email, "E-mail", false, ["e-mail", "email", "e-mailadres", "emailadres", "email address", "e-mail address", "mail"]),
            new(Phone, "Telefoon", false, ["telefoon", "telefoonnummer", "phone", "phone number", "tel", "mobiel", "mobile"]),
            new(Address, "Adres", false, ["adres", "address", "straat", "street", "adres 1", "adresregel 1", "address 1", "address line 1", "straat en huisnummer"]),
            new(Postcode, "Postcode", false, ["postcode", "zip", "zip code", "zipcode", "postal code"]),
            new(City, "Plaats", false, ["plaats", "woonplaats", "vestigingsplaats", "stad", "city", "town"]),
            new(Country, "Land", false, ["land", "country", "landcode", "country code"]),
            new(Kvk, "KvK-nummer", false, ["kvk-nummer", "kvk nummer", "kvknummer", "kvk", "kamer van koophandel", "coc number", "coc", "chamber of commerce",
                "chamber of commerce number"]),
            new(Vat, "Btw-nummer", false, ["btw-nummer", "btw nummer", "btwnummer", "btw-id", "btw id", "btw-identificatienummer", "vat number", "vat id", "vat",
                "vat-nummer", "tax number", "tax id"]),
            new(Iban, "IBAN", false, ["iban", "rekeningnummer", "bankrekening", "bank account", "account number"]),
            new(Language, "Taal", false, ["taal", "language", "lang"]),
        ],
        [ImportKinds.Projects] =
        [
            new(Name, "Projectnaam", true, ["projectnaam", "project name", "project", "naam", "name"]),
            new(Customer, "Klant", true, CustomerNames),
            new(Rate, "Uurtarief", false, ["uurtarief", "tarief per uur", "hourly rate", "billable rate", "tarief", "rate"]),
            new(Fixed, "Vaste prijs", false, ["vaste prijs", "fixed price", "fixed fee", "projectprijs", "prijs", "price"]),
        ],
        [ImportKinds.TimeEntries] =
        [
            new(Date, "Datum", true, ["datum", "date", "start date", "startdatum", "dag", "day"]),
            new(Customer, "Klant", false, ["klant", "klantnaam", "client", "customer", "opdrachtgever"]),
            new(Project, "Project", true, ["project", "projectnaam", "project name"]),
            new(Description, "Omschrijving", false, ["omschrijving", "beschrijving", "description", "notes", "notities", "opmerking", "task", "taak"]),
            new(Duration, "Duur", true, ["duur", "duration", "duration (h)", "duration (decimal)", "uren", "hours", "hours rounded", "tijd", "time"]),
            new(Billable, "Factureerbaar", false, ["factureerbaar", "billable", "billable?", "declarabel", "facturabel"]),
            new(Rate, "Uurtarief (voor nieuwe projecten)", false, ["uurtarief", "billable rate", "hourly rate", "tarief", "rate"]),
        ],
        [ImportKinds.Invoices] =
        [
            new(Number, "Factuurnummer", true, ["factuurnummer", "factuurnr", "factuurnr.", "invoice number", "invoice no", "invoice no.", "invoice #", "nummer",
                "number", "nr", "nr.", "factuur", "invoice"]),
            new(Date, "Factuurdatum", true, ["factuurdatum", "datum factuur", "invoice date", "issue date", "datum", "date"]),
            new(Customer, "Klant", true, [.. CustomerNames, "naam", "name"]),
            new(Subtotal, "Bedrag zonder btw", true, ["bedrag excl. btw", "bedrag excl btw", "totaal excl. btw", "excl. btw", "excl btw", "exclusief btw", "subtotaal",
                "subtotal", "netto", "amount excl. vat", "amount excl vat", "net amount", "bedrag", "amount"]),
            new(VatAmount, "Btw", false, ["btw", "btw-bedrag", "btw bedrag", "btw (bedrag)", "vat", "vat amount", "tax", "omzetbelasting"]),
            new(Total, "Totaal met btw", false, ["totaal incl. btw", "bedrag incl. btw", "incl. btw", "inclusief btw", "totaal", "totaalbedrag", "total incl. vat",
                "total", "gross amount"]),
            new(Paid, "Betaald op", false, ["betaald op", "betaaldatum", "datum betaald", "betaald", "paid date", "paid on", "paid at", "payment date", "date paid"]),
        ],
    };

    // Hoeveel kolomnamen van deze soort we kennen; zo vinden we de kopregel.
    public static int Score(string kind, List<string> headers)
    {
        var known = ByKind[kind].SelectMany(f => f.Names).ToHashSet();
        return headers.Select(ImportFile.Key).Distinct().Count(known.Contains);
    }

    // Per veld de kolom uit het bestand, in volgorde van voorkeur, en elke kolom hooguit één keer.
    public static Dictionary<string, string?> Guess(string kind, List<string> headers)
    {
        var used = new HashSet<string>();
        var result = new Dictionary<string, string?>();
        var keys = headers.Select(h => (Header: h, Key: ImportFile.Key(h))).ToList();
        foreach (var f in ByKind[kind])
        {
            string? pick = null;
            foreach (var name in f.Names)
            {
                pick = keys.FirstOrDefault(k => k.Key == name && !used.Contains(k.Header)).Header;
                if (pick is not null) break;
            }
            if (pick is not null) used.Add(pick);
            result[f.Key] = pick;
        }
        return result;
    }

    // Het pakket waar het bestand vandaan komt, als we het aan de kolommen herkennen. Alleen ter informatie in het scherm.
    public static string? Source(string kind, List<string> headers)
    {
        var h = headers.Select(ImportFile.Key).ToHashSet();
        if (kind != ImportKinds.TimeEntries) return null;
        if (h.Contains("start date") && (h.Contains("duration (h)") || h.Contains("duration (decimal)"))) return "Clockify";
        if (h.Contains("start date") && h.Contains("duration") && (h.Contains("user") || h.Contains("member") || h.Contains("email"))) return "Toggl Track";
        if (h.Contains("date") && h.Contains("hours") && h.Contains("billable?")) return "Harvest";
        return null;
    }
}
