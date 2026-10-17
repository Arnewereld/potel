using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Endpoints;

namespace Potel.Api.Importing;

// Wat de wizard bij elke stap meestuurt: het bestand zelf, welke kolom bij welk veld hoort, wat er met dubbele regels moet en welke
// klanten en projecten bij de namen uit het bestand horen. Een id is een bestaande klant of een bestaand project, 0 is: maak hem aan.
public class ImportFileRequest
{
    public string? Kind { get; set; }
    public string? FileName { get; set; }
    public string? Data { get; set; }
    public Dictionary<string, string?>? Mapping { get; set; }
    public string? Duplicates { get; set; }
    public Dictionary<string, int>? Customers { get; set; }
    public Dictionary<string, int>? Projects { get; set; }
}

public static class DuplicateModes
{
    public const string Skip = "overslaan";
    public const string Update = "bijwerken";
}

public record ImportMessage(int Line, string Text);

// Een naam uit het bestand en de klant of het project dat erbij hoort. Match is wat Potel zelf vond, Choice wat je koos.
public record CustomerChoice(string Key, string Name, int Rows, int? Match, int? Choice, bool CanCreate);
public record ProjectChoice(string Key, string Customer, string Name, int Rows, int? Match, int? Choice, bool CanCreate);

// Het resultaat van het controleren. Ok: er zijn geen fouten en alle namen hebben een klant of project, dus importeren kan.
public record ImportCheck(
    string Kind, string FileName, string? Source, int Rows, bool Ok, int ErrorCount, List<ImportMessage> Errors, int DuplicateCount,
    List<ImportMessage> Duplicates, int Create, int Update, int Skip, int NewCustomers, int NewProjects, bool NeedsChoice,
    List<CustomerChoice> Customers, List<ProjectChoice> Projects, List<string> PreviewFields, List<List<string>> Preview,
    List<ImportOption> CustomerOptions, List<ImportOption> ProjectOptions);

public record ImportOption(int Id, string Name, int? CustomerId = null);

public static class Importer
{
    const int MaxMessages = 100;
    static readonly string[] Colors = ["#ff6d5a", "#4ea5ff", "#3ecf8e", "#9b7bff", "#f5b83d", "#ff5ca8", "#2fc6c6"];

    public const string UnknownKind = "Kies wat je wilt importeren: klanten, projecten, uren of facturen.";
    public const string NoFile = "Kies het CSV-bestand dat je wilt importeren.";

    public static string Label(string kind, int n) => kind switch
    {
        ImportKinds.Customers => n == 1 ? "klant" : "klanten",
        ImportKinds.Projects => n == 1 ? "project" : "projecten",
        ImportKinds.TimeEntries => n == 1 ? "boeking" : "boekingen",
        _ => n == 1 ? "factuur" : "facturen",
    };

    static string DisplayName(Customer c) => string.IsNullOrWhiteSpace(c.Company) ? c.Name : c.Company;

    // ---------- Het bestand ----------

    public static (byte[]? Bytes, string? Error) Decode(string? data)
    {
        var d = data?.Trim() ?? "";
        if (d.StartsWith("data:", StringComparison.Ordinal)) d = d[(d.IndexOf(',') + 1)..];
        if (d == "") return (null, NoFile);
        if (d.Length > (ImportFile.MaxBytes + 2) / 3 * 4 + 4) return (null, ImportFile.TooBig);
        try { return (Convert.FromBase64String(d), null); }
        catch (FormatException) { return (null, ImportFile.NotCsv); }
    }

    public static string FileName(string? name)
    {
        var f = Path.GetFileName((name ?? "").Replace('\\', '/')).Trim();
        f = ImportFile.Text(f, 120) ?? "";
        return f == "" ? "bestand.csv" : f;
    }

    public static (ImportTable? Table, string? Error) Read(string kind, string? data)
    {
        var (bytes, error) = Decode(data);
        if (bytes is null) return (null, error);
        return ImportFile.Read(bytes, h => ImportFields.Score(kind, h));
    }

    // ---------- Het plan: wat er bij importeren zou gebeuren ----------

    sealed class CustomerRef
    {
        public int? Id;
        public Customer? New;
        public int Resolve() => Id ?? New!.Id;
    }

    sealed class ProjectRef
    {
        public int? Id;
        public Project? New;
        public CustomerRef? Customer;
        public int Resolve() => Id ?? New!.Id;
    }

    sealed record CustomerRow(int Line, string Name, string? Contact, string? Email, string? Phone, string? Address, string? City, string? Country,
        string? Kvk, string? Vat, string? Iban, string? Language);
    sealed record ProjectRow(int Line, string Name, string Customer, decimal? Rate, decimal? Fixed);
    sealed record EntryRow(int Line, DateTime Date, string Customer, string Project, string Description, int Minutes, bool Billable, decimal? Rate);
    sealed record InvoiceRow(int Line, string Number, DateTime Date, string Customer, decimal Subtotal, decimal Vat, DateTime? Paid);

    sealed class Plan
    {
        public required string Kind;
        public required string FileName;
        public string? Source;
        public int Rows;
        public string Mode = DuplicateModes.Skip;
        public readonly List<ImportMessage> Errors = [];
        public readonly List<ImportMessage> Duplicates = [];
        public int Skip;
        public bool NeedsChoice;
        public readonly List<CustomerChoice> Customers = [];
        public readonly List<ProjectChoice> Projects = [];
        public readonly List<string> PreviewFields = [];
        public readonly List<List<string>> Preview = [];
        public List<ImportOption> CustomerOptions = [];
        public List<ImportOption> ProjectOptions = [];

        // Wat er aangemaakt of bijgewerkt wordt.
        public readonly Dictionary<string, CustomerRef> NewCustomers = [];
        public readonly Dictionary<string, ProjectRef> NewProjects = [];
        public readonly List<CustomerRow> CustomerCreates = [];
        public readonly List<(int Id, CustomerRow Row)> CustomerUpdates = [];
        public readonly List<(ProjectRow Row, CustomerRef Customer)> ProjectCreates = [];
        public readonly List<(int Id, ProjectRow Row)> ProjectUpdates = [];
        public readonly List<(EntryRow Row, ProjectRef Project)> EntryCreates = [];
        public readonly List<(int Id, bool Billable)> EntryUpdates = [];
        public readonly List<(InvoiceRow Row, CustomerRef Customer)> InvoiceCreates = [];
        public readonly List<(int Id, InvoiceRow Row, CustomerRef Customer)> InvoiceUpdates = [];

