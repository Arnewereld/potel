using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

// Wat je in één schijf van box 1 betaalt: het deel van je inkomen in die schijf en de belasting daarover.
public record BracketTax(decimal From, decimal? UpTo, decimal Rate, decimal Income, decimal Tax);

// De inkomstenbelasting (box 1) en de inkomensafhankelijke bijdrage Zvw over een jaar, stap voor stap. Profit is je winst na
// de investeringsaftrek, TaxableProfit na ondernemersaftrek en MKB-winstvrijstelling. Credits zijn de heffingskortingen die
// echt van je belasting afgaan. Due is wat er na de ingehouden loonheffing overblijft; negatief is: je krijgt geld terug.
public record IncomeTaxEstimate(
    decimal Result, decimal InvestmentDeduction, decimal Profit, bool HoursMet, bool Starter,
    decimal SelfEmployedDeduction, decimal StarterDeduction, decimal SmeExemption, decimal TaxableProfit,
    decimal Wage, decimal TaxableIncome, List<BracketTax> Brackets, decimal RateAdjustment, decimal TaxBeforeCredits,
    decimal LabourIncome, decimal GeneralCredit, decimal LabourCredit, decimal Credits, decimal IncomeTax,
    decimal ZvwBase, decimal Zvw, decimal Total, decimal WageTaxWithheld, decimal Due);

// Rekent de inkomstenbelasting en de bijdrage Zvw uit met de bedragen uit TaxYears, voor wie nog niet de AOW-leeftijd heeft.
// Alles op één plek, zodat het dashboard en het jaaroverzicht dezelfde som maken. Een indicatie om te plannen, geen belastingadvies.
public static class IncomeTax
{
    // Box 1 per schijf, over een inkomen van nul of meer.
    public static List<BracketTax> Brackets(IncomeTaxYear t, decimal income)
    {
        var rows = new List<BracketTax>();
        decimal from = 0;
        foreach (var b in t.Brackets)
        {
            var part = Math.Max(0, Math.Min(income, b.UpTo ?? decimal.MaxValue) - from);
            rows.Add(new BracketTax(from, b.UpTo, b.Rate, part, Money.Round(part * b.Rate / 100)));
            if (b.UpTo is not { } upTo) break;
            from = upTo;
        }
        return rows;
    }

    // De algemene heffingskorting over je verzamelinkomen (hier alleen box 1).
    public static decimal GeneralCredit(IncomeTaxYear t, decimal income) =>
        Math.Max(0, Money.Round(t.GeneralCredit - Math.Max(0, income - t.GeneralCreditPhaseOutFrom) * t.GeneralCreditPhaseOutRate / 100));

    // De arbeidskorting over je arbeidsinkomen: winst vóór ondernemersaftrek en MKB-winstvrijstelling, plus loon.
    public static decimal LabourCredit(IncomeTaxYear t, decimal labourIncome)
    {
        if (labourIncome <= 0) return 0;
        var step = t.LabourCredit.First(s => s.UpTo is not { } upTo || labourIncome <= upTo);
        return Math.Max(0, Money.Round(step.Base + (labourIncome - step.From) * step.Rate / 100));
    }

    // De tariefsaanpassing: over het laagste van je aftrekposten en het deel van je inkomen vóór die aftrekposten boven de grens.
    public static decimal RateAdjustment(IncomeTaxYear t, decimal taxableIncome, decimal deductions)
    {
        if (deductions <= 0) return 0;
        var above = Math.Max(0, taxableIncome + deductions - t.RateAdjustmentFrom);
        return Money.Round(Math.Min(deductions, above) * t.RateAdjustment / 100);
    }

    // De bijdrage Zvw gaat over je belastbare winst, tot het maximum. Over je loon betaalt je werkgever al; dat loon telt mee voor
    // het maximum, dus voor je winst blijft het maximum min je loon over.
    public static decimal ZvwBase(IncomeTaxYear t, decimal taxableProfit, decimal wage) =>
        Math.Max(0, Math.Min(taxableProfit, t.ZvwMaximum - Math.Max(0, wage)));

