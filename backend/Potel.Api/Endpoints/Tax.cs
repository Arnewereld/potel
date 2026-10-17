using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

// Eén rubriek van de btw-aangifte. Vat is null bij een rubriek zonder btw-kolom (1e en 3b).
public record VatRubric(string Code, string Title, decimal Base, decimal? Vat);

// Wat er per btw-nummer van een klant in een ander EU-land in de opgaaf ICP hoort.
public record IcpLine(string VatNumber, string Customer, string Country, decimal Amount);

// Een factuur of uitgave die meetelt, zodat je kunt nagaan waar een bedrag vandaan komt.
public record VatSource(string Kind, int Id, DateTime Date, string Name, string? Number, string Rubric, decimal Base, decimal Vat);

// Filed: je hebt deze aangifte op de pagina Btw-aangifte als gedaan gemarkeerd.
public record VatQuarterRef(int Year, int Quarter, string Label, DateTime DueDate, bool Filed = false);

public record VatFiledRequest(int Year, int Quarter, bool Filed);

// ToReview: uitgaven in dit kwartaal die een vaste kost als concept klaarzette en die je nog niet nakeek. Ze tellen al mee.
public record VatReturnDto(
    int Year, int Quarter, string Label, DateTime From, DateTime To, DateTime DueDate,
    string Regime, bool Kor, bool Deduction,
    List<VatRubric> Rubrics, decimal Due, decimal Deductible, decimal Total,
    decimal Revenue, decimal OutsideEu, decimal Exempt, decimal KorRevenue,
    List<IcpLine> Icp, List<VatSource> Sources, bool DemoData, bool IncludesDemo, VatQuarterRef? Pending, DateTime? FiledAt = null, string? FiledBy = null,
    int ToReview = 0);

// Rekent de btw-aangifte per kwartaal uit. Dezelfde som voor de pagina Btw-aangifte en de tegel op het dashboard.
// Bronnen per rubriek staan bij de rubrieken hieronder en op de pagina; dit is hulp bij het invullen, geen belastingadvies.
public static class VatReturn
{
    // Titels zoals de Belastingdienst ze noemt.
    public const string R1a = "Leveringen/diensten belast met hoog tarief";
    public const string R1b = "Leveringen/diensten belast met laag tarief";
    public const string R1c = "Leveringen/diensten belast met overige tarieven, behalve 0%";
    public const string R1e = "Leveringen/diensten belast met 0% of niet bij u belast";
    public const string R3b = "Leveringen naar of diensten in landen binnen de EU";
    public const string R4a = "Leveringen/diensten uit landen buiten de EU";
    public const string R4b = "Leveringen/diensten uit landen binnen de EU";
    public const string R5a = "Verschuldigde btw";
    public const string R5b = "Voorbelasting";

    public static (DateTime From, DateTime To) Period(int year, int quarter)
    {
        var from = new DateTime(year, (quarter - 1) * 3 + 1, 1);
        return (from, from.AddMonths(3).AddDays(-1));
    }

    // Uiterlijk de laatste dag van de maand na het kwartaal, ook als dat in het weekend valt.
    public static DateTime DueDate(int year, int quarter) => Period(year, quarter).From.AddMonths(4).AddDays(-1);

    public static int QuarterOf(DateTime d) => (d.Month - 1) / 3 + 1;

    public static string Label(int year, int quarter) => $"Q{quarter} {year}";

    // Het tijdvak waarin een factuur meetelt. Btw in Nederland hoort bij de factuurdatum. Een dienst aan een ondernemer in een
    // ander EU-land (3b en de opgaaf ICP) geef je aan in het tijdvak waarin je hem leverde: het einde van de leverperiode op
    // de factuur, of de factuurdatum als die er niet op staat. Een creditnota telt op zijn eigen datum.
    public static DateTime PeriodDate(Invoice i) =>
        i.VatRegime == VatRegimes.ReverseCharge && i.CreditForInvoiceId is null ? (i.DeliveryTo ?? i.DeliveryFrom ?? i.IssueDate).Date : i.IssueDate.Date;

