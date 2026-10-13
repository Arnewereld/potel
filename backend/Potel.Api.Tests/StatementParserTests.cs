using System.Text;
using Potel.Api.Banking;
using Potel.Api.Data;

namespace Potel.Api.Tests;

// Voorbeeldafschriften voor de tests. De kopregels van ING en Rabobank zijn zoals hun exports bij ons bekend zijn; echte
// bestanden van de banken hadden we niet.
static class StatementKit
{
    public const string Own = "NL91ABNA0417164300";
    public const string KlantIban = "NL02ABNA0123456789";
    public const string ShopIban = "NL20INGB0001234567";

    public static string Entry(decimal amount, string cd, string date, string? name = null, string? iban = null, string? text = null,
        string? reference = null, string status = "BOOK", string ccy = "EUR", bool reversal = false)
    {
        var party = cd == "CRDT" ? "Dbtr" : "Cdtr";
        return $"""
            <Ntry>
              <Amt Ccy="{ccy}">{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}</Amt>
              <CdtDbtInd>{cd}</CdtDbtInd>
              {(reversal ? "<RvslInd>true</RvslInd>" : "")}
              <Sts>{status}</Sts>
              <BookgDt><Dt>{date}</Dt></BookgDt>
              <ValDt><Dt>{date}</Dt></ValDt>
              {(reference is null ? "" : $"<AcctSvcrRef>{reference}</AcctSvcrRef>")}
              <NtryDtls><TxDtls>
                <RltdPties>
                  {(name is null ? "" : $"<{party}><Nm>{name}</Nm></{party}>")}
                  {(iban is null ? "" : $"<{party}Acct><Id><IBAN>{iban}</IBAN></Id></{party}Acct>")}
                </RltdPties>
                {(text is null ? "" : $"<RmtInf><Ustrd>{text}</Ustrd></RmtInf>")}
              </TxDtls></NtryDtls>
            </Ntry>
            """;
    }