        public int Create => CustomerCreates.Count + ProjectCreates.Count + EntryCreates.Count + InvoiceCreates.Count;
        public int Update => CustomerUpdates.Count + ProjectUpdates.Count + EntryUpdates.Count + InvoiceUpdates.Count;

        public void Error(int line, string text) => Errors.Add(new(line, text));
        public void Duplicate(int line, string text) => Duplicates.Add(new(line, text));
    }

    // Leest en controleert het bestand zonder iets op te slaan. Een fout in het bestand zelf (geen CSV, te groot, verplichte kolom
    // niet gekozen) komt terug als Error; fouten per regel staan in het plan.
    static async Task<(Plan? Plan, string? Error)> PlanAsync(AppDb db, BusinessClock clock, ImportFileRequest req)
    {
        var kind = req.Kind?.Trim() ?? "";
        if (!ImportKinds.All.Contains(kind)) return (null, UnknownKind);
        var (table, error) = Read(kind, req.Data);
        if (table is null) return (null, error);
        var fields = ImportFields.ByKind[kind];
        var mapping = req.Mapping ?? ImportFields.Guess(kind, table.Headers);
        var columns = new Dictionary<string, int>();
        foreach (var f in fields)
        {
            var header = mapping.GetValueOrDefault(f.Key);
            var index = string.IsNullOrWhiteSpace(header) ? -1 : table.Headers.IndexOf(header);
            if (!string.IsNullOrWhiteSpace(header) && index < 0)
                return (null, $"De kolom \"{ImportFile.Text(header, 60)}\" staat niet in dit bestand. Kies de kolommen opnieuw.");
            if (f.Required && index < 0) return (null, $"Kies in welke kolom {f.Label.ToLowerInvariant()} staat. Dat veld is verplicht.");
            columns[f.Key] = index;
        }
        var plan = new Plan
        {
            Kind = kind, FileName = FileName(req.FileName), Source = ImportFields.Source(kind, table.Headers), Rows = table.Rows.Count,
            Mode = req.Duplicates == DuplicateModes.Update ? DuplicateModes.Update : DuplicateModes.Skip,
        };
        string Cell(ImportRow r, string field) => columns.TryGetValue(field, out var i) && i >= 0 ? r.Cell(i) : "";

        var customers = await db.Customers.AsNoTracking().OrderBy(c => c.Id).ToListAsync();
        plan.CustomerOptions = customers.Select(c => new ImportOption(c.Id, DisplayName(c))).OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        switch (kind)
        {
            case ImportKinds.Customers: PlanCustomers(plan, table, Cell, customers); break;
            case ImportKinds.Projects: await PlanProjectsAsync(db, plan, table, Cell, customers, req); break;
            case ImportKinds.TimeEntries: await PlanEntriesAsync(db, clock, plan, table, Cell, customers, req); break;
            default: await PlanInvoicesAsync(db, clock, plan, table, Cell, customers, req); break;
        }
        return (plan, null);
    }

    // ---------- Klanten ----------

    static void PlanCustomers(Plan plan, ImportTable table, Func<ImportRow, string, string> cell, List<Customer> existing)
    {
        plan.PreviewFields.AddRange(["Regel", "Naam", "Contactpersoon", "E-mail", "Plaats", "KvK", "Btw-nummer", "Taal"]);
        var seen = new Dictionary<string, int>();
        foreach (var r in table.Rows)
        {
            var errors = plan.Errors.Count;
            var name = ImportFile.Text(cell(r, ImportFields.Name), 200);
            if (name is null) { plan.Error(r.Line, "De naam van de klant ontbreekt. Elke klant heeft een naam nodig."); continue; }
            var email = ImportFile.Text(cell(r, ImportFields.Email), 200);
            if (email is not null && !IsEmail(email)) plan.Error(r.Line, $"Het e-mailadres \"{email}\" klopt niet. Verbeter het of laat het leeg.");
            var countryText = ImportFile.Text(cell(r, ImportFields.Country), 60);
            var country = countryText is null ? null : Countries.Normalize(countryText);
            var kvkText = ImportFile.Text(cell(r, ImportFields.Kvk), 40);
            string? kvk = null;
            if (kvkText is not null)
            {
                if (country is null || Countries.IsNetherlands(country))
                {
                    kvk = WorkspaceEndpoints.NormalizeKvk(kvkText);
                    if (kvk is null) plan.Error(r.Line, $"Het KvK-nummer \"{kvkText}\" klopt niet. Een Nederlands KvK-nummer is 8 cijfers.");
                }
                else kvk = new string(kvkText.Where(char.IsLetterOrDigit).Take(20).ToArray());
            }
            var vat = ImportFile.Text(cell(r, ImportFields.Vat), VatNumbers.MaxLength);
            var ibanText = ImportFile.Text(cell(r, ImportFields.Iban), 50);
            var iban = Banking.Ibans.Normalize(ibanText);
            if (ibanText is not null && !Banking.Ibans.IsValid(iban)) plan.Error(r.Line, $"Het rekeningnummer \"{ibanText}\" is geen geldig IBAN. Verbeter het of laat het leeg.");
            var languageText = ImportFile.Text(cell(r, ImportFields.Language), 30);
            var language = languageText is null ? null : LanguageOf(languageText);
            if (languageText is not null && language is null) plan.Error(r.Line, $"De taal \"{languageText}\" kennen we niet. Kies Nederlands of Engels.");
            var postcode = ImportFile.Text(cell(r, ImportFields.Postcode), 20);
            var city = ImportFile.Text(cell(r, ImportFields.City), 160);
            var row = new CustomerRow(r.Line, name, ImportFile.Text(cell(r, ImportFields.Contact), 200), email, ImportFile.Phone(cell(r, ImportFields.Phone), 50),
                ImportFile.Text(cell(r, ImportFields.Address), 200), ImportFile.Text(string.Join(' ', new[] { postcode, city }.Where(x => x is not null)), 200),
                country, kvk, vat, iban, language);

            // Dubbel in het bestand zelf: de eerste telt.
            var nameKey = "n:" + name.ToLowerInvariant();
            var emailKey = email is null ? null : "e:" + email.ToLowerInvariant();
            if (seen.TryGetValue(nameKey, out var first) || (emailKey is not null && seen.TryGetValue(emailKey, out first)))
            {
                plan.Duplicate(r.Line, $"{name} staat ook op regel {first} van dit bestand. Deze regel slaan we over.");
                plan.Skip++;
                continue;
            }
            seen[nameKey] = r.Line;
            if (emailKey is not null) seen[emailKey] = r.Line;

            var match = MatchCustomer(existing, name) ?? (email is null ? null : existing.FirstOrDefault(c => string.Equals(c.Email?.Trim(), email, StringComparison.OrdinalIgnoreCase)));
            var vatCountry = country ?? match?.Country ?? Countries.Netherlands;
            if (VatNumbers.Problem(vat, vatCountry) is { } vatProblem) plan.Error(r.Line, $"{name}: {vatProblem}");
            row = row with { Vat = VatNumbers.Clean(vat, vatCountry) };
            if (plan.Errors.Count > errors) continue;

            if (plan.Preview.Count < 5)
                plan.Preview.Add([r.Line.ToString(), name, row.Contact ?? "", email ?? "", row.City ?? "", kvk ?? "", row.Vat ?? "", (language ?? Languages.Dutch) == Languages.English ? "Engels" : "Nederlands"]);
            if (match is null) plan.CustomerCreates.Add(row);
            else if (plan.Mode == DuplicateModes.Update)
            {
                plan.CustomerUpdates.Add((match.Id, row));
                plan.Duplicate(r.Line, $"{name} bestaat al in Potel. We werken de gegevens bij die in het bestand staan.");
            }
            else
            {
                plan.Duplicate(r.Line, $"{name} bestaat al in Potel. Deze regel slaan we over.");
                plan.Skip++;
            }
        }
    }