    public static async Task<VatReturnDto> CalculateAsync(AppDb db, BusinessClock clock, int year, int quarter, bool includeDemo = false)
    {
        var (from, to) = Period(year, quarter);
        var settings = await SettingsEndpoints.GetAsync(db);
        var kor = settings.VatRegime == VatRegimes.Kor;
        // Met de KOR, of als je omzet vrijgesteld is, trek je de btw op je kosten niet af.
        var deduction = settings.VatRegime is not (VatRegimes.Kor or VatRegimes.Exempt);

        // Ruim genoeg om ook diensten te vinden die in dit kwartaal geleverd zijn en later zijn gefactureerd.
        var invoices = await db.Invoices.AsNoTracking().Include(i => i.Lines).Include(i => i.Customer)
            .Where(i => i.Status != InvoiceStatus.Draft && (includeDemo || !i.IsDemo) && i.IssueDate >= from.AddYears(-1) && i.IssueDate < to.AddYears(1).AddDays(1))
            .AsSplitQuery().ToListAsync();
        invoices = invoices.Where(i => PeriodDate(i) >= from && PeriodDate(i) <= to).OrderBy(i => i.IssueDate).ThenBy(i => i.Id).ToList();
        var expenses = await db.Expenses.AsNoTracking()
            .Where(e => e.Date >= from && e.Date <= to && (includeDemo || !e.IsDemo)).OrderBy(e => e.Date).ThenBy(e => e.Id).ToListAsync();

        var bases = new Dictionary<string, decimal>();
        var vats = new Dictionary<string, decimal>();
        void Add(string rubric, decimal b, decimal v)
        {
            bases[rubric] = bases.GetValueOrDefault(rubric) + b;
            vats[rubric] = vats.GetValueOrDefault(rubric) + v;
        }

        var sources = new List<VatSource>();
        decimal outsideEu = 0, exempt = 0, korRevenue = 0;
        var icp = new Dictionary<string, (string Customer, string Country, decimal Amount)>();
        foreach (var inv in invoices)
        {
            var totals = inv.Totals;
            var name = inv.Buyer?.Name ?? (string.IsNullOrWhiteSpace(inv.Customer?.Company) ? inv.Customer?.Name : inv.Customer.Company) ?? "";
            void Source(string rubric, decimal b, decimal v) =>
                sources.Add(new VatSource("factuur", inv.Id, PeriodDate(inv), name, inv.Number, rubric, b, v));
            switch (inv.VatRegime)
            {
                case VatRegimes.Normal:
                    // Per tarief, met de btw zoals hij op de factuur staat (afgerond per tarief).
                    foreach (var g in totals.VatGroups)
                    {
                        var rubric = g.Rate switch { 21 => "1a", 9 => "1b", 0 => "1e", _ => "1c" };
                        Add(rubric, g.Base, rubric == "1e" ? 0 : g.Vat);
                        Source(rubric, g.Base, g.Vat);
                    }
                    break;
                case VatRegimes.ReverseCharge:
                    Add("3b", totals.Subtotal, 0);
                    Source("3b", totals.Subtotal, 0);
                    var vatNumber = InvoiceParty.NormalizeVatId(inv.Buyer?.VatNumber ?? inv.Customer?.VatNumber) ?? "onbekend";
                    var entry = icp.TryGetValue(vatNumber, out var known) ? known : (Customer: name, Country: Countries.Normalize(inv.Buyer?.Country ?? inv.Customer?.Country), Amount: 0m);
                    icp[vatNumber] = (entry.Customer, entry.Country, entry.Amount + totals.Subtotal);
                    break;
                case VatRegimes.OutsideEu:
                    outsideEu += totals.Subtotal;
                    Source("buiten-eu", totals.Subtotal, 0);
                    break;
                case VatRegimes.Exempt:
                    exempt += totals.Subtotal;
                    Source("vrijgesteld", totals.Subtotal, 0);
                    break;
                default:
                    korRevenue += totals.Subtotal;
                    Source("kor", totals.Subtotal, 0);
                    break;
            }
        }

        // Kosten op de datum van de uitgave. Verlegde btw geef je zelf aan (4a of 4b) en trek je als voorbelasting weer af.
        decimal deductible = 0;
        foreach (var e in expenses)
        {
            var rubric = e.ReverseCharge ? (e.SupplierRegion == SupplierRegions.OutsideEu ? "4a" : "4b") : "5b";
            if (e.ReverseCharge) Add(rubric, e.Amount, e.VatAmount);
            if (deduction) deductible += e.VatAmount;
            if (e.ReverseCharge || e.VatAmount != 0)
                sources.Add(new VatSource("kosten", e.Id, e.Date, e.Supplier, null, rubric, e.AmountExclVat, e.VatAmount));
        }

        var rubrics = new List<VatRubric>
        {
            new("1a", R1a, bases.GetValueOrDefault("1a"), vats.GetValueOrDefault("1a")),
            new("1b", R1b, bases.GetValueOrDefault("1b"), vats.GetValueOrDefault("1b")),
        };
        // Een ander tarief dan 21%, 9% of 0% hoort bij een gewone dienst niet voor te komen; we tonen het alleen als het er is.
        if (bases.ContainsKey("1c")) rubrics.Add(new("1c", R1c, bases["1c"], vats["1c"]));
        rubrics.Add(new("1e", R1e, bases.GetValueOrDefault("1e"), null));
        rubrics.Add(new("3b", R3b, bases.GetValueOrDefault("3b"), null));
        rubrics.Add(new("4a", R4a, bases.GetValueOrDefault("4a"), vats.GetValueOrDefault("4a")));
        rubrics.Add(new("4b", R4b, bases.GetValueOrDefault("4b"), vats.GetValueOrDefault("4b")));
        var due = rubrics.Sum(r => r.Vat ?? 0);
        rubrics.Add(new("5a", R5a, 0, due));
        rubrics.Add(new("5b", R5b, 0, deductible));

        var revenue = bases.Where(b => b.Key is "1a" or "1b" or "1c" or "1e" or "3b").Sum(b => b.Value) + outsideEu + exempt + korRevenue;
        var demoData = await db.Invoices.AnyAsync(i => i.IsDemo) || await db.Expenses.AnyAsync(e => e.IsDemo);
        var filed = await db.VatFilings.AsNoTracking().FirstOrDefaultAsync(f => f.Year == year && f.Quarter == quarter);
        var pending = kor ? null : PendingOn(clock.Today);
        if (pending is not null && await db.VatFilings.AnyAsync(f => f.Year == pending.Year && f.Quarter == pending.Quarter)) pending = pending with { Filed = true };

        return new VatReturnDto(
            year, quarter, Label(year, quarter), from, to, DueDate(year, quarter), settings.VatRegime, kor, deduction,
            rubrics, due, deductible, due - deductible, revenue, outsideEu, exempt, korRevenue,
            icp.OrderBy(x => x.Value.Country).ThenBy(x => x.Key).Select(x => new IcpLine(x.Key, x.Value.Customer, x.Value.Country, x.Value.Amount)).ToList(),
            sources, demoData, includeDemo && demoData, pending, filed?.FiledAt, filed?.FiledBy, expenses.Count(e => e.NeedsReview));
    }

