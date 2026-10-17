using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// Hulpjes voor kostentests: uitgaven en bonnetjes.
static class ExpenseKit
{
    public static object Body(decimal amount = 121m, decimal? vatRate = 21, decimal? vatAmount = null, string supplier = "Coolblue", string category = "hardware",
        bool reverseCharge = false, string? supplierRegion = null, int? projectId = null, int? customerId = null, string date = "2026-05-12") => new
    {
        date, supplier, description = "Aankoop", category, amount, vatRate, vatAmount, reverseCharge, supplierRegion, projectId, customerId,
    };

    public static async Task<JsonElement> CreateAsync(HttpClient c, object body)
    {
        var res = await c.PostAsJsonAsync("/api/expenses", body);
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await res.JsonAsync();
    }

    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0];
    public static readonly byte[] Webp = [.. "RIFF"u8, 0x24, 0, 0, 0, .. "WEBPVP8 "u8];
    public static readonly byte[] Pdf = [.. "%PDF-1.4\n%bon\n1 0 obj\n<<>>\nendobj\n"u8];

    public static Task<HttpResponseMessage> UploadAsync(HttpClient c, int expenseId, byte[] bytes, string fileName = "bon.png", bool dataUrl = false) =>
        c.PutAsJsonAsync($"/api/expenses/{expenseId}/receipt", new
        {
            fileName, data = (dataUrl ? "data:application/octet-stream;base64," : "") + Convert.ToBase64String(bytes),
        });
}