    static bool IsEmail(string s) => s.Length <= 200 && !s.Contains(' ') && MailAddress.TryCreate(s, out var a) && a.Address == s && s.Contains('.');

    static string? LanguageOf(string s) => s.Trim().ToLowerInvariant() switch
    {
        "nl" or "nl-nl" or "nl_nl" or "nederlands" or "dutch" or "nld" => Languages.Dutch,
        "en" or "en-gb" or "en-us" or "en_gb" or "en_us" or "engels" or "english" or "eng" => Languages.English,
        _ => null,
    };

    // Een bestaande klant met deze naam: als bedrijf, als naam, of zoals hij in Potel getoond wordt.
    static Customer? MatchCustomer(List<Customer> existing, string name) => name == "" ? null :
        existing.FirstOrDefault(c => string.Equals(DisplayName(c).Trim(), name, StringComparison.OrdinalIgnoreCase))
        ?? existing.FirstOrDefault(c => string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Company?.Trim(), name, StringComparison.OrdinalIgnoreCase));

    // ---------- Klantnamen in projecten, uren en facturen ----------

    // Kiest de klant voor een naam uit het bestand: wat je koos, anders een bestaande klant met die naam. Zonder keuze en zonder
    // gelijke naam is er nog geen klant; dan vraagt de wizard het eerst. Een nieuwe klant per naam maar één keer.
    static CustomerRef? ResolveCustomer(Plan plan, string name, List<Customer> existing, ImportFileRequest req)
    {
        var key = name.ToLowerInvariant();
        if (req.Customers?.TryGetValue(key, out var choice) == true && choice >= 0)
        {
            if (choice > 0) return existing.Any(c => c.Id == choice) ? new CustomerRef { Id = choice } : null;
            if (name != "")
            {
                if (!plan.NewCustomers.TryGetValue(key, out var made))
                    plan.NewCustomers[key] = made = new CustomerRef { New = new Customer { Name = name } };
                return made;
            }
        }
        return MatchCustomer(existing, name) is { } match ? new CustomerRef { Id = match.Id } : null;
    }

    // Zet de klantnamen die een keuze nodig hebben in de lijst voor de wizard.
    static void AddCustomerChoices(Plan plan, Dictionary<string, (string Name, int Rows)> names, List<Customer> existing, ImportFileRequest req)
    {
        foreach (var (key, (name, rows)) in names.OrderBy(n => n.Value.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var match = MatchCustomer(existing, name)?.Id;
            int? choice = req.Customers?.TryGetValue(key, out var c) == true && c >= 0 ? c : null;
            if (choice > 0 && !existing.Any(x => x.Id == choice))
            {
                plan.Error(0, $"De klant die je koos voor \"{(name == "" ? "zonder klant" : name)}\" bestaat niet (meer). Kies opnieuw.");
                choice = null;
            }
            if (choice == 0 && name == "") choice = null;
            if (choice is null && match is null) plan.NeedsChoice = true;
            plan.Customers.Add(new CustomerChoice(key, name, rows, match, choice, name != ""));
        }
    }

    // ---------- Projecten ----------

    static async Task PlanProjectsAsync(AppDb db, Plan plan, ImportTable table, Func<ImportRow, string, string> cell, List<Customer> customers, ImportFileRequest req)
    {
        plan.PreviewFields.AddRange(["Regel", "Project", "Klant", "Uurtarief", "Vaste prijs"]);
        var settings = await SettingsEndpoints.GetAsync(db);
        var projects = await db.Projects.AsNoTracking().ToListAsync();
        var rows = new List<ProjectRow>();
        foreach (var r in table.Rows)
        {
            var errors = plan.Errors.Count;
            var name = ImportFile.Text(cell(r, ImportFields.Name), 200);
            var customer = ImportFile.Text(cell(r, ImportFields.Customer), 200);
            if (name is null) plan.Error(r.Line, "De naam van het project ontbreekt.");
            if (customer is null) plan.Error(r.Line, "De klant van dit project ontbreekt. Elk project hoort bij een klant.");
            var rate = Money(plan, r, cell(r, ImportFields.Rate), "Het uurtarief", false);
            var fixedPrice = Money(plan, r, cell(r, ImportFields.Fixed), "De vaste prijs", false);
            if (plan.Errors.Count > errors) continue;
            rows.Add(new ProjectRow(r.Line, name!, customer!, rate, fixedPrice));
        }

        var names = rows.GroupBy(x => x.Customer.ToLowerInvariant()).ToDictionary(g => g.Key, g => (g.First().Customer, g.Count()));
        AddCustomerChoices(plan, names, customers, req);
        var seen = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            var cref = ResolveCustomer(plan, row.Customer, customers, req);
            if (cref is null) continue;
            var key = row.Customer.ToLowerInvariant() + "\n" + row.Name.ToLowerInvariant();
            if (seen.TryGetValue(key, out var first))
            {
                plan.Duplicate(row.Line, $"Project {row.Name} van {row.Customer} staat ook op regel {first}. Deze regel slaan we over.");
                plan.Skip++;
                continue;
            }
            seen[key] = row.Line;
            if (plan.Preview.Count < 5)
                plan.Preview.Add([row.Line.ToString(), row.Name, row.Customer, row.Rate is { } rr ? Euro(rr) : row.Fixed > 0 ? "" : Euro(settings.DefaultHourlyRate), row.Fixed is { } f && f > 0 ? Euro(f) : ""]);
            var match = cref.Id is { } cid ? projects.FirstOrDefault(p => p.CustomerId == cid && string.Equals(p.Name.Trim(), row.Name, StringComparison.OrdinalIgnoreCase)) : null;
            if (match is null) plan.ProjectCreates.Add((row, cref));
            else if (plan.Mode == DuplicateModes.Update)
            {
                plan.ProjectUpdates.Add((match.Id, row));
                plan.Duplicate(row.Line, $"Project {row.Name} van {row.Customer} bestaat al. We werken het tarief bij als dat in het bestand staat.");
            }
            else
            {
                plan.Duplicate(row.Line, $"Project {row.Name} van {row.Customer} bestaat al in Potel. Deze regel slaan we over.");
                plan.Skip++;
            }
        }
    }

    // ---------- Uren ----------

    static async Task PlanEntriesAsync(AppDb db, BusinessClock clock, Plan plan, ImportTable table, Func<ImportRow, string, string> cell,
        List<Customer> customers, ImportFileRequest req)
    {
        plan.PreviewFields.AddRange(["Regel", "Datum", "Klant", "Project", "Omschrijving", "Duur", "Factureerbaar"]);
        var settings = await SettingsEndpoints.GetAsync(db);
        var projects = await db.Projects.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
        var customerNames = customers.ToDictionary(c => c.Id, DisplayName);
        plan.ProjectOptions = projects.Select(p => new ImportOption(p.Id, $"{p.Name} ({customerNames.GetValueOrDefault(p.CustomerId) ?? "?"})", p.CustomerId))
            .OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var monthFirst = ImportFile.MonthFirst(table.Rows.Select(r => cell(r, ImportFields.Date)));
        var today = clock.Today;
        var rows = new List<EntryRow>();
        foreach (var r in table.Rows)
        {
            var errors = plan.Errors.Count;
            var dateText = cell(r, ImportFields.Date).Trim();
            var date = ImportFile.Date(dateText, monthFirst);
            if (date is null) plan.Error(r.Line, dateText == "" ? "De datum ontbreekt." : $"De datum \"{ImportFile.Text(dateText, 40)}\" kunnen we niet lezen. Gebruik bijvoorbeeld 31-12-2026 of 2026-12-31.");
            else if (date > today) plan.Error(r.Line, $"De datum {date:dd-MM-yyyy} ligt in de toekomst. Importeer alleen uren die je al gewerkt hebt.");
            var durationText = cell(r, ImportFields.Duration).Trim();
            var minutes = ImportFile.Minutes(durationText);
            if (minutes is null) plan.Error(r.Line, durationText == "" ? "De duur ontbreekt." : $"De duur \"{ImportFile.Text(durationText, 20)}\" kunnen we niet lezen. Gebruik bijvoorbeeld 1:30 of 1,5.");
            else if (minutes <= 0) plan.Error(r.Line, "De duur is 0. Een boeking zonder tijd importeren we niet.");
            else if (minutes > 24 * 60) plan.Error(r.Line, "De duur is meer dan 24 uur. Splits deze boeking over meer dagen.");
            var (flagOk, billable) = ImportFile.Flag(cell(r, ImportFields.Billable));
            if (!flagOk) plan.Error(r.Line, $"Bij factureerbaar staat \"{ImportFile.Text(cell(r, ImportFields.Billable), 20)}\". Gebruik ja of nee.");
            var rate = Money(plan, r, cell(r, ImportFields.Rate), "Het uurtarief", false);
            if (plan.Errors.Count > errors) continue;
            rows.Add(new EntryRow(r.Line, date!.Value, ImportFile.Text(cell(r, ImportFields.Customer), 200) ?? "", ImportFile.Text(cell(r, ImportFields.Project), 200) ?? "",
                ImportFile.Text(cell(r, ImportFields.Description), 500) ?? "", minutes!.Value, billable ?? true, rate));
        }

        // Per combinatie van klant en project uit het bestand: een bestaand project, of een nieuw project na jouw bevestiging.
        var pairs = rows.GroupBy(x => x.Customer.ToLowerInvariant() + "\n" + x.Project.ToLowerInvariant()).ToList();
        var needCustomer = new Dictionary<string, (string Name, int Rows)>();
        foreach (var g in pairs)
        {
            var first = g.First();
            var match = MatchProject(projects, customers, first.Customer, first.Project);
            var matchId = match?.Id;
            int? choice = req.Projects?.TryGetValue(g.Key, out var c) == true && c >= 0 ? c : null;
            if (choice > 0 && !projects.Any(p => p.Id == choice))
            {
                plan.Error(0, $"Het project dat je koos voor \"{Pair(first.Customer, first.Project)}\" bestaat niet (meer). Kies opnieuw.");
                choice = null;
            }
            if (choice == 0 && first.Project == "") choice = null;
            if (choice is null && match is null) plan.NeedsChoice = true;
            plan.Projects.Add(new ProjectChoice(g.Key, first.Customer, first.Project, g.Count(), matchId, choice, first.Project != ""));
            // Een nieuw project hoort bij een klant: die moet dan ook bekend zijn.
            if ((choice ?? match?.Id) is null or 0 && first.Project != "")
            {
                var ck = first.Customer.ToLowerInvariant();
                needCustomer[ck] = (first.Customer, needCustomer.GetValueOrDefault(ck).Rows + g.Count());
            }
        }
        plan.Projects.Sort((a, b) => string.Compare(Pair(a.Customer, a.Name), Pair(b.Customer, b.Name), StringComparison.CurrentCultureIgnoreCase));
        AddCustomerChoices(plan, needCustomer, customers, req);
        if (plan.Customers.Any(x => x.Name == "" && x.Choice is null)) plan.NeedsChoice = true;

        // De projecten per combinatie, met bestaande boekingen om dubbele te herkennen.
        var refs = new Dictionary<string, ProjectRef>();
        foreach (var g in pairs)
        {
            var first = g.First();
            var pc = plan.Projects.First(x => x.Key == g.Key);
            var id = pc.Choice ?? pc.Match;
            if (id > 0) { refs[g.Key] = new ProjectRef { Id = id }; continue; }
            if (id is null || first.Project == "") continue;
            var cref = ResolveCustomer(plan, first.Customer, customers, req);
            if (cref is null) continue;
            var rate = g.Select(x => x.Rate).FirstOrDefault(x => x is not null) ?? settings.DefaultHourlyRate;
            var project = new Project
            {
                Name = first.Project, Billing = Billing.Hourly, HourlyRate = rate, Color = Colors[plan.NewProjects.Count % Colors.Length],
            };
            refs[g.Key] = plan.NewProjects[g.Key] = new ProjectRef { New = project, Customer = cref };
        }

        var projectIds = refs.Values.Where(x => x.Id is not null).Select(x => x.Id!.Value).Distinct().ToList();
        var existing = (await db.TimeEntries.AsNoTracking().Where(t => projectIds.Contains(t.ProjectId))
                .Select(t => new { t.Id, t.ProjectId, t.Date, t.Minutes, t.Description, t.Billable, t.InvoiceId }).ToListAsync())
            .GroupBy(t => EntryKey(t.ProjectId, t.Date, t.Minutes, t.Description))
            .ToDictionary(g => g.Key, g => new Queue<(int Id, bool Billable, bool Invoiced)>(g.OrderBy(t => t.Id).Select(t => (t.Id, t.Billable, t.InvoiceId != null))));
        foreach (var row in rows)
        {
            var key = row.Customer.ToLowerInvariant() + "\n" + row.Project.ToLowerInvariant();
            if (!refs.TryGetValue(key, out var pref)) continue;
            if (plan.Preview.Count < 5)
                plan.Preview.Add([row.Line.ToString(), row.Date.ToString("dd-MM-yyyy"), row.Customer, row.Project, row.Description, $"{row.Minutes / 60}:{row.Minutes % 60:00}", row.Billable ? "ja" : "nee"]);
            if (pref.Id is { } pid && existing.TryGetValue(EntryKey(pid, row.Date, row.Minutes, row.Description), out var queue) && queue.Count > 0)
            {
                var (entryId, wasBillable, invoiced) = queue.Dequeue();
                if (plan.Mode == DuplicateModes.Update && wasBillable != row.Billable && !invoiced)
                {
                    plan.EntryUpdates.Add((entryId, row.Billable));
                    plan.Duplicate(row.Line, $"Deze boeking op {row.Date:dd-MM-yyyy} staat al in Potel. We zetten factureerbaar op {(row.Billable ? "ja" : "nee")}.");
                }
                else
                {
                    plan.Duplicate(row.Line, $"Deze boeking op {row.Date:dd-MM-yyyy} ({Pair(row.Customer, row.Project)}) staat al in Potel. Deze regel slaan we over.");
                    plan.Skip++;
                }
                continue;
            }
            plan.EntryCreates.Add((row, pref));
        }
    }

    static string EntryKey(int projectId, DateTime date, int minutes, string? description) =>
        $"{projectId}|{date:yyyy-MM-dd}|{minutes}|{(description ?? "").Trim().ToLowerInvariant()}";

    static string Pair(string customer, string project) =>
        (customer == "" ? "zonder klant" : customer) + " / " + (project == "" ? "zonder project" : project);

    // Een bestaand project met deze naam: bij de klant met die naam, of zonder klant in het bestand als er maar één zo heet.
    static Project? MatchProject(List<Project> projects, List<Customer> customers, string customer, string project)
    {
        if (project == "") return null;
        var named = projects.Where(p => string.Equals(p.Name.Trim(), project, StringComparison.OrdinalIgnoreCase)).ToList();
        if (customer == "") return named.Count == 1 ? named[0] : null;
        var c = MatchCustomer(customers, customer);
        return c is null ? null : named.FirstOrDefault(p => p.CustomerId == c.Id);
    }

    // ---------- Facturen ----------

    static async Task PlanInvoicesAsync(AppDb db, BusinessClock clock, Plan plan, ImportTable table, Func<ImportRow, string, string> cell,
        List<Customer> customers, ImportFileRequest req)
    {
        plan.PreviewFields.AddRange(["Regel", "Nummer", "Datum", "Klant", "Zonder btw", "Btw", "Betaald op"]);
        var monthFirst = ImportFile.MonthFirst(table.Rows.Select(r => cell(r, ImportFields.Date)).Concat(table.Rows.Select(r => cell(r, ImportFields.Paid))));
        var today = clock.Today;
        var rows = new List<InvoiceRow>();
        foreach (var r in table.Rows)
        {
            var errors = plan.Errors.Count;
            var number = ImportFile.Text(cell(r, ImportFields.Number), 40);
            if (number is null) plan.Error(r.Line, "Het factuurnummer ontbreekt.");
            var dateText = cell(r, ImportFields.Date).Trim();
            var date = ImportFile.Date(dateText, monthFirst);
            if (date is null) plan.Error(r.Line, dateText == "" ? "De factuurdatum ontbreekt." : $"De factuurdatum \"{ImportFile.Text(dateText, 40)}\" kunnen we niet lezen. Gebruik bijvoorbeeld 31-12-2026 of 2026-12-31.");
            else if (date > today) plan.Error(r.Line, $"De factuurdatum {date:dd-MM-yyyy} ligt in de toekomst. Importeer alleen facturen die je al verstuurd hebt.");
            var customer = ImportFile.Text(cell(r, ImportFields.Customer), 200);
            if (customer is null) plan.Error(r.Line, "De klant van deze factuur ontbreekt.");
            var subtotal = Money(plan, r, cell(r, ImportFields.Subtotal), "Het bedrag zonder btw", true, allowNegative: true);
            var vat = Money(plan, r, cell(r, ImportFields.VatAmount), "De btw", false, allowNegative: true);
            var total = Money(plan, r, cell(r, ImportFields.Total), "Het totaal", false, allowNegative: true);
            if (subtotal is { } s && vat is null && total is { } t) vat = t - s;
            else if (subtotal is { } s2 && vat is { } v && total is { } t2 && Math.Abs(s2 + v - t2) > 0.02m)
                plan.Error(r.Line, $"Het bedrag zonder btw ({Euro(s2)}) plus de btw ({Euro(v)}) is niet gelijk aan het totaal ({Euro(t2)}).");
            var paidText = cell(r, ImportFields.Paid).Trim();
            DateTime? paid = null;
            if (paidText != "")
            {
                paid = ImportFile.Date(paidText, monthFirst);
                if (paid is null) plan.Error(r.Line, $"De betaaldatum \"{ImportFile.Text(paidText, 40)}\" kunnen we niet lezen. Laat hem leeg als de factuur niet betaald is.");
                else if (paid > today) plan.Error(r.Line, $"De betaaldatum {paid:dd-MM-yyyy} ligt in de toekomst.");
            }
            if (plan.Errors.Count > errors) continue;
            rows.Add(new InvoiceRow(r.Line, number!, date!.Value, customer!, subtotal!.Value, vat ?? 0, paid));
        }

        var names = rows.GroupBy(x => x.Customer.ToLowerInvariant()).ToDictionary(g => g.Key, g => (g.First().Customer, g.Count()));
        AddCustomerChoices(plan, names, customers, req);
        var numbers = rows.Select(x => x.Number.ToLower()).Distinct().ToList();
        var imported = (await db.ImportedInvoices.AsNoTracking().Select(i => new { i.Id, i.Number }).ToListAsync())
            .Where(i => numbers.Contains(i.Number.ToLower())).GroupBy(i => i.Number.ToLower()).ToDictionary(g => g.Key, g => g.First().Id);
        var own = (await db.Invoices.AsNoTracking().Where(i => i.Number != null).Select(i => i.Number!).ToListAsync())
            .Select(n => n.ToLower()).ToHashSet();
        var seen = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            var cref = ResolveCustomer(plan, row.Customer, customers, req);
            if (cref is null) continue;
            var key = row.Number.ToLower();
            if (seen.TryGetValue(key, out var first))
            {
                plan.Duplicate(row.Line, $"Factuur {row.Number} staat ook op regel {first}. Deze regel slaan we over.");
                plan.Skip++;
                continue;
            }
            seen[key] = row.Line;
            if (plan.Preview.Count < 5)
                plan.Preview.Add([row.Line.ToString(), row.Number, row.Date.ToString("dd-MM-yyyy"), row.Customer, Euro(row.Subtotal), Euro(row.Vat), row.Paid?.ToString("dd-MM-yyyy") ?? ""]);
            if (own.Contains(key))
            {
                plan.Duplicate(row.Line, $"Factuur {row.Number} staat al als gewone factuur in Potel. Die blijft zoals hij is; deze regel slaan we over.");
                plan.Skip++;
            }
            else if (imported.TryGetValue(key, out var id))
            {
                if (plan.Mode == DuplicateModes.Update)
                {
                    plan.InvoiceUpdates.Add((id, row, cref));
                    plan.Duplicate(row.Line, $"Factuur {row.Number} is al eerder geïmporteerd. We werken hem bij met de gegevens uit het bestand.");
                }
                else
                {
                    plan.Duplicate(row.Line, $"Factuur {row.Number} is al eerder geïmporteerd. Deze regel slaan we over.");
                    plan.Skip++;
                }
            }
            else plan.InvoiceCreates.Add((row, cref));
        }
    }

    // Een bedrag uit een cel. Leeg is null (of een fout als het verplicht is).
    static decimal? Money(Plan plan, ImportRow r, string text, string what, bool required, bool allowNegative = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (required) plan.Error(r.Line, $"{what} ontbreekt.");
            return null;
        }
        var amount = ImportFile.Amount(text);
        if (amount is null) { plan.Error(r.Line, $"{what} \"{ImportFile.Text(text, 30)}\" kunnen we niet lezen. Gebruik bijvoorbeeld 1250,00 of 1250.00."); return null; }
        if (!allowNegative && amount < 0) { plan.Error(r.Line, $"{what} kan niet negatief zijn."); return null; }
        return amount;
    }

    static readonly System.Globalization.CultureInfo Dutch = System.Globalization.CultureInfo.GetCultureInfo("nl-NL");
    static string Euro(decimal d) => d.ToString("C", Dutch);

    // ---------- Controleren ----------

    public static async Task<(ImportCheck? Check, string? Error)> CheckAsync(AppDb db, BusinessClock clock, ImportFileRequest req)
    {
        var (plan, error) = await PlanAsync(db, clock, req);
        return plan is null ? (null, error) : (Dto(plan), null);
    }

    static ImportCheck Dto(Plan p) => new(
        p.Kind, p.FileName, p.Source, p.Rows, p.Errors.Count == 0 && !p.NeedsChoice && p.Create + p.Update > 0, p.Errors.Count,
        p.Errors.OrderBy(e => e.Line).Take(MaxMessages).ToList(), p.Duplicates.Count, p.Duplicates.OrderBy(e => e.Line).Take(MaxMessages).ToList(),
        p.Create, p.Update, p.Skip, p.NewCustomers.Count, p.NewProjects.Count, p.NeedsChoice, p.Customers, p.Projects, p.PreviewFields, p.Preview,
        p.CustomerOptions, p.ProjectOptions);

    // ---------- Importeren ----------

    public record RunResult(ImportCheck? Refused, ImportBatch? Batch, string? Error);

    // Alles of niets: we controleren opnieuw binnen de schrijfvergrendeling en slaan alles in één transactie op. Is er een fout,
    // een naam zonder keuze of niets te doen, dan gebeurt er niets.
    public static async Task<RunResult> RunAsync(AppDb db, BusinessClock clock, ImportFileRequest req, User me)
    {
        await using var tx = await WriteLock.BeginAsync(db);
        var (plan, error) = await PlanAsync(db, clock, req);
        if (plan is null) return new(null, null, error);
        if (plan.Errors.Count > 0 || plan.NeedsChoice || plan.Create + plan.Update == 0) return new(Dto(plan), null, null);

        var now = clock.UtcNow;
        var batch = new ImportBatch
        {
            Kind = plan.Kind, FileName = plan.FileName, Source = plan.Source, ImportedBy = me.Name, ImportedAt = now,
            Created = plan.Create, Updated = plan.Update, Skipped = plan.Skip,
            CustomersCreated = plan.NewCustomers.Count + plan.CustomerCreates.Count, ProjectsCreated = plan.NewProjects.Count + plan.ProjectCreates.Count,
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync();

        // Eerst de klanten, dan de projecten, dan de rest: elk heeft de id's van de vorige nodig.
        foreach (var c in plan.NewCustomers.Values)
        {
            c.New!.ImportBatchId = batch.Id;
            c.New.CreatedAt = now;
            db.Customers.Add(c.New);
        }
        foreach (var row in plan.CustomerCreates)
        {
            var c = new Customer { ImportBatchId = batch.Id, CreatedAt = now };
            ApplyCustomer(c, row, isNew: true);
            db.Customers.Add(c);
        }
        if (plan.CustomerUpdates.Count > 0)
        {
            var ids = plan.CustomerUpdates.Select(u => u.Id).ToList();
            var tracked = await db.Customers.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id);
            foreach (var (id, row) in plan.CustomerUpdates) ApplyCustomer(tracked[id], row, isNew: false);
        }
        await db.SaveChangesAsync();

        var settings = await SettingsEndpoints.GetAsync(db);
        var color = 0;
        foreach (var (row, cref) in plan.ProjectCreates)
        {
            var fixedPrice = row.Fixed is { } f && f > 0;
            db.Projects.Add(new Project
            {
                Name = row.Name, CustomerId = cref.Resolve(), Billing = fixedPrice ? Billing.Fixed : Billing.Hourly,
                HourlyRate = row.Rate ?? settings.DefaultHourlyRate, FixedPrice = row.Fixed ?? 0, Color = Colors[color++ % Colors.Length],
                ImportBatchId = batch.Id, CreatedAt = now,
            });
        }
        foreach (var p in plan.NewProjects.Values)
        {
            p.New!.CustomerId = p.Customer!.Resolve();
            p.New.ImportBatchId = batch.Id;
            p.New.CreatedAt = now;
            db.Projects.Add(p.New);
        }
        if (plan.ProjectUpdates.Count > 0)
        {
            var ids = plan.ProjectUpdates.Select(u => u.Id).ToList();
            var tracked = await db.Projects.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
            foreach (var (id, row) in plan.ProjectUpdates)
            {
                var p = tracked[id];
                if (row.Fixed is { } f && f > 0) { p.Billing = Billing.Fixed; p.FixedPrice = f; }
                if (row.Rate is { } r) { p.HourlyRate = r; if (row.Fixed is null or 0) p.Billing = Billing.Hourly; }
            }
        }
        await db.SaveChangesAsync();

        foreach (var (row, pref) in plan.EntryCreates)
            db.TimeEntries.Add(new TimeEntry
            {
                ProjectId = pref.Resolve(), Date = row.Date, Minutes = row.Minutes, Description = row.Description, Billable = row.Billable,
                UserId = me.Id, ImportBatchId = batch.Id, CreatedAt = now,
            });
        if (plan.EntryUpdates.Count > 0)
        {
            var ids = plan.EntryUpdates.Select(u => u.Id).ToList();
            var tracked = await db.TimeEntries.Where(t => ids.Contains(t.Id) && t.InvoiceId == null).ToDictionaryAsync(t => t.Id);
            foreach (var (id, billable) in plan.EntryUpdates) if (tracked.TryGetValue(id, out var t)) t.Billable = billable;
        }
        foreach (var (row, cref) in plan.InvoiceCreates)
            db.ImportedInvoices.Add(new ImportedInvoice
            {
                ImportBatchId = batch.Id, Number = row.Number, IssueDate = row.Date, CustomerId = cref.Resolve(), Subtotal = row.Subtotal, Vat = row.Vat,
                PaidAt = row.Paid, Source = plan.Source ?? plan.FileName, CreatedAt = now,
            });
        if (plan.InvoiceUpdates.Count > 0)
        {
            var ids = plan.InvoiceUpdates.Select(u => u.Id).ToList();
            var tracked = await db.ImportedInvoices.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
            foreach (var (id, row, cref) in plan.InvoiceUpdates)
            {
                var i = tracked[id];
                (i.IssueDate, i.CustomerId, i.Subtotal, i.Vat, i.PaidAt) = (row.Date, cref.Resolve(), row.Subtotal, row.Vat, row.Paid);
            }
        }

        var parts = new List<string> { $"{plan.Create} {Label(plan.Kind, plan.Create)} nieuw" };
        if (plan.Update > 0) parts.Add($"{plan.Update} bijgewerkt");
        if (plan.Skip > 0) parts.Add($"{plan.Skip} overgeslagen");
        if (plan.NewCustomers.Count > 0) parts.Add($"{plan.NewCustomers.Count} {Label(ImportKinds.Customers, plan.NewCustomers.Count)} aangemaakt");
        if (plan.NewProjects.Count > 0) parts.Add($"{plan.NewProjects.Count} {Label(ImportKinds.Projects, plan.NewProjects.Count)} aangemaakt");
        db.Log("import", $"{me.Name} importeerde {batch.FileName}{(plan.Source is null ? "" : $" uit {plan.Source}")}: {string.Join(", ", parts)}");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new(null, batch, null);
    }

    // Een nieuwe klant krijgt alles uit het bestand; een bestaande alleen wat er in het bestand staat, zodat een lege cel niets wist.
    static void ApplyCustomer(Customer c, CustomerRow row, bool isNew)
    {
        if (row.Contact is not null)
        {
            c.Name = row.Contact;
            c.Company = row.Name;
        }
        else if (isNew) c.Name = row.Name;
        if (row.Email is not null && !string.Equals(c.Email?.Trim(), row.Email, StringComparison.OrdinalIgnoreCase)) c.Email = row.Email;
        if (row.Phone is not null) c.Phone = row.Phone;
        if (row.Address is not null) c.Address = row.Address;
        if (row.City is not null) c.City = row.City;
        if (row.Country is not null) c.Country = row.Country;
        if (row.Kvk is not null) c.Kvk = row.Kvk;
        if (row.Vat is not null) c.VatNumber = row.Vat;
        if (row.Iban is not null) c.Iban = row.Iban;
        if (row.Language is not null) c.Language = row.Language;
    }

    // ---------- Ongedaan maken ----------

    public static readonly TimeSpan UndoWindow = TimeSpan.FromHours(24);

    public static bool CanUndo(ImportBatch b, DateTime utcNow) => b.UndoneAt is null && utcNow - b.ImportedAt <= UndoWindow;

    public record UndoResult(int Removed, int Kept, List<string> KeptReasons);

    // Haalt precies weg wat deze import aanmaakte. Wat sindsdien gebruikt is, blijft staan: uren op een factuur, een project met andere
    // uren, kosten of ritten, en een klant met facturen, offertes of andere gegevens. Bijwerkingen van bestaande gegevens blijven zoals ze zijn.
    public static async Task<UndoResult> UndoAsync(AppDb db, ImportBatch batch, DateTime utcNow, string by)
    {
        var id = batch.Id;
        var removed = 0;
        var reasons = new List<string>();

        removed += await db.ImportedInvoices.Where(i => i.ImportBatchId == id).ExecuteDeleteAsync();

        var invoicedEntries = await db.TimeEntries.CountAsync(t => t.ImportBatchId == id && t.InvoiceId != null);
        if (invoicedEntries > 0) reasons.Add($"{invoicedEntries} {Label(ImportKinds.TimeEntries, invoicedEntries)} {(invoicedEntries == 1 ? "staat" : "staan")} inmiddels op een factuur");
        removed += await db.TimeEntries.Where(t => t.ImportBatchId == id && t.InvoiceId == null).ExecuteDeleteAsync();

        var projectIds = await db.Projects.Where(p => p.ImportBatchId == id).Select(p => p.Id).ToListAsync();
        var usedProjects = new HashSet<int>();
        usedProjects.UnionWith(await db.TimeEntries.Where(t => projectIds.Contains(t.ProjectId)).Select(t => t.ProjectId).ToListAsync());
        usedProjects.UnionWith(await db.Expenses.Where(e => e.ProjectId != null && projectIds.Contains(e.ProjectId.Value)).Select(e => e.ProjectId!.Value).ToListAsync());
        usedProjects.UnionWith(await db.RecurringExpenses.Where(e => e.ProjectId != null && projectIds.Contains(e.ProjectId.Value)).Select(e => e.ProjectId!.Value).ToListAsync());
        usedProjects.UnionWith(await db.Trips.Where(t => t.ProjectId != null && projectIds.Contains(t.ProjectId.Value)).Select(t => t.ProjectId!.Value).ToListAsync());
        usedProjects.UnionWith(await db.TripRoutes.Where(t => t.ProjectId != null && projectIds.Contains(t.ProjectId.Value)).Select(t => t.ProjectId!.Value).ToListAsync());
        usedProjects.UnionWith(await db.Quotes.Where(q => q.ProjectId != null && projectIds.Contains(q.ProjectId.Value)).Select(q => q.ProjectId!.Value).ToListAsync());
        usedProjects.UnionWith(await db.GitHubCommits.Where(c => projectIds.Contains(c.ProjectId)).Select(c => c.ProjectId).ToListAsync());
        usedProjects.UnionWith(await db.RunningTimers.Where(t => projectIds.Contains(t.ProjectId)).Select(t => t.ProjectId).ToListAsync());
        if (usedProjects.Count > 0) reasons.Add($"{usedProjects.Count} {Label(ImportKinds.Projects, usedProjects.Count)} {(usedProjects.Count == 1 ? "wordt" : "worden")} inmiddels gebruikt");
        var removeProjects = projectIds.Where(p => !usedProjects.Contains(p)).ToList();
        removed += await db.Projects.Where(p => removeProjects.Contains(p.Id)).ExecuteDeleteAsync();

        var customerIds = await db.Customers.Where(c => c.ImportBatchId == id).Select(c => c.Id).ToListAsync();
        var used = new HashSet<int>();
        used.UnionWith(await db.Invoices.Where(i => customerIds.Contains(i.CustomerId)).Select(i => i.CustomerId).ToListAsync());
        used.UnionWith(await db.ImportedInvoices.Where(i => customerIds.Contains(i.CustomerId)).Select(i => i.CustomerId).ToListAsync());
        used.UnionWith(await db.Projects.Where(p => customerIds.Contains(p.CustomerId)).Select(p => p.CustomerId).ToListAsync());
        used.UnionWith(await db.Quotes.Where(q => q.CustomerId != null && customerIds.Contains(q.CustomerId.Value)).Select(q => q.CustomerId!.Value).ToListAsync());
        used.UnionWith(await db.RecurringInvoices.Where(r => customerIds.Contains(r.CustomerId)).Select(r => r.CustomerId).ToListAsync());
        used.UnionWith(await db.Expenses.Where(e => e.CustomerId != null && customerIds.Contains(e.CustomerId.Value)).Select(e => e.CustomerId!.Value).ToListAsync());
        used.UnionWith(await db.RecurringExpenses.Where(e => e.CustomerId != null && customerIds.Contains(e.CustomerId.Value)).Select(e => e.CustomerId!.Value).ToListAsync());
        used.UnionWith(await db.Trips.Where(t => t.CustomerId != null && customerIds.Contains(t.CustomerId.Value)).Select(t => t.CustomerId!.Value).ToListAsync());
        used.UnionWith(await db.TripRoutes.Where(t => t.CustomerId != null && customerIds.Contains(t.CustomerId.Value)).Select(t => t.CustomerId!.Value).ToListAsync());
        used.UnionWith(await db.Appointments.Where(a => a.CustomerId != null && customerIds.Contains(a.CustomerId.Value)).Select(a => a.CustomerId!.Value).ToListAsync());
        used.UnionWith(await db.Leads.Where(l => l.CustomerId != null && customerIds.Contains(l.CustomerId.Value)).Select(l => l.CustomerId!.Value).ToListAsync());
        used.UnionWith(await db.CustomerPortals.Where(p => customerIds.Contains(p.CustomerId)).Select(p => p.CustomerId).ToListAsync());
        used.UnionWith(await db.PortalLinks.Where(p => customerIds.Contains(p.CustomerId)).Select(p => p.CustomerId).ToListAsync());
        used.UnionWith(await db.Workspaces.Where(w => w.BillingCustomerId != null && customerIds.Contains(w.BillingCustomerId.Value)).Select(w => w.BillingCustomerId!.Value).ToListAsync());
        if (used.Count > 0) reasons.Add($"{used.Count} {Label(ImportKinds.Customers, used.Count)} {(used.Count == 1 ? "wordt" : "worden")} inmiddels gebruikt, bijvoorbeeld op een factuur of in een project");
        var removeCustomers = customerIds.Where(c => !used.Contains(c)).ToList();
        removed += await db.Customers.Where(c => removeCustomers.Contains(c.Id)).ExecuteDeleteAsync();

        var kept = invoicedEntries + usedProjects.Count + used.Count;
        batch.UndoneAt = utcNow;
        batch.UndoneBy = by;
        batch.Kept = kept;
        db.Log("import", $"{by} maakte de import van {batch.FileName} ongedaan: {removed} weggehaald" + (kept > 0 ? $", {kept} bleef staan omdat het inmiddels gebruikt wordt" : ""));
        await db.SaveChangesAsync();
        return new UndoResult(removed, kept, reasons);
    }
}
