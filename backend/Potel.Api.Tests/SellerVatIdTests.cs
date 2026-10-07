using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Je factureert als Nederlands bedrijf: je btw-id moet altijd een geldig Nederlands btw-id zijn, ook als het niet met NL begint.
public class SellerVatIdTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    async Task<HttpClient> WithVatIdAsync(string btw)
    {
        var c = await TestApi.RegisterAsync(factory, $"btw-{Guid.NewGuid():N}@example.com");
        (await c.PutAsJsonAsync("/api/settings", Company(btw: btw))).EnsureSuccessStatusCode();
        return c;
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("001234567B01")]
    [InlineData("BE0123456789")]
    [InlineData("DE123456789")]
    public async Task A_vat_id_that_is_not_a_dutch_one_blocks_sending(string btw)
    {
        var c = await WithVatIdAsync(btw);
        var id = await InvoiceAsync(c, await CustomerAsync(c), "concept", 1, 100);
        var res = await SetStatusAsync(c, id, "verzonden");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("btw-id klopt niet", await res.ErrorAsync());
        Assert.Equal("concept", (await GetInvoiceAsync(c, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_vat_id_with_spaces_and_dots_is_stored_and_printed_in_its_normal_form()
    {
        var c = await WithVatIdAsync(" nl 0012.34567.b01 ");
        Assert.Equal("NL001234567B01", (await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("btw").GetString());

        var id = await InvoiceAsync(c, await CustomerAsync(c), "concept", 1, 100);
        var sent = await SetStatusAsync(c, id, "verzonden");
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal("NL001234567B01", (await Json(sent)).GetProperty("seller").GetProperty("vatNumber").GetString());
    }
}