    // Eerst de ondernemersaftrek zoals het jaaroverzicht die aangeeft (AnnualOverview.Indicate), dan box 1 over winst plus loon,
    // de tariefsaanpassing, de heffingskortingen (nooit meer dan de belasting) en de bijdrage Zvw. De ingehouden loonheffing gaat eraf.
    public static IncomeTaxEstimate Estimate(IncomeTaxYear t, TaxYear deductions, decimal result, decimal kia, bool hoursMet, bool starter,
        decimal wage, decimal withheld)
    {
        var hours = new HoursSummary(hoursMet ? TaxYears.HoursCriterion * 60 : 0, 0, 0, TaxYears.HoursCriterion, hoursMet);
        var ind = AnnualOverview.Indicate(deductions, t.Year, result, hours, starter, kia);
        var profit = result - kia;
        var taxableIncome = ind.TaxableProfit + wage;
        var income = Math.Max(0, taxableIncome);
        var brackets = Brackets(t, income);
        var deducted = Math.Max(0, ind.SelfEmployedDeduction) + Math.Max(0, ind.StarterDeduction) + Math.Max(0, ind.SmeExemption);
        var adjustment = RateAdjustment(t, income, deducted);
        var beforeCredits = brackets.Sum(b => b.Tax) + adjustment;
        var labourIncome = Math.Max(0, profit + wage);
        var general = GeneralCredit(t, income);
        var labour = LabourCredit(t, labourIncome);
        var credits = Math.Min(general + labour, beforeCredits);
        var incomeTax = beforeCredits - credits;
        var zvwBase = ZvwBase(t, ind.TaxableProfit, wage);
        var zvw = Money.Round(zvwBase * t.ZvwRate / 100);
        var total = incomeTax + zvw;
        return new IncomeTaxEstimate(result, kia, profit, hoursMet, starter, ind.SelfEmployedDeduction, ind.StarterDeduction, ind.SmeExemption,
            ind.TaxableProfit, wage, taxableIncome, brackets, adjustment, beforeCredits, labourIncome, general, labour, credits, incomeTax,
            zvwBase, zvw, total, withheld, total - withheld);
    }
}

// Je instellingen voor de belastingpot in een jaar. Saved is false als je voor dat jaar nog niets hebt opgeslagen; dan nemen
// we de manier van reserveren over van het jaar ervoor.
public record TaxReserveSettingsDto(
    int Year, bool Saved, decimal Wage, decimal WageTaxWithheld, bool Starter, string Provisional, decimal ProvisionalAmount, int ProvisionalMonths,
    decimal ProvisionalTotal, string Mode, decimal Percentage, DateTime? UpdatedAt, string? UpdatedBy)
{
    public static TaxReserveSettingsDto From(TaxReserveYear r, bool saved) => new(r.Year, saved, r.Wage, r.WageTaxWithheld, r.Starter, r.Provisional,
        r.ProvisionalAmount, r.ProvisionalMonths, r.ProvisionalTotal, r.Mode, r.Percentage, saved ? r.UpdatedAt : null, saved ? r.UpdatedBy : null);
}

public record TaxReserveSettingsInput(decimal Wage, decimal WageTaxWithheld, bool Starter, string? Provisional, decimal ProvisionalAmount, int ProvisionalMonths,
    string? Mode, decimal Percentage);

// Btw van één kwartaal die nog betaald moet worden. Running is het lopende kwartaal, tot en met vandaag.
public record ReserveVat(int Year, int Quarter, string Label, DateTime DueDate, decimal Amount, bool Running);

public record TaxSourceLink(string Label, string Url);

// De belastingpot: hoeveel je opzij zet. Btw (het lopende kwartaal en het vorige als die aangifte nog niet gedaan is) en een
// schatting van de inkomstenbelasting en de bijdrage Zvw over het jaar. Total is wat je voor het hele jaar reserveert,
// Provisional wat je voorlopige aanslag daarvan al dekt, Remaining wat je nog opzij zet, PerMonth dat over de maanden die nog
// komen (alleen in het lopende jaar). Elapsed is welk deel van het jaar voorbij is.
public record TaxReserveDto(
    int Year, DateTime Today, bool Current, bool Started, decimal Elapsed, int MonthsLeft, TaxReserveSettingsDto Settings,
    bool Kor, List<ReserveVat> Vat, decimal VatTotal,
    string Mode, bool Available, string? Missing,
    decimal ResultSoFar, decimal ResultFullYear, HoursSummary Hours, decimal ExpectedHours, bool HoursMet,
    IncomeTaxEstimate? SoFar, IncomeTaxEstimate? FullYear, decimal PaidSoFar, decimal PaidFullYear,
    decimal SoFarAmount, decimal Total, decimal Provisional, decimal Remaining, decimal? PerMonth,
    List<string> Explanation, List<string> LeftOut, List<TaxSourceLink> Sources);

