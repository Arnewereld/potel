using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

// Wat de frontend meestuurt. Zonder btw-bedrag rekenen we het zelf uit; vatRate null is "geen btw". Een investering krijgt
// een gebruiksduur (standaard 5 jaar) en eventueel een restwaarde.
public record ExpenseInput(
    DateTime Date, string? Supplier, string? Description, string? Category, decimal Amount, decimal? VatRate, decimal? VatAmount,
    bool ReverseCharge, string? SupplierRegion, int? ProjectId, int? CustomerId,
    bool Investment = false, int? UsefulLifeYears = null, decimal? ResidualValue = null, bool PassengerCar = false);

// Een bonnetje als base64 (of als data-URL, zoals de browser hem leest) met de naam van het bestand.
public record ReceiptUpload(string? FileName, string? Data);

public record ReceiptInfo(string FileName, string ContentType, int Size, DateTime UploadedAt);

public record ExpenseCategoryInput(string? Category);

public record ExpenseDto(
    int Id, DateTime Date, string Supplier, string? Description, string Category, decimal Amount, decimal? VatRate, decimal VatAmount,
    decimal AmountExclVat, bool ReverseCharge, string? SupplierRegion, int? ProjectId, string? ProjectName, int? CustomerId, string? CustomerName,
    bool Demo, ReceiptInfo? Receipt, DateTime CreatedAt,
    bool Investment, int? UsefulLifeYears, decimal? ResidualValue, bool PassengerCar, DateTime? DisposedOn, decimal? DisposalPrice,
    int? RecurringExpenseId = null, string? RecurringPeriod = null, bool NeedsReview = false);

// Zakelijke kosten met hun btw en bonnetje. Iedereen in de werkruimte mag kosten toevoegen, bekijken en aanpassen, ook als
// investering markeren; verwijderen, en een bonnetje vervangen of weghalen, mag alleen een beheerder. Bonnetjes moet je bewaren.
public static class ExpenseEndpoints
{
    public const int MaxReceiptBytes = 5 * 1024 * 1024;
    public const string ReceiptTypeError = "Een bonnetje moet een PNG-, JPG- of WebP-afbeelding of een PDF zijn.";
    public const string ReceiptTooLargeError = "Een bonnetje mag maximaal 5 MB zijn.";
    public const string ReceiptAdminError = "Alleen een beheerder kan een bonnetje vervangen of verwijderen. Bonnetjes moet je bewaren voor je administratie.";

    static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole(Roles.Admin);