    // Het vorige kwartaal, zolang de aangifte daarvan nog op tijd kan.
    public static VatQuarterRef? PendingOn(DateTime today)
    {
        var (year, quarter) = QuarterOf(today) == 1 ? (today.Year - 1, 4) : (today.Year, QuarterOf(today) - 1);
        return today.Date <= DueDate(year, quarter) ? new VatQuarterRef(year, quarter, Label(year, quarter), DueDate(year, quarter)) : null;
    }
}

public record CategoryCost(string Category, string Label, decimal Amount, int Count);
public record MonthTotals(string Month, decimal Revenue, decimal Costs);
public record HoursSummary(int Minutes, int BillableMinutes, int NonBillableMinutes, int Criterion, bool Met);

// Een indicatie van de kleinschaligheidsinvesteringsaftrek, de ondernemersaftrek en de MKB-winstvrijstelling, met de bedragen uit TaxYears.
public record TaxIndication(
    bool Available, decimal Profit, bool HoursMet, bool Starter, decimal SelfEmployedDeduction, decimal StarterDeduction,
    decimal ProfitAfterDeductions, decimal? SmePercentage, decimal SmeExemption, decimal TaxableProfit, List<string> Notes, string[] Sources,
    decimal InvestmentDeduction = 0);

// Ritten van het jaar. Alleen zakelijke kilometers met je eigen auto geven aftrek: kilometers maal het bedrag per kilometer van dat jaar.
public record MileageSummary(int Trips, decimal BusinessKm, decimal CompanyCarKm, decimal PrivateKm, decimal? Rate, decimal Deduction, string Source, string? Note);

// Costs is zonder investeringen: die tellen mee via de afschrijving (en afgeboekte boekwaarde) in Investments. Result is
// omzet min kosten plus boekwinst. PossibleInvestments zijn uitgaven voor hardware van € 450 of meer die geen investering zijn.
public record AnnualDto(
    int Year, decimal Revenue, decimal Costs, decimal Result, bool Deduction, List<CategoryCost> CostsByCategory, List<MonthTotals> Months,
    HoursSummary Hours, TaxIndication Indication, int Invoices, int Expenses, int PossibleInvestments, bool DemoData,
    string HoursSource, string DepreciationSource, MileageSummary Mileage, InvestmentSummary Investments, int ImportedInvoices = 0);

