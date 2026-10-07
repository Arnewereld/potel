using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// Logo en accentkleur komen op de factuur; alleen een beheerder past bedrijfsgegevens aan.
public class SettingsTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    const string Png = "data:image/png;base64,iVBORw0KGgo=";

    static async Task<Dictionary<string, object?>> CurrentAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<Dictionary<string, JsonElement>>("/api/settings"))!.ToDictionary(k => k.Key, k => (object?)k.Value);

    static async Task<HttpResponseMessage> PutAsync(HttpClient c, string? logo, string? color)
    {
        var body = await CurrentAsync(c);
        body["logoDataUrl"] = logo;
        body["brandColor"] = color;
        return await c.PutAsJsonAsync("/api/settings", body);
    }

    [Fact]
    public async Task Logo_and_brand_color_are_saved()
    {
        var c = await RegisterAsync(factory, "huisstijl@example.com");
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(c, Png, "#4EA5FF")).StatusCode);
        var after = await c.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal(Png, after.GetProperty("logoDataUrl").GetString());
        Assert.Equal("#4ea5ff", after.GetProperty("brandColor").GetString());

        // Logo weghalen kan ook.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(c, null, "#4ea5ff")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("logoDataUrl").ValueKind);
    }

    [Theory]
    [InlineData("data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciLz4=")]
    [InlineData("data:text/html;base64,PGgxPmhvaTwvaDE+")]
    [InlineData("data:image/png;base64,AAAA")] // geen echte PNG
    [InlineData("data:image/png;base64,niet-base64!")]
    [InlineData("https://example.com/logo.png")]
    public async Task Logos_other_than_real_png_jpeg_or_webp_are_refused(string logo)
    {
        var c = await RegisterAsync(factory, $"logo-{Guid.NewGuid():N}@example.com");
        var res = await PutAsync(c, logo, "#ff6d5a");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("logo", await res.ErrorAsync());
        Assert.Equal(JsonValueKind.Null, (await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("logoDataUrl").ValueKind);
    }

    [Fact]
    public async Task Jpeg_and_webp_are_accepted_but_not_above_300_kb()
    {
        var c = await RegisterAsync(factory, "logo-groot@example.com");
        var jpeg = "data:image/jpeg;base64," + Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10]);
        var webp = "data:image/webp;base64," + Convert.ToBase64String("RIFF\0\0\0\0WEBPVP8 "u8.ToArray());
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(c, jpeg, "#ff6d5a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(c, webp, "#ff6d5a")).StatusCode);

        var big = new byte[300 * 1024 + 1];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(big, 0);
        var res = await PutAsync(c, "data:image/png;base64," + Convert.ToBase64String(big), "#ff6d5a");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("300 KB", await res.ErrorAsync());
    }

    [Theory]
    [InlineData("rood")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("url(javascript:alert(1))")]
    public async Task Brand_color_must_be_hex(string color)
    {
        var c = await RegisterAsync(factory, $"kleur-{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(c, null, color)).StatusCode);
    }

    [Fact]
    public async Task Employee_cannot_change_company_details_or_iban()
    {
        var admin = await RegisterAsync(factory, "iban-a@example.com");
        await CreateUserAsync(admin, "mia@example.com");
        var mia = await LoginAsync(factory, "mia@example.com");
        var body = await CurrentAsync(mia);
        body["iban"] = "NL99 EVIL 0000 0000 00";
        var put = await mia.PutAsJsonAsync("/api/settings", body);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Contains("beheerder", await put.ErrorAsync());
        Assert.NotEqual("NL99 EVIL 0000 0000 00", (await admin.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("iban").GetString());
        // Lezen mag wel, bijvoorbeeld voor de factuur.
        Assert.Equal(HttpStatusCode.OK, (await mia.GetAsync("/api/settings")).StatusCode);
    }
}