public static class TaxReserve
{
    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    static string Euro(decimal d) => d.ToString("C", Dutch);
    static string Number(decimal d) => d.ToString("#,0.##", Dutch);

    // De instellingen van een jaar; zonder opgeslagen rij een lege, met de manier van reserveren van het laatste jaar ervoor.
    public static async Task<(TaxReserveYear Row, bool Saved)> SettingsAsync(AppDb db, int year)
    {
        var row = await db.TaxReserveYears.AsNoTracking().FirstOrDefaultAsync(r => r.Year == year);
        if (row is not null) return (row, true);
        var previous = await db.TaxReserveYears.AsNoTracking().Where(r => r.Year < year).OrderByDescending(r => r.Year).FirstOrDefaultAsync();
        return (new TaxReserveYear { Year = year, Mode = previous?.Mode ?? ReserveModes.Calculated, Percentage = previous?.Percentage ?? 0 }, false);
    }

    // Wat er in een jaar op je facturen betaald is, zonder btw, zonder voorbeeldfacturen. Per betaling naar rato van de factuur,
    // zodat deelbetalingen ook meetellen.
    static async Task<decimal> PaidAsync(AppDb db, int year)
    {
        var from = new DateTime(year, 1, 1);
        var to = new DateTime(year, 12, 31);
        var payments = await db.InvoicePayments.AsNoTracking().Where(p => p.Date >= from && p.Date <= to).Select(p => new { p.InvoiceId, p.Amount }).ToListAsync();
        var ids = payments.Select(p => p.InvoiceId).Distinct().ToList();
        var invoices = await db.Invoices.AsNoTracking().Include(i => i.Lines).Where(i => ids.Contains(i.Id) && !i.IsDemo).ToDictionaryAsync(i => i.Id, i => i.Totals);
        return Money.Round(payments.Sum(p => invoices.TryGetValue(p.InvoiceId, out var t) && t.Total != 0 ? p.Amount * t.Subtotal / t.Total : 0));
    }