// Het jaaroverzicht voor de aangifte inkomstenbelasting: omzet, kosten, resultaat, uren en een indicatie van de aftrekposten.
// Zonder concepten en voorbeelddata. Een indicatie, geen belastingadvies.
public static class AnnualOverview
{
    public record Data(AnnualDto Summary, List<Invoice> Invoices, List<Expense> Expenses, HashSet<int> ReceiptIds, List<TripRow> Trips,
        List<InvestmentDto> Investments, List<ImportedInvoice> Imported);

    public record TripRow(Trip Trip, string? Customer, string? Project, string? InvoiceNumber);

    // De aftrek per maand, zo afgerond dat de maanden samen precies de aftrek van het jaar zijn.
    static decimal[] SplitByMonth(IEnumerable<Trip> trips, decimal? rate)
    {
        var months = new decimal[12];
        if (rate is not { } r) return months;
        var km = 0m;
        var done = 0m;
        for (var m = 1; m <= 12; m++)
        {
            km += trips.Where(t => t.Date.Month == m).Sum(t => t.Kilometres);
            var upTo = Money.Round(km * r);
            months[m - 1] = upTo - done;
            done = upTo;
        }
        return months;
    }

    public static async Task<Data> CalculateAsync(AppDb db, int year, bool starter)
    {
        var from = new DateTime(year, 1, 1);
        var to = new DateTime(year, 12, 31);
        var settings = await SettingsEndpoints.GetAsync(db);
        var deduction = settings.VatRegime is not (VatRegimes.Kor or VatRegimes.Exempt);

        // Omzet op factuurdatum, zonder btw. Creditnota's hebben negatieve regels en trekken dus vanzelf af.
        var invoices = await db.Invoices.AsNoTracking().Include(i => i.Lines).Include(i => i.Customer).Include(i => i.CreditFor)
            .Where(i => i.Status != InvoiceStatus.Draft && !i.IsDemo && i.IssueDate >= from && i.IssueDate <= to)
            .OrderBy(i => i.IssueDate).ThenBy(i => i.Id).AsSplitQuery().ToListAsync();
        // Facturen uit je vorige pakket tellen mee in de omzet, net als je eigen facturen.
        var imported = await db.ImportedInvoices.AsNoTracking().Include(i => i.Customer).Where(i => i.IssueDate >= from && i.IssueDate <= to)
            .OrderBy(i => i.IssueDate).ThenBy(i => i.Id).ToListAsync();
        var expenses = await db.Expenses.AsNoTracking().Where(e => !e.IsDemo && e.Date >= from && e.Date <= to)
            .OrderBy(e => e.Date).ThenBy(e => e.Id).ToListAsync();
        // Alle uren tellen voor het urencriterium, ook die je niet factureert.
        var hours = await db.TimeEntries.AsNoTracking().Where(t => !t.IsDemo && t.Date >= from && t.Date <= to)
            .Select(t => new { t.Minutes, t.Billable }).ToListAsync();
        // Ritten tellen op hun datum, ook als je ze aan een klant doorbelast: wat de klant betaalt is omzet, de aftrek blijft.
        var trips = await db.Trips.AsNoTracking().Where(t => !t.IsDemo && t.Date >= from && t.Date <= to)
            .OrderBy(t => t.Date).ThenBy(t => t.Id).ToListAsync();
        var (businessKm, companyCarKm, privateKm) = TripEndpoints.Totals(trips);
        var kmRate = TaxYears.KilometreRateFor(year);
        var mileageByMonth = SplitByMonth(trips.Where(t => t.Kind == TripKinds.Business), kmRate.Rate);
        var mileage = new MileageSummary(trips.Count, businessKm, companyCarKm, privateKm, kmRate.Rate, mileageByMonth.Sum(), kmRate.Source, kmRate.Note);

        // Een investering telt niet in één keer mee, maar via de afschrijving over de jaren dat je hem gebruikt.
        var plain = expenses.Where(e => !e.Investment).ToList();
        var (assets, investments, assetsByMonth) = await InvestmentEndpoints.YearAsync(db, year, deduction);

        var revenue = invoices.Sum(i => i.Totals.Subtotal) + imported.Sum(i => i.Subtotal);
        var costs = plain.Sum(e => e.CostForProfit(deduction)) + mileage.Deduction + investments.Depreciation + investments.DisposalLoss;
        var result = revenue - costs + investments.DisposalGain;
        var byCategory = ExpenseCategories.All
            .Select(c => new CategoryCost(c, ExpenseCategories.Label(c), plain.Where(e => e.Category == c).Sum(e => e.CostForProfit(deduction)), plain.Count(e => e.Category == c)))
            .Where(c => c.Count > 0).ToList();
        var months = Enumerable.Range(1, 12).Select(m => new MonthTotals($"{year}-{m:00}",
            invoices.Where(i => i.IssueDate.Month == m).Sum(i => i.Totals.Subtotal) + imported.Where(i => i.IssueDate.Month == m).Sum(i => i.Subtotal),
            plain.Where(e => e.Date.Month == m).Sum(e => e.CostForProfit(deduction)) + mileageByMonth[m - 1] + assetsByMonth[m - 1])).ToList();
        var minutes = hours.Sum(h => h.Minutes);
        var billable = hours.Where(h => h.Billable).Sum(h => h.Minutes);
        var hoursSummary = new HoursSummary(minutes, billable, minutes - billable, TaxYears.HoursCriterion, minutes >= TaxYears.HoursCriterion * 60);
        var possibleInvestments = plain.Count(e => e.Category == ExpenseCategories.Hardware && e.CostForProfit(deduction) >= TaxYears.DepreciationThreshold);
        var demoData = await db.Invoices.AnyAsync(i => i.IsDemo) || await db.Expenses.AnyAsync(e => e.IsDemo) || await db.TimeEntries.AnyAsync(t => t.IsDemo)
            || await db.Trips.AnyAsync(t => t.IsDemo);

        var indication = Indicate(TaxYears.For(year), year, result, hoursSummary, starter, investments.Kia);
        // Bij de indicatie alleen wat over dit jaar gaat: de KIA als je iets kocht, en de waarschuwingen.
        if (indication.Available && investments.Invested > 0) indication.Notes.AddRange(investments.KiaNotes);
        if (indication.Available) indication.Notes.AddRange(investments.Warnings);
        var summary = new AnnualDto(year, revenue, costs, result, deduction, byCategory, months, hoursSummary,
            indication, invoices.Count, expenses.Count, possibleInvestments, demoData,
            TaxYears.HoursSource, TaxYears.DepreciationSource, mileage, investments, imported.Count);
        var ids = expenses.Select(e => e.Id).ToList();
        var receiptIds = (await db.ExpenseReceipts.Where(r => ids.Contains(r.ExpenseId)).Select(r => r.ExpenseId).ToListAsync()).ToHashSet();
        var projects = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name);
        var customers = await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => string.IsNullOrWhiteSpace(c.Company) ? c.Name : c.Company);
        var invoiceIds = trips.Where(t => t.InvoiceId != null).Select(t => t.InvoiceId!.Value).Distinct().ToList();
        var numbers = await db.Invoices.Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Number);
        var tripRows = trips.Select(t => new TripRow(t, t.CustomerId is { } c ? customers.GetValueOrDefault(c) : null,
            t.ProjectId is { } p ? projects.GetValueOrDefault(p) : null, t.InvoiceId is { } i ? numbers.GetValueOrDefault(i) ?? "concept" : null)).ToList();
        return new Data(summary, invoices, expenses, receiptIds, tripRows, assets.Where(a => !a.Demo).ToList(), imported);
    }

    // Eerst de kleinschaligheidsinvesteringsaftrek (die verlaagt je winst), dan de zelfstandigenaftrek (met het urencriterium,
    // niet hoger dan die winst behalve voor starters), startersaftrek en daarna de MKB-winstvrijstelling over de winst na
    // ondernemersaftrek; bij verlies maakt die het verlies kleiner.
    public static TaxIndication Indicate(TaxYear? t, int year, decimal profit, HoursSummary hours, bool starter, decimal kia = 0)
    {
        var notes = new List<string>();
        if (t is null)
        {
            notes.Add($"Voor {year} hebben we de bedragen nog niet. Daarom staat hier geen indicatie.");
            return new TaxIndication(false, profit, hours.Met, starter, 0, 0, profit, null, 0, profit, notes, []);
        }
        if (!hours.Met)
            notes.Add($"Je hebt {Thousands(hours.Minutes / 60)} van de {Thousands(TaxYears.HoursCriterion)} uur. Zonder het urencriterium krijg je geen zelfstandigenaftrek en geen startersaftrek.");
        var winst = profit - kia;
        var selfEmployed = hours.Met ? t.SelfEmployedDeduction : 0;
        if (!starter && selfEmployed > Math.Max(0, winst))
        {
            selfEmployed = Math.Max(0, winst);
            notes.Add("De zelfstandigenaftrek is niet hoger dan je winst, behalve als je starter bent.");
        }
        var starterDeduction = hours.Met && starter ? t.StarterDeduction ?? 0 : 0;
        var after = winst - selfEmployed - starterDeduction;
        var sme = t.SmeProfitExemption is { } pct ? Money.Round(after * pct / 100) : 0;
        if (t.Note is not null) notes.Add(t.Note);
        notes.Add("Heb je aan het begin van het jaar de AOW-leeftijd, dan is de zelfstandigenaftrek de helft.");
        notes.Add("Andere aftrekposten, zoals de energie-investeringsaftrek, de milieu-investeringsaftrek en de meewerkaftrek, tellen hier niet mee.");
        return new TaxIndication(true, profit, hours.Met, starter, selfEmployed, starterDeduction, after, t.SmeProfitExemption, sme, after - sme, notes, t.Sources, kia);
    }

    // Eén CSV-bestand voor je boekhouder: de samenvatting, de facturen, de kosten, de investeringen en de ritten. UTF-8 met BOM, puntkomma's en
    // Nederlandse bedragen, zodat Excel het meteen goed opent.
    public static byte[] Csv(Data data, string company)
    {
        var s = data.Summary;
        var sb = new System.Text.StringBuilder();
        void Line(params string[] cells) => sb.Append(string.Join(';', cells)).Append("\r\n");

        Line(CsvText($"Jaaroverzicht {s.Year}"), CsvText(company));
        Line(CsvText("Zonder concepten en voorbeelddata. Bedragen in euro."));
        Line();
        Line("Samenvatting");
        Line("Omschrijving", "Bedrag");
        Line("Omzet zonder btw", CsvNumber(s.Revenue));
        Line(s.Deduction ? "Kosten zonder btw" : "Kosten met btw (je trekt geen btw af)", CsvNumber(s.Costs));
        foreach (var c in s.CostsByCategory) Line(CsvText($"Kosten {c.Label.ToLowerInvariant()}"), CsvNumber(c.Amount));
        var inv = s.Investments;
        if (data.Investments.Count > 0)
        {
            Line("Afschrijving bedrijfsmiddelen", CsvNumber(inv.Depreciation));
            if (inv.DisposalLoss > 0) Line("Afgeboekte boekwaarde bij verkoop of wegdoen", CsvNumber(inv.DisposalLoss));
            if (inv.DisposalGain > 0) Line("Boekwinst bij verkoop", CsvNumber(inv.DisposalGain));
            Line(CsvText($"Investeringen in {s.Year} (aanschafprijs)"), CsvNumber(inv.Invested));
            Line("Investeringen die meetellen voor de KIA", CsvNumber(inv.Qualifying));
            Line(inv.KiaKnown ? "Kleinschaligheidsinvesteringsaftrek (indicatie)" : "Kleinschaligheidsinvesteringsaftrek (bedragen nog niet bekend)", CsvNumber(inv.Kia));
            Line(CsvText($"Boekwaarde bedrijfsmiddelen 31-12-{s.Year}"), CsvNumber(inv.BookValue));
        }
        if (s.Mileage.Trips > 0)
        {
            Line("Zakelijke kilometers eigen auto", CsvNumber(s.Mileage.BusinessKm));
            Line(CsvText(s.Mileage.Rate is { } r ? $"Kilometeraftrek eigen auto ({CsvNumber(r)} per km)" : "Kilometeraftrek eigen auto (bedrag per km nog niet bekend)"),
                CsvNumber(s.Mileage.Deduction));
            Line("Zakelijke kilometers auto van de zaak", CsvNumber(s.Mileage.CompanyCarKm));
            Line("Privékilometers", CsvNumber(s.Mileage.PrivateKm));
        }
        Line("Resultaat voor belasting", CsvNumber(s.Result));
        Line("Uren totaal", CsvNumber(s.Hours.Minutes / 60m));
        Line("Uren factureerbaar", CsvNumber(s.Hours.BillableMinutes / 60m));
        Line("Uren niet factureerbaar", CsvNumber(s.Hours.NonBillableMinutes / 60m));
        Line();
        Line("Facturen");
        Line("Nummer", "Datum", "Klant", "Btw-nummer klant", "Land", "Btw-regeling", "Bedrag zonder btw", "Btw", "Totaal", "Status", "Creditnota voor");
        foreach (var i in data.Invoices)
        {
            var t = i.Totals;
            var customer = i.Buyer?.Name ?? (string.IsNullOrWhiteSpace(i.Customer?.Company) ? i.Customer?.Name : i.Customer.Company);
            Line(CsvText(i.Number), CsvDate(i.IssueDate), CsvText(customer), CsvText(i.Buyer?.VatNumber ?? i.Customer?.VatNumber),
                CsvText(Countries.Normalize(i.Buyer?.Country ?? i.Customer?.Country)), CsvText(i.VatRegime),
                CsvNumber(t.Subtotal), CsvNumber(t.Vat), CsvNumber(t.Total), CsvText(i.Status), CsvText(i.CreditFor?.Number));
        }
        if (data.Imported.Count > 0)
        {
            Line();
            Line(CsvText("Geïmporteerde facturen (uit je vorige pakket, tellen mee in de omzet)"));
            Line("Nummer", "Datum", "Klant", "Bedrag zonder btw", "Btw", "Totaal", "Betaald op");
            foreach (var i in data.Imported)
                Line(CsvText(i.Number), CsvDate(i.IssueDate), CsvText(string.IsNullOrWhiteSpace(i.Customer?.Company) ? i.Customer?.Name : i.Customer.Company),
                    CsvNumber(i.Subtotal), CsvNumber(i.Vat), CsvNumber(i.Total), i.PaidAt is { } paid ? CsvDate(paid) : "");
        }
        Line();
        Line("Kosten");
        Line("Datum", "Leverancier", "Omschrijving", "Categorie", "Bedrag", "Btw-tarief", "Btw", "Bedrag zonder btw", "Btw verlegd", "Leverancier zit", "Bonnetje",
            "Investering (telt mee via de afschrijving)");
        foreach (var e in data.Expenses)
            Line(CsvDate(e.Date), CsvText(e.Supplier), CsvText(e.Description), CsvText(ExpenseCategories.Label(e.Category)), CsvNumber(e.Amount),
                e.VatRate is { } r ? CsvNumber(r) : "geen", CsvNumber(e.VatAmount), CsvNumber(e.AmountExclVat), e.ReverseCharge ? "ja" : "nee",
                e.SupplierRegion == SupplierRegions.Eu ? "EU" : e.SupplierRegion == SupplierRegions.OutsideEu ? "buiten de EU" : "",
                data.ReceiptIds.Contains(e.Id) ? "ja" : "nee", e.Investment ? "ja" : "nee");
        if (data.Investments.Count > 0)
        {
            Line();
            Line("Investeringen");
            Line("Aankoopdatum", "Leverancier", "Omschrijving", "Aanschafprijs", "Restwaarde", "Gebruiksduur in jaren", CsvText($"Afschrijving {s.Year}"),
                CsvText($"Boekwaarde 31-12-{s.Year}"), "Verkocht of weggedaan op", "Verkoopprijs zonder btw", "Boekwinst of -verlies", CsvText($"Telt mee voor de KIA {s.Year}"));
            foreach (var a in data.Investments)
                Line(CsvDate(a.Date), CsvText(a.Supplier), CsvText(a.Description), CsvNumber(a.Cost), CsvNumber(a.Residual),
                    a.Years.ToString(System.Globalization.CultureInfo.InvariantCulture), CsvNumber(a.Depreciation), CsvNumber(a.BookValue),
                    a.DisposedOn is { } d ? CsvDate(d) : "", a.DisposedOn is null ? "" : CsvNumber(a.DisposalPrice ?? 0),
                    a.DisposalResult is { } result ? CsvNumber(result) : "", a.Date.Year != s.Year ? "" : a.CountsForKia ? "ja" : "nee");
        }
        if (data.Trips.Count > 0)
        {
            Line();
            Line("Ritten");
            Line("Datum", "Van", "Naar", "Doel", "Soort", "Kilometers", "Kilometerstand begin", "Kilometerstand eind", "Klant", "Project", "Doorbelast op factuur");
            foreach (var (t, customer, project, number) in data.Trips)
                Line(CsvDate(t.Date), CsvText(t.From), CsvText(t.To), CsvText(t.Purpose), CsvText(TripKinds.Label(t.Kind)), CsvNumber(t.Kilometres),
                    t.OdometerStart?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                    t.OdometerEnd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                    CsvText(customer), CsvText(project), CsvText(number));
        }

        return [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(sb.ToString())];
    }

    // Tekst in een cel. Begint hij met een teken waarmee een spreadsheet een formule start, dan zetten we er een apostrof voor.
    // Met een ander scheidingsteken (een komma in een Engelse CSV) gaat een cel met dat teken tussen aanhalingstekens.
    public static string CsvText(string? s, char separator = ';')
    {
        s ??= "";
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return s.IndexOfAny([separator, ';', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }

    // Bedragen met een komma en zonder duizendtallen, los van de taalinstellingen van de server.
    public static string CsvNumber(decimal d) => Money.Round(d).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');

    // 1225 als "1.225", los van de taalinstellingen van de server.
    static string Thousands(int n) => n.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', '.');

    public static string CsvDate(DateTime d) => d.ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture);
}