public class ExpenseTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    static string Mail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    async Task<(HttpClient Admin, HttpClient Employee)> TeamAsync(string prefix)
    {
        var admin = await RegisterAsync(factory, Mail($"{prefix}-a"));
        var employee = Mail($"{prefix}-m");
        await CreateUserAsync(admin, employee);
        return (admin, await LoginAsync(factory, employee));
    }

    [Theory]
    [InlineData(121, 21, false, 21)]        // 121 inclusief 21% btw: 21 btw
    [InlineData(109, 9, false, 9)]          // 109 inclusief 9% btw: 9 btw
    [InlineData(10, 21, false, 1.74)]       // 10 × 21/121 = 1,7355 → 1,74
    [InlineData(100, 21, true, 21)]         // btw verlegd: 21% bovenop 100
    [InlineData(18.10, 21, true, 3.80)]     // 3,801 → 3,80
    [InlineData(50, 0, false, 0)]
    [InlineData(-121, 21, false, -21)]      // terugbetaling: btw ook negatief
    public void Vat_is_calculated_from_the_receipt_amount(decimal amount, decimal rate, bool reverse, decimal vat) =>
        Assert.Equal(vat, Expense.VatFor(amount, rate, reverse));

    [Fact]
    public void Vat_halves_round_away_from_zero_and_no_vat_means_zero()
    {
        Assert.Equal(0.01m, Expense.VatFor(0.05m, 21, true));    // 0,0105 → 0,01
        Assert.Equal(0.11m, Expense.VatFor(0.50m, 21, true));    // 0,105 → 0,11 (half van nul af)
        Assert.Equal(-0.11m, Expense.VatFor(-0.50m, 21, true));
        Assert.Equal(0m, Expense.VatFor(121m, null, false));
    }

    [Fact]
    public async Task Expense_is_saved_with_computed_or_corrected_vat()
    {
        var c = await RegisterAsync(factory, Mail("kosten-basis"));
        var computed = await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 121m));
        Assert.Equal(21m, computed.GetProperty("vatAmount").GetDecimal());
        Assert.Equal(100m, computed.GetProperty("amountExclVat").GetDecimal());

        // Een bon die anders afrondt: het btw-bedrag mag je zelf aanpassen.
        var corrected = await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 30.25m, vatAmount: 5.26m));
        Assert.Equal(5.26m, corrected.GetProperty("vatAmount").GetDecimal());
        Assert.Equal(24.99m, corrected.GetProperty("amountExclVat").GetDecimal());

        // Btw verlegd: geen btw betaald, dus het bedrag is zonder btw en de btw komt erbovenop.
        var reverse = await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 23m, supplier: "GitHub, Inc.", category: "software", reverseCharge: true, supplierRegion: "buiten-eu"));
        Assert.Equal(4.83m, reverse.GetProperty("vatAmount").GetDecimal());
        Assert.Equal(23m, reverse.GetProperty("amountExclVat").GetDecimal());
        Assert.Equal("buiten-eu", reverse.GetProperty("supplierRegion").GetString());

        // Geen btw: het btw-bedrag is altijd 0, ook als er iets anders wordt meegestuurd.
        var none = await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 40m, vatRate: null, vatAmount: 7m));
        Assert.Equal(0m, none.GetProperty("vatAmount").GetDecimal());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("vatRate").ValueKind);

        var id = computed.GetProperty("id").GetInt32();
        var put = await c.PutAsJsonAsync($"/api/expenses/{id}", ExpenseKit.Body(amount: 218m, vatRate: 9, supplier: "NS", category: "reiskosten"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = await put.JsonAsync();
        Assert.Equal(18m, updated.GetProperty("vatAmount").GetDecimal());
        Assert.Equal("NS", updated.GetProperty("supplier").GetString());

        var month = await c.GetFromJsonAsync<List<JsonElement>>("/api/expenses?from=2026-05-01&to=2026-05-31");
        Assert.Equal(4, month!.Count);
        Assert.Empty((await c.GetFromJsonAsync<List<JsonElement>>("/api/expenses?from=2026-06-01&to=2026-06-30"))!);
    }

    [Theory]
    [InlineData("leverancier", 121, 21, null, false, null, "")]
    [InlineData("categorie", 121, 21, null, false, null, "Coolblue", "eten")]
    [InlineData("bedrag", 0, 21, null, false, null, "Coolblue")]
    [InlineData("21%", 121, 6, null, false, null, "Coolblue")]
    [InlineData("hoger", 121, 21, 130, false, null, "Coolblue")]
    [InlineData("positief", 121, 21, -5, false, null, "Coolblue")]
    [InlineData("leverancier zit", 100, 21, null, true, null, "Coolblue")]
    [InlineData("tarief", 100, null, null, true, "eu", "Coolblue")]
    public async Task Invalid_expenses_are_refused(string message, decimal amount, int? rate, int? vatAmount, bool reverse, string? region, string supplier, string category = "hardware")
    {
        var c = await RegisterAsync(factory, Mail("kosten-fout"));
        var res = await c.PostAsJsonAsync("/api/expenses", new
        {
            date = "2026-05-12", supplier, category, amount, vatRate = (decimal?)rate,
            vatAmount = (decimal?)vatAmount, reverseCharge = reverse, supplierRegion = region,
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(message, await res.ErrorAsync());
        Assert.Empty((await c.GetFromJsonAsync<List<JsonElement>>("/api/expenses"))!);
    }

    [Fact]
    public async Task Linking_a_project_sets_its_customer_and_deleting_it_keeps_the_expense()
    {
        var c = await RegisterAsync(factory, Mail("kosten-project"));
        var customer = await CreateCustomerAsync(c, "Fietsplein");
        var other = await CreateCustomerAsync(c, "Ander");
        var project = (await (await c.PostAsJsonAsync("/api/projects", new { name = "Shop", customerId = customer, status = "actief", billing = "uur", hourlyRate = 90, color = "#000000" })).JsonAsync()).GetProperty("id").GetInt32();

        var e = await ExpenseKit.CreateAsync(c, ExpenseKit.Body(projectId: project));
        Assert.Equal(customer, e.GetProperty("customerId").GetInt32());
        Assert.Equal("Shop", e.GetProperty("projectName").GetString());
        var mismatch = await c.PostAsJsonAsync("/api/expenses", ExpenseKit.Body(projectId: project, customerId: other));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/projects/{project}")).StatusCode);
        var kept = await c.GetFromJsonAsync<JsonElement>($"/api/expenses/{e.GetProperty("id").GetInt32()}");
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("projectId").ValueKind);
        Assert.Equal(customer, kept.GetProperty("customerId").GetInt32());
    }

    [Fact]
    public async Task Workspaces_cannot_see_touch_or_link_each_others_expenses_and_receipts()
    {
        var a = await RegisterAsync(factory, Mail("kosten-a"));
        var b = await RegisterAsync(factory, Mail("kosten-b"));
        var e = await ExpenseKit.CreateAsync(a, ExpenseKit.Body(supplier: "Geheime leverancier"));
        var id = e.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await ExpenseKit.UploadAsync(a, id, ExpenseKit.Pdf, "geheim.pdf")).StatusCode);

        Assert.Empty((await b.GetFromJsonAsync<List<JsonElement>>("/api/expenses"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/expenses/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsJsonAsync($"/api/expenses/{id}", ExpenseKit.Body(supplier: "Gekaapt"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/expenses/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/expenses/{id}/receipt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ExpenseKit.UploadAsync(b, id, ExpenseKit.Png)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/expenses/{id}/receipt")).StatusCode);

        // Een project of klant van een andere werkruimte koppelen kan niet.
        var foreignCustomer = await CreateCustomerAsync(a, "Klant van A");
        var foreignProject = (await (await a.PostAsJsonAsync("/api/projects", new { name = "P", customerId = foreignCustomer, status = "actief", billing = "uur", hourlyRate = 1, color = "#000000" })).JsonAsync()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/expenses", ExpenseKit.Body(customerId: foreignCustomer))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/expenses", ExpenseKit.Body(projectId: foreignProject))).StatusCode);
        var own = await ExpenseKit.CreateAsync(b, ExpenseKit.Body());
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PutAsJsonAsync($"/api/expenses/{own.GetProperty("id").GetInt32()}", ExpenseKit.Body(projectId: foreignProject))).StatusCode);

        // A heeft alles nog, met het bonnetje.
        Assert.Equal("Geheime leverancier", (await a.GetFromJsonAsync<JsonElement>($"/api/expenses/{id}")).GetProperty("supplier").GetString());
        Assert.Equal(ExpenseKit.Pdf, await (await a.GetAsync($"/api/expenses/{id}/receipt")).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Employee_can_add_view_and_edit_but_only_an_admin_deletes()
    {
        var (admin, employee) = await TeamAsync("kosten-rol");
        var e = await ExpenseKit.CreateAsync(employee, ExpenseKit.Body());
        var id = e.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await employee.PutAsJsonAsync($"/api/expenses/{id}", ExpenseKit.Body(amount: 242m))).StatusCode);
        Assert.Single((await admin.GetFromJsonAsync<List<JsonElement>>("/api/expenses"))!);

        // Een eerste bonnetje mag, vervangen of verwijderen niet.
        Assert.Equal(HttpStatusCode.OK, (await ExpenseKit.UploadAsync(employee, id, ExpenseKit.Jpeg, "bon.jpg")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await employee.GetAsync($"/api/expenses/{id}/receipt")).StatusCode);
        var replace = await ExpenseKit.UploadAsync(employee, id, ExpenseKit.Png);
        Assert.Equal(HttpStatusCode.Forbidden, replace.StatusCode);
        Assert.Contains("beheerder", await replace.ErrorAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.DeleteAsync($"/api/expenses/{id}/receipt")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.DeleteAsync($"/api/expenses/{id}")).StatusCode);
        Assert.Equal("image/jpeg", (await admin.GetAsync($"/api/expenses/{id}/receipt")).Content.Headers.ContentType!.MediaType);

        Assert.Equal(HttpStatusCode.OK, (await ExpenseKit.UploadAsync(admin, id, ExpenseKit.Png)).StatusCode);
        Assert.Equal("image/png", (await admin.GetAsync($"/api/expenses/{id}/receipt")).Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/expenses/{id}/receipt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/expenses/{id}/receipt")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ExpenseKit.UploadAsync(admin, id, ExpenseKit.Pdf, "bon.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/expenses/{id}")).StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<List<JsonElement>>("/api/expenses"))!);
        Assert.False(factory.WithDb(db => db.ExpenseReceipts.IgnoreQueryFilters().Any(r => r.ExpenseId == id)));
    }

    [Fact]
    public async Task Read_only_workspace_can_view_but_not_change_expenses()
    {
        var c = await RegisterAsync(factory, Mail("kosten-alleenlezen"));
        var id = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body())).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await ExpenseKit.UploadAsync(c, id, ExpenseKit.Png)).StatusCode);
        await ExpireTrialAsync(factory, await WorkspaceIdAsync(c));

        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/expenses", ExpenseKit.Body())).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PutAsJsonAsync($"/api/expenses/{id}", ExpenseKit.Body(amount: 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.DeleteAsync($"/api/expenses/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await ExpenseKit.UploadAsync(c, id, ExpenseKit.Pdf)).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.DeleteAsync($"/api/expenses/{id}/receipt")).StatusCode);

        Assert.Single((await c.GetFromJsonAsync<List<JsonElement>>("/api/expenses"))!);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/expenses/{id}/receipt")).StatusCode);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("webp")]
    [InlineData("pdf")]
    public async Task Png_jpeg_webp_and_pdf_receipts_are_accepted_and_served_as_attachment(string kind)
    {
        var c = await RegisterAsync(factory, Mail($"bon-{kind}"));
        var id = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body())).GetProperty("id").GetInt32();
        var (bytes, type, ext) = kind switch
        {
            "png" => (ExpenseKit.Png, "image/png", "png"),
            "jpeg" => (ExpenseKit.Jpeg, "image/jpeg", "jpg"),
            "webp" => (ExpenseKit.Webp, "image/webp", "webp"),
            _ => (ExpenseKit.Pdf, "application/pdf", "pdf"),
        };
        // De naam zegt iets anders dan de inhoud: de inhoud wint, ook in de extensie.
        var res = await ExpenseKit.UploadAsync(c, id, bytes, "factuur maart.txt", dataUrl: kind == "pdf");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var receipt = (await res.JsonAsync()).GetProperty("receipt");
        Assert.Equal(type, receipt.GetProperty("contentType").GetString());
        Assert.Equal(bytes.Length, receipt.GetProperty("size").GetInt32());

        var file = await c.GetAsync($"/api/expenses/{id}/receipt");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(type, file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", file.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal($"factuur maart.{ext}", file.Content.Headers.ContentDisposition.FileNameStar ?? file.Content.Headers.ContentDisposition.FileName?.Trim('"'));
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", file.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(bytes, await file.Content.ReadAsByteArrayAsync());
    }

    public static TheoryData<string, byte[]> BadReceipts => new()
    {
        { "gif", [.. "GIF89a"u8, 1, 0, 1, 0] },
        { "svg", [.. "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8] },
        { "html", [.. "<!doctype html><script>alert(document.cookie)</script>"u8] },
        { "tekst", [.. "gewoon een bonnetje"u8] },
        { "pdf met voorloop", [.. " %PDF-1.4"u8] },
        { "leeg", [] },
    };

    [Theory]
    [MemberData(nameof(BadReceipts))]
    public async Task Files_that_are_not_png_jpeg_webp_or_pdf_are_refused(string kind, byte[] bytes)
    {
        var c = await RegisterAsync(factory, Mail($"bon-fout-{kind.Replace(' ', '-')}"));
        var id = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body())).GetProperty("id").GetInt32();
        // Ook met een naam die op een toegestaan bestand lijkt.
        var res = await ExpenseKit.UploadAsync(c, id, bytes, "bon.pdf");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/expenses/{id}/receipt")).StatusCode);
    }

    [Fact]
    public async Task Receipts_over_5_mb_or_with_broken_base64_are_refused()
    {
        var c = await RegisterAsync(factory, Mail("bon-groot"));
        var id = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body())).GetProperty("id").GetInt32();
        var big = new byte[5 * 1024 * 1024 + 1];
        ExpenseKit.Pdf.CopyTo(big, 0);
        var res = await ExpenseKit.UploadAsync(c, id, big, "groot.pdf");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("5 MB", await res.ErrorAsync());

        var exact = new byte[5 * 1024 * 1024];
        ExpenseKit.Pdf.CopyTo(exact, 0);
        Assert.Equal(HttpStatusCode.OK, (await ExpenseKit.UploadAsync(c, id, exact, "precies.pdf")).StatusCode);

        var broken = await c.PutAsJsonAsync($"/api/expenses/{id}/receipt", new { fileName = "x.png", data = "dit is geen base64!" });
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
    }

    [Theory]
    [InlineData("../../etc/passwd.pdf", "passwd.pdf")]
    [InlineData("C:\\Users\\jij\\bon\"; x=\"y.pdf", "bon xy.pdf")]
    [InlineData("<script>.pdf", "script.pdf")]
    [InlineData("...", "bonnetje-2026-05-12.pdf")]
    [InlineData(null, "bonnetje-2026-05-12.pdf")]
    public void File_names_lose_folders_and_odd_characters(string? name, string expected) =>
        Assert.Equal(expected, Potel.Api.Endpoints.ExpenseEndpoints.SafeFileName(name, "pdf", new DateTime(2026, 5, 12)));

    [Fact]
    public async Task Export_includes_expenses_and_receipts_of_the_own_workspace_only()
    {
        var a = await RegisterAsync(factory, Mail("export-a"));
        var b = await RegisterAsync(factory, Mail("export-b"));
        var mine = (await ExpenseKit.CreateAsync(a, ExpenseKit.Body(supplier: "Van A"))).GetProperty("id").GetInt32();
        await ExpenseKit.UploadAsync(a, mine, ExpenseKit.Pdf, "bon-a.pdf");
        var theirs = (await ExpenseKit.CreateAsync(b, ExpenseKit.Body(supplier: "Van B"))).GetProperty("id").GetInt32();
        await ExpenseKit.UploadAsync(b, theirs, ExpenseKit.Png, "bon-b.png");

        var res = await a.GetAsync("/api/workspace/export");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition!.DispositionType);
        var export = await res.JsonAsync();
        var expense = Assert.Single(export.GetProperty("expenses").EnumerateArray());
        Assert.Equal("Van A", expense.GetProperty("supplier").GetString());
        var receipt = Assert.Single(export.GetProperty("receipts").EnumerateArray());
        Assert.Equal(mine, receipt.GetProperty("expenseId").GetInt32());
        Assert.Equal("bon-a.pdf", receipt.GetProperty("fileName").GetString());
        Assert.Equal(ExpenseKit.Pdf, receipt.GetProperty("data").GetBytesFromBase64());
        // De rest van de export is er nog gewoon.
        Assert.Equal(JsonValueKind.Array, export.GetProperty("invoices").ValueKind);
        Assert.Equal(JsonValueKind.Object, export.GetProperty("settings").ValueKind);
        Assert.DoesNotContain("Van B", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Deleting_the_workspace_removes_its_expenses_and_receipts()
    {
        var c = await RegisterAsync(factory, Mail("kosten-weg"));
        var wsId = await WorkspaceIdAsync(c);
        var id = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body())).GetProperty("id").GetInt32();
        await ExpenseKit.UploadAsync(c, id, ExpenseKit.Pdf);
        var other = await RegisterAsync(factory, Mail("kosten-blijft"));
        await ExpenseKit.UploadAsync(other, (await ExpenseKit.CreateAsync(other, ExpenseKit.Body())).GetProperty("id").GetInt32(), ExpenseKit.Png);

        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "geheim123" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(delete)).StatusCode);

        Assert.False(factory.WithDb(db => db.Expenses.IgnoreQueryFilters().Any(e => e.WorkspaceId == wsId)));
        Assert.False(factory.WithDb(db => db.ExpenseReceipts.IgnoreQueryFilters().Any(r => r.WorkspaceId == wsId)));
        Assert.Single((await other.GetFromJsonAsync<List<JsonElement>>("/api/expenses"))!);
    }

    [Fact]
    public async Task Demo_data_has_expenses_that_go_away_with_it_unless_you_added_a_receipt()
    {
        var c = await RegisterAsync(factory, Mail("kosten-demo"));
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/workspace/demo-data", null)).StatusCode);
        var demo = (await c.GetFromJsonAsync<List<JsonElement>>("/api/expenses"))!;
        Assert.NotEmpty(demo);
        Assert.All(demo, e => Assert.True(e.GetProperty("demo").GetBoolean()));
        Assert.Contains(demo, e => e.GetProperty("supplier").GetString() == "Spaces Utrecht");
        Assert.Contains(demo, e => e.GetProperty("reverseCharge").GetBoolean());

        var withReceipt = demo[0].GetProperty("id").GetInt32();
        await ExpenseKit.UploadAsync(c, withReceipt, ExpenseKit.Png);
        var own = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body(supplier: "Eigen uitgave"))).GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/workspace/demo-data")).StatusCode);
        var left = (await c.GetFromJsonAsync<List<JsonElement>>("/api/expenses"))!;
        Assert.Equal(new[] { own, withReceipt }.Order(), left.Select(e => e.GetProperty("id").GetInt32()).Order());
        Assert.All(left, e => Assert.False(e.GetProperty("demo").GetBoolean()));
    }
}