    public static async Task<TaxReserveDto> CalculateAsync(AppDb db, BusinessClock clock, int year)
    {
        var today = clock.Today;
        var (row, saved) = await SettingsAsync(db, year);
        var current = year == today.Year;
        var started = year <= today.Year;
        // De dagen van het jaar die voorbij zijn, vandaag meegeteld. Een eerder jaar is helemaal voorbij.
        var days = DateTime.IsLeapYear(year) ? 366 : 365;
        var passed = current ? today.DayOfYear : started ? days : 0;
        var elapsed = Math.Round((decimal)passed / days, 4);
        // Naar heel het jaar doorgerekend, naar rato van de dagen.
        decimal Extrapolate(decimal soFar) => passed is > 0 ? soFar * days / passed : 0;
        var monthsLeft = current ? 13 - today.Month : 0;
        var explanation = new List<string>();
        var leftOut = new List<string>();
        var sources = new List<TaxSourceLink>();

        // Btw hoort bij vandaag: het lopende kwartaal en het vorige, zolang die aangifte nog op tijd kan en je hem niet als gedaan
        // hebt gemarkeerd. Een terug te vragen bedrag telt als nul.
        var vat = new List<ReserveVat>();
        var kor = (await SettingsEndpoints.GetAsync(db)).VatRegime == VatRegimes.Kor;
        if (current && !kor)
        {
            if (VatReturn.PendingOn(today) is { } pending && !await db.VatFilings.AnyAsync(f => f.Year == pending.Year && f.Quarter == pending.Quarter))
            {
                var earlier = await VatReturn.CalculateAsync(db, clock, pending.Year, pending.Quarter);
                vat.Add(new ReserveVat(pending.Year, pending.Quarter, pending.Label, pending.DueDate, earlier.Total, false));
            }
            var quarter = VatReturn.QuarterOf(today);
            var running = await VatReturn.CalculateAsync(db, clock, today.Year, quarter);
            vat.Add(new ReserveVat(today.Year, quarter, running.Label, running.DueDate, running.Total, true));
        }
        var vatTotal = vat.Sum(v => Math.Max(0, v.Amount));

        var data = await AnnualOverview.CalculateAsync(db, year, row.Starter);
        var s = data.Summary;
        var inv = s.Investments;
        // Afschrijving, boekwinst en afgeboekte boekwaarde staan al voor het hele jaar vast; omzet en de andere kosten rekenen we
        // naar rato van de dagen door naar het hele jaar.
        var fixedPart = -inv.Depreciation - inv.DisposalLoss + inv.DisposalGain;
        var resultFullYear = current ? Money.Round(Extrapolate(s.Result - fixedPart)) + fixedPart : s.Result;
        var expectedHours = Math.Floor(Extrapolate(s.Hours.Minutes / 60m));
        var hoursMet = s.Hours.Met || expectedHours >= TaxYears.HoursCriterion;

        var mode = ReserveModes.All.Contains(row.Mode) ? row.Mode : ReserveModes.Calculated;
        var available = started;
        string? missing = started ? null : $"{year} is nog niet begonnen. Kies het lopende jaar of een jaar dat al voorbij is.";
        IncomeTaxEstimate? soFar = null, fullYear = null;
        decimal paidSoFar = 0, paidFullYear = 0, soFarAmount = 0, total = 0;

        if (started && mode == ReserveModes.Percentage)
        {
            paidSoFar = await PaidAsync(db, year);
            paidFullYear = current ? Money.Round(Extrapolate(paidSoFar)) : paidSoFar;
            soFarAmount = Money.Round(paidSoFar * row.Percentage / 100);
            total = Money.Round(paidFullYear * row.Percentage / 100);
            explanation.Add($"Je hebt gekozen voor een vast percentage: {Number(row.Percentage)}% van wat er op je facturen betaald is, zonder de btw.");
            explanation.Add(current
                ? $"Dit jaar kreeg je tot nu toe {Euro(paidSoFar)} betaald, dus zet je daarvan {Euro(soFarAmount)} opzij. Doorgerekend naar heel {year} is dat ongeveer {Euro(paidFullYear)} betaald en {Euro(total)} om te reserveren."
                : $"In {year} kreeg je {Euro(paidFullYear)} betaald, dus reserveer je {Euro(total)}.");
            explanation.Add("Met een vast percentage tellen je loon uit loondienst, de schijven, de heffingskortingen en de bijdrage Zvw niet apart mee. Bij Instellingen kun je Potel het ook laten uitrekenen.");
        }
        else if (started)
        {
            var t = TaxYears.IncomeTaxFor(year);
            var d = TaxYears.For(year);
            if (t is null || d is null)
            {
                available = false;
                missing = (year > TaxYears.IncomeTax.Max(x => x.Year)
                        ? $"De schijven, heffingskortingen en de bijdrage Zvw voor {year} staan nog niet op belastingdienst.nl. Daarom rekenen we de inkomstenbelasting voor {year} nog niet uit; gokken doen we niet."
                        : $"Voor {year} rekent Potel de inkomstenbelasting niet uit, want de bedragen van dat jaar zitten er niet in.")
                    + " Bij Instellingen kun je wel een vast percentage van je betaalde facturen kiezen.";
            }
            else
            {
                fullYear = IncomeTax.Estimate(t, d, resultFullYear, inv.Kia, hoursMet, row.Starter, row.Wage, row.WageTaxWithheld);
                if (current)
                    soFar = IncomeTax.Estimate(t, d, s.Result, inv.Kia, hoursMet, row.Starter, Money.Round(row.Wage * passed / days), Money.Round(row.WageTaxWithheld * passed / days));
                soFarAmount = Math.Max(0, (soFar ?? fullYear).Due);
                total = Math.Max(0, fullYear.Due);
                Explain(explanation, sources, t, s, fullYear, current, expectedHours, row);
            }
        }

        var provisional = started ? Money.Round(row.ProvisionalTotal) : 0;
        var remaining = Math.Max(0, total - provisional);
        decimal? perMonth = current && monthsLeft > 0 ? Money.Round(remaining / monthsLeft) : null;
        if (available)
        {
            if (provisional > 0)
                explanation.Add($"Je voorlopige aanslag dekt {Euro(provisional)} van het jaar. Die betaal je al aan de Belastingdienst, dus dat deel hoef je niet zelf opzij te zetten.");
            if (provisional > total)
                explanation.Add("Je voorlopige aanslag is hoger dan deze schatting. Misschien krijg je na je aangifte geld terug. Een voorlopige aanslag kun je in Mijn Belastingdienst laten aanpassen.");
            if (perMonth is not null)
                explanation.Add($"Per maand is wat je nog opzij zet, verdeeld over de {monthsLeft} {(monthsLeft == 1 ? "maand" : "maanden")} die van {year} over zijn, deze maand meegeteld. Heb je al geld opzij gezet voor {year}, dan trek je dat eraf.");
            if (current && passed < 31)
                explanation.Add("Het jaar is net begonnen. Hoe verder in het jaar, hoe beter de schatting.");
        }
        if (current)
            explanation.Insert(0, kor
                ? "Je doet mee met de kleineondernemersregeling (KOR), dus er staat geen btw in je pot."
                : "Btw: het lopende kwartaal tot en met vandaag, en het vorige kwartaal zolang de aangifte daarvan nog op tijd kan en je hem niet als gedaan hebt gemarkeerd op de pagina Btw-aangifte. Een kwartaal waarin je btw terugkrijgt telt als nul.");

        leftOut.Add("Box 2 (aanmerkelijk belang) en box 3 (sparen en beleggen).");
        leftOut.Add("Een fiscale partner, toeslagen en de inkomensafhankelijke combinatiekorting of andere heffingskortingen dan de algemene heffingskorting en de arbeidskorting.");
        leftOut.Add("Je eigen woning (hypotheekrente en eigenwoningforfait), persoonsgebonden aftrek zoals giften en zorgkosten, lijfrente en de oudedagsreserve.");
        leftOut.Add("Andere aftrekposten voor ondernemers, zoals de meewerkaftrek en de energie- of milieu-investeringsaftrek, en verliezen uit eerdere jaren.");
        leftOut.Add("De AOW-leeftijd: we rekenen met de tarieven voor wie nog niet de AOW-leeftijd heeft. Belastingrente en afrondingen van de Belastingdienst zitten er ook niet in.");

        return new TaxReserveDto(year, today, current, started, elapsed, monthsLeft, TaxReserveSettingsDto.From(row, saved),
            kor, vat, vatTotal, mode, available, missing,
            s.Result, resultFullYear, s.Hours, expectedHours, hoursMet,
            soFar, fullYear, paidSoFar, paidFullYear,
            soFarAmount, total, provisional, remaining, perMonth, explanation, leftOut, sources);
    }