public static class TaxEndpoints
{
    public static void MapTax(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/tax");

        // De btw-aangifte van een kwartaal; zonder jaar en kwartaal het lopende kwartaal.
        g.MapGet("/vat-return", async (AppDb db, BusinessClock clock, int? year, int? quarter, bool? demo) =>
        {
            var today = clock.Today;
            var y = year ?? today.Year;
            var q = quarter ?? VatReturn.QuarterOf(today);
            if (y is < 2000 or > 2100 || q is < 1 or > 4) return Results.BadRequest(new { error = "Kies een kwartaal van 1 tot en met 4 en een geldig jaar." });
            return Results.Ok(await VatReturn.CalculateAsync(db, clock, y, q, demo == true));
        }).ForAccountant();

        // Een aangifte als gedaan markeren (of dat terugdraaien), zodat hij niet meer in de belastingpot telt. Alleen voor een
        // kwartaal dat voorbij is, en alleen een beheerder of een boekhouder die dat van de beheerder mag.
        g.MapPut("/vat-return/filed", async (AppDb db, BusinessClock clock, ClaimsPrincipal user, VatFiledRequest req) =>
        {
            if (req.Year is < 2000 or > 2100 || req.Quarter is < 1 or > 4) return Results.BadRequest(new { error = "Kies een kwartaal van 1 tot en met 4 en een geldig jaar." });
            if (VatReturn.Period(req.Year, req.Quarter).To >= clock.Today)
                return Results.BadRequest(new { error = "Dit kwartaal is nog niet voorbij. De aangifte doe je pas na het einde van het kwartaal." });
            await using var tx = await WriteLock.BeginAsync(db);
            var row = await db.VatFilings.FirstOrDefaultAsync(f => f.Year == req.Year && f.Quarter == req.Quarter);
            var label = VatReturn.Label(req.Year, req.Quarter);
            if (req.Filed && row is null)
            {
                db.VatFilings.Add(row = new VatFiling { Year = req.Year, Quarter = req.Quarter, FiledAt = clock.UtcNow, FiledBy = user.FindFirstValue(ClaimTypes.Name) });
                db.Log("btw", $"Btw-aangifte over {label} gemarkeerd als gedaan");
            }
            else if (!req.Filed && row is not null)
            {
                db.VatFilings.Remove(row);
                row = null;
                db.Log("btw", $"Btw-aangifte over {label} weer op niet gedaan gezet");
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(new { year = req.Year, quarter = req.Quarter, filedAt = row?.FiledAt, filedBy = row?.FiledBy });
        }).AdminOrAccountant().ForAccountant(AccountantPermissions.VatFiling);

        // Het jaaroverzicht; zonder jaar het lopende jaar. Starter geeft de startersaftrek mee in de indicatie; zonder starter
        // telt wat een beheerder bij de belastingpot voor dat jaar heeft ingesteld.
        g.MapGet("/annual", async (AppDb db, BusinessClock clock, int? year, bool? starter) =>
        {
            var y = year ?? clock.Today.Year;
            if (y is < 2000 or > 2100) return Results.BadRequest(new { error = "Kies een geldig jaar." });
            var isStarter = starter ?? (await TaxReserve.SettingsAsync(db, y)).Row.Starter;
            return Results.Ok((await AnnualOverview.CalculateAsync(db, y, isStarter)).Summary);
        }).ForAccountant();

        // Hetzelfde overzicht als CSV voor je boekhouder, met alle facturen en kosten van dat jaar.
        g.MapGet("/annual.csv", async (AppDb db, BusinessClock clock, HttpContext http, int? year) =>
        {
            var y = year ?? clock.Today.Year;
            if (y is < 2000 or > 2100) return Results.BadRequest(new { error = "Kies een geldig jaar." });
            var data = await AnnualOverview.CalculateAsync(db, y, starter: false);
            var company = (await SettingsEndpoints.GetAsync(db)).CompanyName;
            http.Response.Headers.CacheControl = "private, no-store";
            return Results.File(AnnualOverview.Csv(data, company), "text/csv; charset=utf-8", $"jaaroverzicht-{y}.csv");
        }).AccountantExport(Accountants.DescribeAnnualExport);
    }
}