    public static string Camt(params string[] entries) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <BkToCstmrStmt>
            <GrpHdr><MsgId>MSG1</MsgId><CreDtTm>2026-10-12T10:00:00</CreDtTm></GrpHdr>
            <Stmt>
              <Id>STMT1</Id>
              <Acct><Id><IBAN>{Own}</IBAN></Id></Acct>
              {string.Join("\n", entries)}
            </Stmt>
          </BkToCstmrStmt>
        </Document>
        """;

    public const string IngHeader = "\"Datum\";\"Naam / Omschrijving\";\"Rekening\";\"Tegenrekening\";\"Code\";\"Af Bij\";\"Bedrag (EUR)\";\"Mutatiesoort\";\"Mededelingen\"";

    public static string IngRow(string date, string name, string counter, string sign, string amount, string notes) =>
        $"\"{date}\";\"{name}\";\"{Own}\";\"{counter}\";\"OV\";\"{sign}\";\"{amount}\";\"Overschrijving\";\"{notes}\"";

    public static string Ing(params string[] rows) => string.Join("\r\n", [IngHeader, .. rows]) + "\r\n";

    public static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
}

public class StatementParserTests
{
    [Fact]
    public void Camt053_reads_booked_euro_entries_with_counterparty_description_and_sign()
    {
        var xml = StatementKit.Camt(
            StatementKit.Entry(121.00m, "CRDT", "2026-10-01", "Klant B.V.", StatementKit.KlantIban, "Factuur 2026-0001", "REF-1"),
            StatementKit.Entry(49.95m, "DBIT", "2026-10-02", "Coolblue", StatementKit.ShopIban, "Bestelling 123", "REF-2"),
            StatementKit.Entry(10m, "CRDT", "2026-10-03", "Nog niet geboekt", status: "PDNG"),
            StatementKit.Entry(30m, "DBIT", "2026-10-03", "In dollars", ccy: "USD"));
        var r = StatementParser.Parse(StatementKit.Bytes(xml));
        Assert.Null(r.Error);
        Assert.Equal("CAMT.053", r.Format);
        Assert.Equal(1, r.SkippedNotBooked);
        Assert.Equal(1, r.SkippedOtherCurrency);
        Assert.Equal(2, r.Transactions.Count);
        var inn = r.Transactions[0];
        Assert.Equal(121.00m, inn.Amount);
        Assert.Equal(new DateTime(2026, 10, 1), inn.Date);
        Assert.Equal("Klant B.V.", inn.CounterpartyName);
        Assert.Equal(StatementKit.KlantIban, inn.CounterpartyIban);
        Assert.Equal("Factuur 2026-0001", inn.Description);
        Assert.Equal("REF-1", inn.Reference);
        Assert.Equal(StatementKit.Own, inn.Account);
        var uit = r.Transactions[1];
        Assert.Equal(-49.95m, uit.Amount);
        Assert.Equal("Coolblue", uit.CounterpartyName);
    }

    [Fact]
    public void Camt053_splits_a_batch_only_when_the_parts_add_up_and_flips_a_reversal()
    {
        const string batch = """
            <Ntry>
              <Amt Ccy="EUR">150.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><Sts><Cd>BOOK</Cd></Sts>
              <BookgDt><Dt>2026-10-05</Dt></BookgDt><AcctSvcrRef>BATCH</AcctSvcrRef>
              <NtryDtls>
                <TxDtls><AmtDtls><TxAmt><Amt Ccy="EUR">100.00</Amt></TxAmt></AmtDtls><RltdPties><Dbtr><Nm>Een</Nm></Dbtr></RltdPties></TxDtls>
                <TxDtls><Amt Ccy="EUR">50.00</Amt><RltdPties><Dbtr><Pty><Nm>Twee</Nm></Pty></Dbtr></RltdPties></TxDtls>
              </NtryDtls>
            </Ntry>
            """;
        const string mismatch = """
            <Ntry>
              <Amt Ccy="EUR">90.00</Amt><CdtDbtInd>DBIT</CdtDbtInd><Sts>BOOK</Sts><BookgDt><Dt>2026-10-05</Dt></BookgDt>
              <AddtlNtryInf>Verzamelbetaling</AddtlNtryInf>
              <NtryDtls>
                <TxDtls><Amt Ccy="EUR">10.00</Amt><RltdPties><Cdtr><Nm>A</Nm></Cdtr></RltdPties></TxDtls>
                <TxDtls><Amt Ccy="EUR">20.00</Amt><RltdPties><Cdtr><Nm>B</Nm></Cdtr></RltdPties></TxDtls>
              </NtryDtls>
            </Ntry>
            """;
        var r = StatementParser.Parse(StatementKit.Bytes(StatementKit.Camt(batch, mismatch,
            StatementKit.Entry(25m, "DBIT", "2026-10-06", "Terug", reversal: true))));
        Assert.Null(r.Error);
        Assert.Equal(4, r.Transactions.Count);
        Assert.Equal([100m, 50m], r.Transactions.Take(2).Select(t => t.Amount));
        Assert.Equal(["Een", "Twee"], r.Transactions.Take(2).Select(t => t.CounterpartyName));
        Assert.Equal(["BATCH/1", "BATCH/2"], r.Transactions.Take(2).Select(t => t.Reference));
        // Klopt de som niet, dan één regel met het bedrag van de boeking en zonder tegenpartij.
        Assert.Equal(-90m, r.Transactions[2].Amount);
        Assert.Null(r.Transactions[2].CounterpartyName);
        Assert.Equal("Verzamelbetaling", r.Transactions[2].Description);
        // Een terugboeking van een afschrijving is geld dat terugkomt.
        Assert.Equal(25m, r.Transactions[3].Amount);
    }

    [Fact]
    public void Xml_with_a_dtd_or_external_entity_is_refused()
    {
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE Document [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02"><BkToCstmrStmt><Stmt><Ntry>
              <Amt Ccy="EUR">1.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><BookgDt><Dt>2026-10-01</Dt></BookgDt>
              <NtryDtls><TxDtls><RmtInf><Ustrd>&xxe;</Ustrd></RmtInf></TxDtls></NtryDtls>
            </Ntry></Stmt></BkToCstmrStmt></Document>
            """;
        var r = StatementParser.Parse(StatementKit.Bytes(xxe));
        Assert.Equal(StatementParser.NotAStatement, r.Error);
        Assert.Empty(r.Transactions);