    // Hoe de berekende schatting tot stand komt, in gewone woorden, met de bronnen erbij.
    static void Explain(List<string> lines, List<TaxSourceLink> sources, IncomeTaxYear t, AnnualDto s, IncomeTaxEstimate full, bool current,
        decimal expectedHours, TaxReserveYear row)
    {
        lines.Add(current
            ? $"We nemen je resultaat uit het jaaroverzicht tot en met vandaag ({Euro(s.Result)}) en rekenen omzet en kosten naar rato van de dagen door naar heel {t.Year}. De afschrijving staat al voor het hele jaar vast. Zo komen we op ongeveer {Euro(full.Result)}."
            : $"We nemen je resultaat uit het jaaroverzicht van {t.Year}: {Euro(full.Result)}.");
        var hours = Number(Math.Floor(s.Hours.Minutes / 60m));
        lines.Add(s.Hours.Met
            ? $"Je hebt {hours} uur geboekt en haalt het urencriterium van {Number(TaxYears.HoursCriterion)} uur, dus de zelfstandigenaftrek telt mee."
            : full.HoursMet
                ? $"Je hebt {hours} uur geboekt. Doorgerekend naar heel het jaar is dat ongeveer {Number(expectedHours)} uur, dus we verwachten dat je het urencriterium haalt en tellen de zelfstandigenaftrek mee."
                : current
                    ? $"Je hebt {hours} uur geboekt. Doorgerekend naar heel het jaar is dat ongeveer {Number(expectedHours)} uur, minder dan {Number(TaxYears.HoursCriterion)}. Daarom tellen de zelfstandigenaftrek en de startersaftrek niet mee."
                    : $"Je hebt {hours} uur geboekt, minder dan {Number(TaxYears.HoursCriterion)}. Daarom tellen de zelfstandigenaftrek en de startersaftrek niet mee.");
        var deductions = new List<string>();
        if (full.InvestmentDeduction > 0) deductions.Add("de kleinschaligheidsinvesteringsaftrek");
        if (full.HoursMet) deductions.Add("de zelfstandigenaftrek");
        if (full.HoursMet && row.Starter) deductions.Add("de startersaftrek (je hebt aangegeven dat je starter bent)");
        deductions.Add("de MKB-winstvrijstelling");
        lines.Add($"Van je resultaat {(deductions.Count == 1 ? "gaat" : "gaan")} {Join(deductions)} af, zoals in de indicatie hierboven. Wat overblijft is je belastbare winst.");
        lines.Add(row.Wage > 0
            ? $"Je loon uit loondienst ({Euro(row.Wage)}) komt erbij. Over het totaal rekenen we de schijven van box 1 voor {t.Year}, en daar gaan de algemene heffingskorting en de arbeidskorting af. Die kortingen zijn nooit hoger dan de belasting."
            : $"Over je belastbare winst rekenen we de schijven van box 1 voor {t.Year}, en daar gaan de algemene heffingskorting en de arbeidskorting af. Die kortingen zijn nooit hoger dan de belasting.");
        if (full.RateAdjustment > 0)
            lines.Add($"Je inkomen komt boven {Euro(t.RateAdjustmentFrom)}. Dan leveren je ondernemersaftrek en de MKB-winstvrijstelling minder op: er gaat {Number(t.RateAdjustment)}% bij over het deel boven die grens (tariefsaanpassing).");
        lines.Add($"De inkomensafhankelijke bijdrage Zvw is {Number(t.ZvwRate)}% van je belastbare winst, tot {Euro(t.ZvwMaximum)}"
            + (row.Wage > 0 ? " min je loon, want over je loon betaalt je werkgever al." : "."));
        if (row.WageTaxWithheld > 0)
            lines.Add($"De loonheffing die je werkgever inhoudt ({Euro(row.WageTaxWithheld)}) heb je al betaald; die gaat eraf.");
        if (current)
            lines.Add("Tot nu toe is dezelfde som over je resultaat tot en met vandaag, alsof het jaar nu zou eindigen. Je loon en loonheffing tellen dan naar rato mee.");

        sources.Add(new($"Schijven box 1 {t.Year}", t.BracketsSource));
        sources.Add(new($"Algemene heffingskorting {t.Year}", t.GeneralCreditSource));
        sources.Add(new($"Arbeidskorting {t.Year}", t.LabourCreditSource));
        sources.Add(new("Arbeidsinkomen", TaxYears.LabourIncomeSource));
        sources.Add(new("Heffingskortingen en uitbetalen", TaxYears.CreditsLimitSource));
        sources.Add(new("Tariefsaanpassing", t.RateAdjustmentSource));
        sources.Add(new($"Percentages bijdrage Zvw {t.Year}", t.ZvwSource));
        sources.Add(new("Bijdrage Zvw over je winst", TaxYears.ZvwProfitSource));
        sources.Add(new("Bijdrage Zvw met loon", TaxYears.ZvwWageSource));
        sources.Add(new("Urencriterium", TaxYears.HoursSource));
        if (TaxYears.For(t.Year) is { } d)
            foreach (var url in d.Sources)
                sources.Add(new(url.Contains("mkb", StringComparison.OrdinalIgnoreCase) ? $"MKB-winstvrijstelling {t.Year}" : $"Zelfstandigenaftrek {t.Year}", url));
    }