// Een werkruimte in de proef heeft maar beperkt ruimte voor bonnetjes.
public class SmallReceiptQuotaFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Receipts:MaxMegabytesTrial", "1");
    }
}

public class ReceiptQuotaTests(SmallReceiptQuotaFactory factory) : IClassFixture<SmallReceiptQuotaFactory>
{
    [Fact]
    public async Task Uploads_stop_when_the_workspace_quota_is_full_and_replacing_counts_once()
    {
        var c = await RegisterAsync(factory, $"quota-{Guid.NewGuid():N}@example.com");
        var file = new byte[600 * 1024];
        ExpenseKit.Pdf.CopyTo(file, 0);
        var first = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body())).GetProperty("id").GetInt32();
        var second = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body())).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await ExpenseKit.UploadAsync(c, first, file)).StatusCode);
        // Vervangen telt het oude bonnetje niet dubbel.
        Assert.Equal(HttpStatusCode.OK, (await ExpenseKit.UploadAsync(c, first, file)).StatusCode);
        var full = await ExpenseKit.UploadAsync(c, second, file);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, full.StatusCode);
        Assert.Contains("vol", await full.ErrorAsync());
        Assert.Equal(HttpStatusCode.OK, (await ExpenseKit.UploadAsync(c, second, ExpenseKit.Png)).StatusCode);
    }
}