        const string bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">]>
            <Document><BkToCstmrStmt><Stmt>&lol2;</Stmt></BkToCstmrStmt></Document>
            """;
        Assert.Equal(StatementParser.NotAStatement, StatementParser.Parse(StatementKit.Bytes(bomb)).Error);
    }

    [Fact]
    public void Other_files_get_a_friendly_message()
    {
        Assert.Equal(StatementParser.NotAStatement, StatementParser.Parse([]).Error);
        Assert.Equal(StatementParser.NotAStatement, StatementParser.Parse(StatementKit.Bytes("<html><body>Hallo</body></html>")).Error);
        Assert.Equal(StatementParser.NotAStatement, StatementParser.Parse(StatementKit.Bytes("<Document><Kapot")).Error);
        Assert.Equal(StatementParser.NotAStatement, StatementParser.Parse(StatementKit.Bytes("Gewoon een brief\nmet twee regels")).Error);
        // Een zip (xlsx) of pdf met binaire tekens.
        Assert.Equal(StatementParser.NotAStatement, StatementParser.Parse([0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00]).Error);
        Assert.Equal(StatementParser.Mt940, StatementParser.Parse(StatementKit.Bytes(":20:940S130403\r\n:25:NL91ABNA0417164300\r\n:61:1304030403C12,50NTRF\r\n")).Error);
        Assert.Equal(StatementParser.OtherCamt, StatementParser.Parse(StatementKit.Bytes(
            "<Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:camt.052.001.02\"><BkToCstmrAcctRpt/></Document>")).Error);
        Assert.Equal(StatementParser.TooBig, StatementParser.Parse(new byte[StatementParser.MaxBytes + 1]).Error);
    }

    [Fact]
    public void Ing_csv_is_recognized_by_its_header_with_dutch_amounts_and_af_bij()
    {
        var csv = StatementKit.Ing(
            StatementKit.IngRow("20261001", "Klant B.V.", StatementKit.KlantIban, "Bij", "1.234,56", "Factuur 2026-0001; \"\"spoed\"\""),
            StatementKit.IngRow("20261002", "COOLBLUE BV", StatementKit.ShopIban, "Af", "49,95", "Bestelling 123"));
        var r = StatementParser.Parse(StatementKit.Bytes("﻿" + csv));
        Assert.Null(r.Error);
        Assert.Equal("ING", r.Format);
        Assert.Equal([1234.56m, -49.95m], r.Transactions.Select(t => t.Amount));
        Assert.Equal(new DateTime(2026, 10, 1), r.Transactions[0].Date);
        Assert.Equal(StatementKit.KlantIban, r.Transactions[0].CounterpartyIban);
        Assert.Equal(StatementKit.Own, r.Transactions[0].Account);
        Assert.Equal("Factuur 2026-0001; \"spoed\"", r.Transactions[0].Description);
        Assert.Null(r.Transactions[0].Reference);

        var broken = StatementKit.Ing(StatementKit.IngRow("20261001", "X", "", "Bij", "twaalf", ""));
        Assert.Contains("Regel 2", StatementParser.Parse(StatementKit.Bytes(broken)).Error);
        var noSign = StatementKit.Ing(StatementKit.IngRow("20261001", "X", "", "Misschien", "12,00", ""));
        Assert.Contains("Regel 2", StatementParser.Parse(StatementKit.Bytes(noSign)).Error);
    }

    [Fact]
    public void Rabobank_csv_in_windows_1252_with_signed_amounts_and_sequence_numbers()
    {
        const string header = "\"IBAN/BBAN\",\"Munt\",\"BIC\",\"Volgnr\",\"Datum\",\"Rentedatum\",\"Bedrag\",\"Saldo na trn\",\"Tegenrekening IBAN/BBAN\",\"Naam tegenpartij\",\"Naam uiteindelijke partij\",\"Naam initiërende partij\",\"BIC tegenpartij\",\"Code\",\"Batch ID\",\"Transactiereferentie\",\"Machtigingskenmerk\",\"Incassant ID\",\"Betalingskenmerk\",\"Omschrijving-1\",\"Omschrijving-2\",\"Omschrijving-3\"";
        var rows = new[]
        {
            $"\"{StatementKit.Own}\",\"EUR\",\"RABONL2U\",\"000000000000007001\",\"2026-10-01\",\"2026-10-01\",\"+121,00\",\"+1121,00\",\"{StatementKit.KlantIban}\",\"Klant B.V.\",\"\",\"\",\"\",\"cb\",\"\",\"\",\"\",\"\",\"\",\"Factuur 2026-0001\",\"Café\",\"\"",
            $"\"{StatementKit.Own}\",\"EUR\",\"RABONL2U\",\"000000000000007002\",\"2026-10-02\",\"2026-10-02\",\"-12,50\",\"+1108,50\",\"\",\"Bakker\",\"\",\"\",\"\",\"bc\",\"\",\"\",\"\",\"\",\"\",\"Brood\",\"\",\"\"",
            $"\"{StatementKit.Own}\",\"USD\",\"RABONL2U\",\"000000000000007003\",\"2026-10-02\",\"2026-10-02\",\"-5,00\",\"+0,00\",\"\",\"Amerika\",\"\",\"\",\"\",\"bc\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\"",
        };
        var bytes = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetBytes(string.Join("\n", [header, .. rows]));
        var r = StatementParser.Parse(bytes);
        Assert.Null(r.Error);
        Assert.Equal("Rabobank", r.Format);
        Assert.Equal(1, r.SkippedOtherCurrency);
        Assert.Equal([121m, -12.50m], r.Transactions.Select(t => t.Amount));
        Assert.Equal("Factuur 2026-0001 Café", r.Transactions[0].Description);
        Assert.Equal("volgnr:000000000000007001", r.Transactions[0].Reference);
        Assert.Equal(StatementKit.KlantIban, r.Transactions[0].CounterpartyIban);
    }

    [Fact]
    public void An_unknown_csv_asks_for_a_mapping_and_then_reads_it()
    {
        const string csv = "Boekdatum,Tegenpartij,Rekening tegenpartij,Bedrag,Richting,Omschrijving\n" +
                           "01/10/2026,Klant,NL02 ABNA 0123 4567 89,\"1.000,00\",C,Factuur 2026-0001\n" +
                           "2-10-2026,Shop,,\"25,10\",D,Inkt\n";
        var first = StatementParser.Parse(StatementKit.Bytes(csv));
        Assert.True(first.NeedsMapping);
        Assert.Equal(["Boekdatum", "Tegenpartij", "Rekening tegenpartij", "Bedrag", "Richting", "Omschrijving"], first.Headers);
        Assert.Equal(2, first.Sample!.Count);

        var mapped = StatementParser.Parse(StatementKit.Bytes(csv), new CsvMapping("Boekdatum", "bedrag", "Omschrijving", "Tegenpartij", "Rekening tegenpartij", "Richting"));
        Assert.Null(mapped.Error);
        Assert.Equal([1000m, -25.10m], mapped.Transactions.Select(t => t.Amount));
        Assert.Equal([new DateTime(2026, 10, 1), new DateTime(2026, 10, 2)], mapped.Transactions.Select(t => t.Date));
        Assert.Equal(StatementKit.KlantIban, mapped.Transactions[0].CounterpartyIban);

        Assert.NotNull(StatementParser.Parse(StatementKit.Bytes(csv), new CsvMapping("Bestaat niet", "Bedrag", null, null, null, null)).Error);
        // Zonder teken-kolom staat het teken in het bedrag zelf.
        var signed = StatementParser.Parse(StatementKit.Bytes("Datum;Bedrag\n2026-10-01;-12,50\n2026-10-02;7,5\n"), new CsvMapping("Datum", "Bedrag", null, null, null, null));
        Assert.Equal([-12.50m, 7.50m], signed.Transactions.Select(t => t.Amount));
    }

    [Theory]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1234,56", 1234.56)]
    [InlineData("-12,50", -12.50)]
    [InlineData("+1500,00", 1500.00)]
    [InlineData("12.50", 12.50)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1.250", 1250)]
    [InlineData("€ 1 234,56", 1234.56)]
    [InlineData("€ 1.234,56", 1234.56)]
    [InlineData("(12,50)", -12.50)]
    [InlineData("12,50-", -12.50)]
    [InlineData("0,005", 0.01)]
    [InlineData("1.234.567,89", 1234567.89)]
    public void Amounts_are_read_the_dutch_way(string input, double expected) =>
        Assert.Equal((decimal)expected, StatementParser.ParseAmount(input));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12,50abc")]
    [InlineData("1e5")]
    [InlineData(",50")]
    [InlineData("12.")]
    [InlineData("99999999999999999999")]
    public void Nonsense_amounts_are_refused(string input) => Assert.Null(StatementParser.ParseAmount(input));

    [Theory]
    [InlineData("20261001", 2026, 10, 1)]
    [InlineData("2026-10-01", 2026, 10, 1)]
    [InlineData("01-10-2026", 2026, 10, 1)]
    [InlineData("1-10-2026", 2026, 10, 1)]
    [InlineData("01/10/2026", 2026, 10, 1)]
    [InlineData("01.10.2026", 2026, 10, 1)]
    [InlineData("2026-10-01T12:30:00", 2026, 10, 1)]
    [InlineData("05/04/2026", 2026, 4, 5)]
    public void Dates_are_day_before_month(string input, int y, int m, int d) =>
        Assert.Equal(new DateTime(y, m, d), StatementParser.ParseDate(input));

    [Theory]
    [InlineData("31-02-2026")]
    [InlineData("13/13/2026")]
    [InlineData("1899-01-01")]
    [InlineData("gisteren")]
    public void Impossible_dates_are_refused(string input) => Assert.Null(StatementParser.ParseDate(input));

    [Fact]
    public void Dedupe_keys_use_the_bank_reference_or_a_hash_with_an_occurrence_counter()
    {
        var coffee = new ParsedTransaction(new DateTime(2026, 10, 1), -3.20m, "Koffiebar", null, "Koffie", null, StatementKit.Own);
        var withRef = new ParsedTransaction(new DateTime(2026, 10, 1), 121m, "Klant", null, "Factuur", "REF-1", StatementKit.Own);
        var keys = StatementParser.DedupeKeys([coffee, withRef, coffee]);
        Assert.StartsWith("ref:", keys[1]);
        Assert.StartsWith("hash:", keys[0]);
        Assert.EndsWith("#1", keys[0]);
        Assert.EndsWith("#2", keys[2]);
        Assert.Equal(keys[0][..^2], keys[2][..^2]);
        // Hetzelfde afschrift nog eens geeft dezelfde sleutels; een andere omschrijving een andere.
        Assert.Equal(keys, StatementParser.DedupeKeys([coffee, withRef, coffee]));
        Assert.NotEqual(keys[0], StatementParser.DedupeKeys([coffee with { Description = "Thee" }])[0]);
        // Een kenmerk dat in één bestand twee keer voorkomt, is geen bruikbaar kenmerk.
        Assert.All(StatementParser.DedupeKeys([withRef, withRef with { Amount = 5m }]), k => Assert.StartsWith("hash:", k));
    }

    [Theory]
    [InlineData("NL91ABNA0417164300", true)]
    [InlineData("NL02ABNA0123456789", true)]
    [InlineData("NL03ABNA0123456789", false)]
    [InlineData("GB82WEST12345698765432", true)]
    [InlineData("NL91", false)]
    public void Ibans_are_checked_with_mod_97(string iban, bool valid) => Assert.Equal(valid, Ibans.IsValid(iban));

    [Theory]
    [InlineData("BAKKERIJ JANSEN BV", "Bakkerij Jansen B.V.", true)]
    [InlineData("HR J DE VRIES", "Jan de Vries", true)]
    [InlineData("Café Zoë", "CAFE ZOE", true)]
    [InlineData("Albert Heijn 1403", "Jumbo", false)]
    [InlineData("Jansen Installatie", "Pietersen Bouw", false)]
    public void Names_are_compared_loosely(string a, string b, bool same) => Assert.Equal(same, BankMatcher.SameName(a, b));

    [Fact]
    public void Invoice_numbers_are_found_with_or_without_separators_but_not_inside_longer_numbers()
    {
        var p = BankMatcher.NumberPattern("2026-0007")!;
        Assert.Matches(p, "Betaling factuur 2026-0007 dank");
        Assert.Matches(p, "FACTUUR 20260007");
        Assert.Matches(p, "fact. 2026 0007");
        Assert.DoesNotMatch(p, "2026-00071");
        Assert.DoesNotMatch(p, "12026-0007");
        Assert.Null(BankMatcher.NumberPattern("7"));
    }

    [Fact]
    public void Matcher_prefers_the_invoice_number_proposes_partial_payments_and_expenses()
    {
        var inv = new InvoiceCandidate(1, "2026-0001", "Klant B.V.", StatementKit.KlantIban, ["Klant B.V.", "Jan Klant"], 121m, InvoiceStatus.Sent, []);
        var other = new InvoiceCandidate(2, "2026-0002", "Ander", null, ["Ander"], 121m, InvoiceStatus.Sent, []);
        var paid = new InvoiceCandidate(3, "2026-0003", "Klant B.V.", null, ["Klant B.V."], 0m, InvoiceStatus.Paid, [new PaymentCandidate(9, 50m, new DateTime(2026, 10, 1))]);
        var expense = new ExpenseCandidate(5, new DateTime(2026, 9, 20), "Coolblue", 49.95m, "hardware", 21);
        var m = new BankMatcher([inv, other, paid], [expense]);

        BankTransaction T(decimal amount, string? name = null, string? iban = null, string? text = null) =>
            new() { Id = 1, Date = new DateTime(2026, 10, 2), Amount = amount, CounterpartyName = name, CounterpartyIban = iban, Description = text };

        var byNumber = m.Propose(T(121m, "Iemand anders", null, "factuur 2026-0002"));
        Assert.Equal(2, byNumber[0].InvoiceId);
        Assert.Equal("factuurnummer", byNumber[0].Reason);
        Assert.False(byNumber[0].Partial);

        var byIban = Assert.Single(m.Propose(T(121m, null, StatementKit.KlantIban)));
        Assert.Equal(1, byIban.InvoiceId);
        Assert.Equal("bedrag en rekeningnummer", byIban.Reason);
        Assert.Null(byIban.RememberIban);

        var byName = Assert.Single(m.Propose(T(121m, "KLANT BV", StatementKit.ShopIban)));
        Assert.Equal(StatementKit.ShopIban, byName.RememberIban);

        var partial = Assert.Single(m.Propose(T(60m, "Klant B.V.")));
        Assert.True(partial.Partial);
        Assert.Equal(1, partial.InvoiceId);

        var existing = Assert.Single(m.Propose(T(50m, "Klant BV")), p => p.Kind == "payment");
        Assert.Equal(9, existing.PaymentId);

        // Zelfde bedrag maar geen naam, rekeningnummer of factuurnummer: geen voorstel.
        Assert.Empty(m.Propose(T(121m, "Onbekend")));

        var out1 = Assert.Single(m.Propose(T(-49.95m, "COOLBLUE B.V.")));
        Assert.Equal(5, out1.ExpenseId);
        Assert.Equal("bedrag en leverancier", out1.Reason);
        Assert.Empty(m.Propose(T(-49.96m, "Coolblue")));

        var prefill = BankMatcher.Prefill(T(-12.10m, "COOLBLUE BV", text: "Kabel"), [expense]);
        Assert.Equal("Coolblue", prefill.Supplier);
        Assert.Equal("hardware", prefill.Category);
        Assert.Equal(12.10m, prefill.Amount);
        Assert.True(prefill.FromHistory);
        var fresh = BankMatcher.Prefill(T(-8m, "ALBERT HEIJN 1403"), [expense]);
        Assert.Equal("Albert Heijn 1403", fresh.Supplier);
        Assert.Null(fresh.Category);
    }
}