    // "a", "a en b", "a, b en c".
    static string Join(List<string> items) => items.Count == 1 ? items[0] : $"{string.Join(", ", items[..^1])} en {items[^1]}";

    public static string? Check(TaxReserveSettingsInput i)
    {
        if (i.Wage < 0 || i.WageTaxWithheld < 0 || i.ProvisionalAmount < 0 || i.Percentage < 0) return "Bedragen kunnen niet negatief zijn.";
        if (i.Wage > Money.MaxAmount || i.ProvisionalAmount > Money.MaxAmount) return "Dat bedrag is te groot.";
        if (i.WageTaxWithheld > i.Wage) return "De ingehouden loonheffing kan niet hoger zijn dan je loon.";
        if (!ProvisionalKinds.All.Contains(i.Provisional ?? "")) return "Kies of je een voorlopige aanslag hebt: geen, in één keer of per maand.";
        if (i.Provisional == ProvisionalKinds.Monthly && i.ProvisionalMonths is < 1 or > 12) return "Vul in over hoeveel maanden je de voorlopige aanslag betaalt, van 1 tot en met 12.";
        if (!ReserveModes.All.Contains(i.Mode ?? "")) return "Kies of Potel het uitrekent of dat je een vast percentage reserveert.";
        if (i.Percentage > 100) return "Een percentage kan niet hoger zijn dan 100.";
        if (i.Mode == ReserveModes.Percentage && i.Percentage <= 0) return "Vul in welk percentage van elke betaalde factuur je wilt reserveren.";
        return null;
    }
}

