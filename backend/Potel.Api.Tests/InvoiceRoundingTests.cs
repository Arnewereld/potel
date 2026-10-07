using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Potel.Api.Data;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Eén afrondingsregel: server, dashboard en de geprinte factuur komen op dezelfde cent uit.
public class InvoiceRoundingTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task Server_totals_and_vat_match_the_invoice_document()
    {
        var c = await ReadyAsync(factory, "rounding");
        var customerId = await CustomerAsync(c);
        var id = await InvoiceAsync(c, customerId, "verzonden", 1.5m, 95);   // 142,50 + 21% = 29,925 btw

        var totals = (await GetInvoiceAsync(c, id)).GetProperty("totals");
        Assert.Equal(142.50m, totals.GetProperty("subtotal").GetDecimal());
        Assert.Equal(29.93m, totals.GetProperty("vat").GetDecimal());
        Assert.Equal(172.43m, totals.GetProperty("total").GetDecimal());

        var dash = await c.GetFromJsonAsync<JsonElement>("/api/dashboard");
        var vat = dash.GetProperty("vat").GetProperty("amount").GetDecimal();
        var outstanding = dash.GetProperty("outstanding").GetDecimal();
        Assert.True(outstanding == 172.43m && vat == 29.93m, $"Factuur zegt 172,43 / btw 29,93; server zegt {outstanding} / btw {vat}");
    }

    [Fact]
    public async Task Full_credit_note_cancels_the_invoice_to_the_cent()
    {
        var c = await ReadyAsync(factory, "credit-round");
        var customerId = await CustomerAsync(c);
        var invoiceId = await InvoiceAsync(c, customerId, "verzonden", 1.5m, 95);
        var credit = await Json(await c.PostAsync($"/api/invoices/{invoiceId}/credit", null));
        Assert.Equal(-1.5m, credit.GetProperty("lines")[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(-172.43m, Total(credit));
        Assert.Equal(0m, Total(await GetInvoiceAsync(c, invoiceId)) + Total(credit));
    }

    [Fact]
    public void Vat_is_rounded_per_rate_over_the_total_half_away_from_zero()
    {
        var lines = new List<InvoiceLine>
        {
            new() { Quantity = 1, UnitPrice = 0.05m, VatRate = 21 },   // 0,0105
            new() { Quantity = 1, UnitPrice = 0.05m, VatRate = 21 },   // samen 0,10 x 21% = 0,021 → 0,02 (per regel zou 0,01 + 0,01 zijn)
            new() { Quantity = 0.335m, UnitPrice = 10, VatRate = 9 },  // 3,35 → btw 0,3015 → 0,30
            new() { Quantity = 1, UnitPrice = 1.005m, VatRate = 0 },   // 1,005 → 1,01
        };
        var t = Money.Totals(lines);
        Assert.Equal(new[] { 21m, 9m, 0m }, t.VatGroups.Select(g => g.Rate));
        Assert.Equal(0.10m, t.VatGroups[0].Base);
        Assert.Equal(0.02m, t.VatGroups[0].Vat);
        Assert.Equal(0.30m, t.VatGroups[1].Vat);
        Assert.Equal(1.01m, t.VatGroups[2].Base);
        Assert.Equal(4.46m, t.Subtotal);
        Assert.Equal(4.78m, t.Total);
        Assert.Equal(-1.01m, Money.Round(-1.005m));
    }

    // De frontend rekent het concept zelf voor (frontend/src/lib/format.ts). Hier draaien we die code met Node op
    // dezelfde regels en vergelijken we elke cent met de server. Zonder Node of zonder de frontend slaan we dit over.
    [Fact]
    public void Frontend_invoice_totals_match_the_server_to_the_cent()
    {
        var format = FindFrontendFile("src/lib/format.ts");
        if (format is null || !NodeAvailable()) return;

        var rnd = new Random(32);
        var cases = new List<List<InvoiceLine>>
        {
            new() { new() { Quantity = 1.5m, UnitPrice = 95, VatRate = 21 } },
            new() { new() { Quantity = -1.5m, UnitPrice = 95, VatRate = 21 } },
            new() { new() { Quantity = 1, UnitPrice = 1.005m, VatRate = 21 }, new() { Quantity = 0.33m, UnitPrice = 87.5m, VatRate = 9 } },
            new() { new() { Quantity = 0.01m, UnitPrice = 0.5m, VatRate = 21 }, new() { Quantity = -0.01m, UnitPrice = 0.5m, VatRate = 21 } },
        };
        decimal[] rates = [21, 9, 0];
        for (var i = 0; i < 300; i++)
            cases.Add(Enumerable.Range(0, rnd.Next(1, 6)).Select(_ => new InvoiceLine
            {
                Quantity = rnd.Next(-400, 4000) / (rnd.Next(2) == 0 ? 100m : 4m),
                UnitPrice = rnd.Next(0, 2) == 0 ? rnd.Next(0, 20000) / 100m : rnd.Next(0, 300) + 0.5m,
                VatRate = rates[rnd.Next(rates.Length)],
            }).ToList());

        var dir = Directory.CreateTempSubdirectory("potel-totals-");
        try
        {
            var input = Path.Combine(dir.FullName, "cases.json");
            File.WriteAllText(input, JsonSerializer.Serialize(cases.Select(l => l.Select(x => new { quantity = x.Quantity, unitPrice = x.UnitPrice, vatRate = x.VatRate }))));
            var script = Path.Combine(dir.FullName, "totals.mjs");
            File.WriteAllText(script, $$"""
                import { readFileSync } from 'node:fs'
                import { invoiceTotals } from {{JsonSerializer.Serialize(new Uri(format).AbsoluteUri)}}
                const cases = JSON.parse(readFileSync(process.argv[2], 'utf8'))
                console.log(JSON.stringify(cases.map(lines => invoiceTotals(lines))))
                """);
            var output = Run("node", "--experimental-strip-types", "--no-warnings", script, input);
            var paper = JsonSerializer.Deserialize<List<JsonElement>>(output)!;
            for (var i = 0; i < cases.Count; i++)
            {
                var server = Money.Totals(cases[i]);
                var p = paper[i];
                var context = string.Join(" + ", cases[i].Select(l => $"{l.Quantity} x {l.UnitPrice} @ {l.VatRate}%"));
                Assert.True(server.Subtotal == p.GetProperty("subtotal").GetDecimal()
                            && server.Vat == p.GetProperty("vat").GetDecimal()
                            && server.Total == p.GetProperty("total").GetDecimal(),
                    $"{context}: server {server.Subtotal}/{server.Vat}/{server.Total}, factuur {p}");
                Assert.Equal(server.VatGroups.Select(g => (g.Rate, g.Base, g.Vat)),
                    p.GetProperty("vatGroups").EnumerateArray().Select(g => (g.GetProperty("rate").GetDecimal(), g.GetProperty("base").GetDecimal(), g.GetProperty("vat").GetDecimal())));
            }
        }
        finally
        {
            dir.Delete(true);
        }
    }

    static string? FindFrontendFile(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var path = Path.Combine(d.FullName, "frontend", relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    static bool NodeAvailable()
    {
        try { return Run("node", "--version").StartsWith('v'); }
        catch { return false; }
    }

    static string Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000)) { p.Kill(); throw new TimeoutException($"{file} reageert niet"); }
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} faalde: {stderr.Result}");
        return stdout.Result.Trim();
    }
}