    // Het soort bestand aan de eerste bytes, niet aan de naam of wat de browser zegt.
    public static (string ContentType, string Extension)? DetectReceipt(byte[] b)
    {
        var span = b.AsSpan();
        if (span.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ("image/png", "png");
        if (span.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return ("image/jpeg", "jpg");
        if (span.Length >= 12 && span[..4].SequenceEqual("RIFF"u8) && span.Slice(8, 4).SequenceEqual("WEBP"u8)) return ("image/webp", "webp");
        if (span.StartsWith("%PDF-"u8)) return ("application/pdf", "pdf");
        return null;
    }

    // Een veilige bestandsnaam: zonder map, zonder rare tekens en met de extensie die bij de inhoud hoort.
    public static string SafeFileName(string? name, string extension, DateTime date)
    {
        var last = (name ?? "").Replace('\\', '/').Split('/')[^1];
        var stem = Path.GetFileNameWithoutExtension(last);
        stem = new string(stem.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').ToArray()).Trim(' ', '.');
        if (stem.Length > 80) stem = stem[..80].Trim(' ', '.');
        if (stem == "") stem = $"bonnetje-{date:yyyy-MM-dd}";
        return $"{stem}.{extension}";
    }

    // Hoeveel ruimte een werkruimte voor bonnetjes heeft: weinig tijdens de proef, ruim met een abonnement.
    static long ReceiptQuota(IConfiguration config, Workspace? w) =>
        (w?.Plan == Plans.Trial ? config.GetValue("Receipts:MaxMegabytesTrial", 100L) : config.GetValue("Receipts:MaxMegabytesPaid", 2048L)) * 1024 * 1024;

    static async Task<List<ExpenseDto>> ListAsync(AppDb db, Func<IQueryable<Expense>, IQueryable<Expense>> filter)
    {
        var expenses = await filter(db.Expenses.AsNoTracking()).OrderByDescending(e => e.Date).ThenByDescending(e => e.Id).ToListAsync();
        var ids = expenses.Select(e => e.Id).ToList();
        // Alleen de gegevens van de bonnetjes, niet de bestanden zelf.
        var receipts = await db.ExpenseReceipts.AsNoTracking().Where(r => ids.Contains(r.ExpenseId))
            .Select(r => new { r.ExpenseId, Info = new ReceiptInfo(r.FileName, r.ContentType, r.Size, r.UploadedAt) })
            .ToDictionaryAsync(r => r.ExpenseId, r => r.Info);
        var projects = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name);
        var customers = await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => string.IsNullOrWhiteSpace(c.Company) ? c.Name : c.Company);
        // De periode van een uitgave uit een vaste kost, zoals "oktober 2026".
        var recurringIds = expenses.Where(e => e.RecurringExpenseId != null).Select(e => e.RecurringExpenseId!.Value).Distinct().ToList();
        var recurring = await db.RecurringExpenses.AsNoTracking().Where(r => recurringIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        string? Period(Expense e) => e.RecurringExpenseId is { } rid && e.RecurringPeriodStart is { } start && recurring.TryGetValue(rid, out var r)
            ? Invoicing.RecurringExpenseService.Label(Invoicing.RecurringPeriods.Period(r, start))
            : null;
        return expenses.Select(e => new ExpenseDto(
            e.Id, e.Date, e.Supplier, e.Description, e.Category, e.Amount, e.VatRate, e.VatAmount, e.AmountExclVat, e.ReverseCharge, e.SupplierRegion,
            e.ProjectId, e.ProjectId is { } p ? projects.GetValueOrDefault(p) : null,
            e.CustomerId, e.CustomerId is { } c ? customers.GetValueOrDefault(c) : null,
            e.IsDemo, receipts.GetValueOrDefault(e.Id), e.CreatedAt,
            e.Investment, e.UsefulLifeYears, e.ResidualValue, e.PassengerCar, e.DisposedOn, e.DisposalPrice,
            e.RecurringExpenseId, Period(e), e.NeedsReview)).ToList();
    }

    static async Task<ExpenseDto> OneAsync(AppDb db, int id) => (await ListAsync(db, q => q.Where(e => e.Id == id))).Single();

    // Controleert de invoer en zet hem op de uitgave. Elke koppeling moet in de eigen werkruimte bestaan. Ook voor een
    // uitgave die je vanuit je bankafschrift maakt.
    public static async Task<string?> ApplyAsync(AppDb db, Expense e, ExpenseInput input)
    {
        if (input.Date == default || input.Date.Year < 2000 || input.Date.Year > 2100) return "Vul de datum van de uitgave in.";
        var supplier = input.Supplier?.Trim() ?? "";
        if (supplier == "") return "Vul de leverancier in.";
        if (supplier.Length > 200) return "De naam van de leverancier is te lang.";
        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        if (description is { Length: > 1000 }) return "De omschrijving is te lang.";
        var category = input.Category?.Trim() ?? "";
        if (!ExpenseCategories.All.Contains(category)) return "Kies een categorie.";
        var amount = Money.Round(input.Amount);
        if (amount == 0) return "Vul het bedrag in.";
        if (Math.Abs(amount) > Money.MaxAmount) return "Dat bedrag is te groot.";
        if (input.VatRate is { } r && !Expense.VatRates.Contains(r)) return "Kies 21%, 9%, 0% of geen btw.";

        // Bij btw verlegd bereken je zelf de Nederlandse btw, dus dan hoort er een tarief bij.
        string? region = null;
        if (input.ReverseCharge)
        {
            if (input.VatRate is not (21 or 9)) return "Bij btw verlegd bereken je zelf de Nederlandse btw. Kies het tarief dat daarbij hoort, meestal 21%.";
            region = input.SupplierRegion?.Trim();
            if (!SupplierRegions.All.Contains(region)) return "Kies waar de leverancier zit: in een ander EU-land of buiten de EU.";
        }

        // Het btw-bedrag mag afwijken van de berekening (bonnetjes ronden soms anders af), maar niet van teken en niet hoger dan het bedrag.
        var vat = input.VatRate is not > 0 ? 0 : Money.Round(input.VatAmount ?? Expense.VatFor(amount, input.VatRate, input.ReverseCharge));
        if (vat != 0 && Math.Sign(vat) != Math.Sign(amount)) return "Het btw-bedrag moet net als het bedrag positief zijn, of allebei negatief bij een terugbetaling.";
        if (Math.Abs(vat) > Math.Abs(amount)) return "Het btw-bedrag kan niet hoger zijn dan het bedrag zelf.";

        int? customerId = input.CustomerId;
        if (input.ProjectId is { } projectId)
        {
            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId);
            if (project is null) return "Kies een project uit je eigen werkruimte.";
            if (customerId is { } c && c != project.CustomerId) return "Dit project hoort bij een andere klant. Kies de klant van het project of laat de klant leeg.";
            customerId = project.CustomerId;
        }
        if (customerId is { } cid && !await db.Customers.AnyAsync(c => c.Id == cid)) return "Kies een klant uit je eigen werkruimte.";

        // Een investering kost minstens € 450: zonder btw als je de btw aftrekt, met btw als je dat niet kunt.
        int? years = null;
        decimal? residual = null;
        if (input.Investment)
        {
            var settings = await SettingsEndpoints.GetAsync(db);
            var deduction = settings.VatRegime is not (VatRegimes.Kor or VatRegimes.Exempt);
            var cost = new Expense { Amount = amount, VatAmount = vat, ReverseCharge = input.ReverseCharge }.CostForProfit(deduction);
            if (cost < TaxYears.DepreciationThreshold)
                return $"Een investering kost minstens € 450{(deduction ? " zonder btw" : "")}. Een goedkoper bedrijfsmiddel trek je in één keer af als kosten.";
            years = input.UsefulLifeYears ?? Depreciation.DefaultYears;
            if (years is < 1 or > Depreciation.MaxYears) return $"Kies een gebruiksduur van 1 tot en met {Depreciation.MaxYears} jaar.";
            residual = input.ResidualValue is { } rv && rv != 0 ? Money.Round(rv) : null;
            if (residual < 0) return "De restwaarde kan niet negatief zijn.";
            if (residual >= cost) return $"De restwaarde moet lager zijn dan de aanschafprijs van {cost.ToString("C", System.Globalization.CultureInfo.GetCultureInfo("nl-NL"))}.";
        }
        else if (e.DisposedOn is not null)
            return "Dit bedrijfsmiddel staat als verkocht of weggedaan. Maak dat eerst ongedaan bij Investeringen; daarna kun je er weer gewone kosten van maken.";
        if (e.DisposedOn is { } disposed && input.Date.Date > disposed)
            return "De aankoopdatum kan niet na de datum liggen waarop je het bedrijfsmiddel verkocht of wegdeed.";

        e.Date = input.Date.Date; e.Supplier = supplier; e.Description = description; e.Category = category; e.Amount = amount;
        e.VatRate = input.VatRate; e.VatAmount = vat; e.ReverseCharge = input.ReverseCharge; e.SupplierRegion = region;
        e.ProjectId = input.ProjectId; e.CustomerId = customerId;
        e.Investment = input.Investment; e.UsefulLifeYears = years; e.ResidualValue = residual; e.PassengerCar = input.Investment && input.PassengerCar;
        return null;
    }

    public static void MapExpenses(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/expenses");

        g.MapGet("/", async (AppDb db, DateTime? from, DateTime? to) =>
            await ListAsync(db, q =>
            {
                if (from is { } f) q = q.Where(e => e.Date >= f.Date);
                if (to is { } t) q = q.Where(e => e.Date <= t.Date);
                return q;
            })).ForAccountant();

        g.MapGet("/{id:int}", async (AppDb db, int id) =>
            await db.Expenses.AnyAsync(e => e.Id == id) ? Results.Ok(await OneAsync(db, id)) : Results.NotFound()).ForAccountant();

        g.MapPost("/", async (AppDb db, ExpenseInput input) =>
        {
            var e = new Expense();
            if (await ApplyAsync(db, e, input) is { } error) return Results.BadRequest(new { error });
            db.Expenses.Add(e);
            db.Log("kosten", $"Uitgave bij {e.Supplier} toegevoegd");
            await db.SaveChangesAsync();
            return Results.Created($"/api/expenses/{e.Id}", await OneAsync(db, e.Id));
        });

        g.MapPut("/{id:int}", async (AppDb db, int id, ExpenseInput input) =>
        {
            var e = await db.Expenses.FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            if (await ApplyAsync(db, e, input) is { } error) return Results.BadRequest(new { error });
            // Opslaan vanuit het formulier is nakijken: een concept van een vaste kost is daarna gewoon een uitgave.
            e.NeedsReview = false;
            await db.SaveChangesAsync();
            return Results.Ok(await OneAsync(db, id));
        });

        // Een concept van een vaste kost klopt: het is daarna een gewone uitgave.
        g.MapPost("/{id:int}/reviewed", async (AppDb db, int id) =>
        {
            var e = await db.Expenses.FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            if (e.NeedsReview)
            {
                e.NeedsReview = false;
                db.Log("kosten", $"Uitgave bij {e.Supplier} van {e.Date:dd-MM-yyyy} nagekeken");
                await db.SaveChangesAsync();
            }
            return Results.Ok(await OneAsync(db, id));
        });

        // Alleen de categorie aanpassen. Dat mag ook een boekhouder die kosten mag aanvullen; verder blijft de uitgave zoals hij is.
        g.MapPut("/{id:int}/category", async (AppDb db, int id, ExpenseCategoryInput input) =>
        {
            var e = await db.Expenses.FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            var category = input.Category?.Trim() ?? "";
            if (!ExpenseCategories.All.Contains(category)) return Results.BadRequest(new { error = "Kies een categorie." });
            if (e.Category != category)
            {
                db.Log("kosten", $"Categorie van de uitgave bij {e.Supplier} van {e.Date:dd-MM-yyyy} gewijzigd in {ExpenseCategories.Label(category)}");
                e.Category = category;
                await db.SaveChangesAsync();
            }
            return Results.Ok(await OneAsync(db, id));
        }).ForAccountant(AccountantPermissions.CompleteExpenses);

        // Het bonnetje gaat mee weg.
        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var e = await db.Expenses.FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            await db.ExpenseReceipts.Where(r => r.ExpenseId == id).ExecuteDeleteAsync();
            db.Expenses.Remove(e);
            db.Log("kosten", $"Uitgave bij {e.Supplier} verwijderd");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Een bonnetje uploaden. Een medewerker mag er een toevoegen; een bestaand bonnetje vervangen mag alleen een beheerder.
        g.MapPut("/{id:int}/receipt", async (AppDb db, IConfiguration config, ClaimsPrincipal user, int id, ReceiptUpload upload) =>
        {
            var data = upload.Data?.Trim() ?? "";
            if (data.StartsWith("data:", StringComparison.Ordinal)) data = data[(data.IndexOf(',') + 1)..];
            if (data == "") return Results.BadRequest(new { error = "Kies een bestand." });
            if (data.Length > (MaxReceiptBytes + 2) / 3 * 4 + 4) return Results.BadRequest(new { error = ReceiptTooLargeError });
            byte[] bytes;
            try { bytes = Convert.FromBase64String(data); }
            catch (FormatException) { return Results.BadRequest(new { error = ReceiptTypeError }); }
            if (bytes.Length > MaxReceiptBytes) return Results.BadRequest(new { error = ReceiptTooLargeError });
            if (DetectReceipt(bytes) is not { } detected) return Results.BadRequest(new { error = ReceiptTypeError });
            var (contentType, extension) = detected;

            await using var tx = await WriteLock.BeginAsync(db);
            var e = await db.Expenses.FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            var existing = await db.ExpenseReceipts.FirstOrDefaultAsync(r => r.ExpenseId == id);
            if (existing is not null && !IsAdmin(user)) return Results.Json(new { error = ReceiptAdminError }, statusCode: StatusCodes.Status403Forbidden);
            var used = await db.ExpenseReceipts.Where(r => r.ExpenseId != id).SumAsync(r => (long)r.Size);
            if (used + bytes.Length > ReceiptQuota(config, await db.Workspaces.FindAsync(db.TenantId)))
                return Results.Json(new { error = "De ruimte voor bonnetjes in je werkruimte is vol. Verwijder oude bonnetjes die je elders bewaart, of kies een groter abonnement." },
                    statusCode: StatusCodes.Status413PayloadTooLarge);

            var receipt = existing ?? db.ExpenseReceipts.Add(new ExpenseReceipt { ExpenseId = id }).Entity;
            receipt.FileName = SafeFileName(upload.FileName, extension, e.Date);
            receipt.ContentType = contentType;
            receipt.Size = bytes.Length;
            receipt.Data = bytes;
            receipt.UploadedAt = DateTime.UtcNow;
            db.Log("kosten", existing is null ? $"Bonnetje bij {e.Supplier} toegevoegd" : $"Bonnetje bij {e.Supplier} vervangen");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(await OneAsync(db, id));
        }).WithMetadata(new RequestSizeLimitAttribute(8 * 1024 * 1024)).ForAccountant(AccountantPermissions.CompleteExpenses);

        // Het bonnetje downloaden, alleen binnen de eigen werkruimte. Altijd als bijlage en met het type dat bij de inhoud hoort,
        // zodat de browser het nooit als pagina opent.
        g.MapGet("/{id:int}/receipt", async (AppDb db, HttpContext http, int id) =>
        {
            var r = await db.ExpenseReceipts.AsNoTracking().FirstOrDefaultAsync(x => x.ExpenseId == id);
            if (r is null) return Results.NotFound();
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            http.Response.Headers.CacheControl = "private, no-store";
            return Results.File(r.Data, r.ContentType, r.FileName);
        }).AccountantExport(Accountants.DescribeReceiptExport);

        g.MapDelete("/{id:int}/receipt", async (AppDb db, int id) =>
        {
            if (await db.ExpenseReceipts.Where(x => x.ExpenseId == id).ExecuteDeleteAsync() == 0) return Results.NotFound();
            db.Log("kosten", "Bonnetje verwijderd");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }
}