public static class TaxReserveEndpoints
{
    // De belastingpot laat ook je loon en je voorlopige aanslag zien: net als de bank alleen voor beheerders. Een boekhouder kijkt mee,
    // maar wijzigt niets.
    public static void MapTaxReserve(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/tax/reserve").AdminOrAccountant();

        // De pot voor een jaar; zonder jaar het lopende jaar.
        g.MapGet("/", async (AppDb db, BusinessClock clock, int? year) =>
        {
            var y = year ?? clock.Today.Year;
            if (y is < 2000 or > 2100) return Results.BadRequest(new { error = "Kies een geldig jaar." });
            return Results.Ok(await TaxReserve.CalculateAsync(db, clock, y));
        }).ForAccountant();

        g.MapGet("/settings", async (AppDb db, BusinessClock clock, int? year) =>
        {
            var y = year ?? clock.Today.Year;
            if (y is < 2000 or > 2100) return Results.BadRequest(new { error = "Kies een geldig jaar." });
            var (row, saved) = await TaxReserve.SettingsAsync(db, y);
            return Results.Ok(TaxReserveSettingsDto.From(row, saved));
        }).ForAccountant();

        g.MapPut("/settings", async (AppDb db, BusinessClock clock, ClaimsPrincipal user, int? year, TaxReserveSettingsInput input) =>
        {
            var y = year ?? clock.Today.Year;
            if (y is < 2000 or > 2100) return Results.BadRequest(new { error = "Kies een geldig jaar." });
            if (TaxReserve.Check(input) is { } error) return Results.BadRequest(new { error });
            await using var tx = await WriteLock.BeginAsync(db);
            var row = await db.TaxReserveYears.FirstOrDefaultAsync(r => r.Year == y);
            if (row is null) db.TaxReserveYears.Add(row = new TaxReserveYear { Year = y });
            row.Wage = Money.Round(input.Wage);
            row.WageTaxWithheld = Money.Round(input.WageTaxWithheld);
            row.Starter = input.Starter;
            row.Provisional = input.Provisional!;
            row.ProvisionalAmount = row.Provisional == ProvisionalKinds.None ? 0 : Money.Round(input.ProvisionalAmount);
            row.ProvisionalMonths = row.Provisional == ProvisionalKinds.Monthly ? input.ProvisionalMonths : 12;
            row.Mode = input.Mode!;
            row.Percentage = Math.Round(input.Percentage, 2, MidpointRounding.AwayFromZero);
            row.UpdatedAt = clock.UtcNow;
            row.UpdatedBy = user.FindFirstValue(ClaimTypes.Name);
            db.Log("instellingen", $"Instellingen voor de belastingpot van {y} bijgewerkt");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(TaxReserveSettingsDto.From(row, true));
        }).AdminOnly();
    }
}
