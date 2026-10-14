using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Potel.Api.Banking;
using Potel.Api.Data;

namespace Potel.Api.Tests;

// Factuurnummers zoeken in omschrijvingen uit de bank: zonder time-out, in een tijd die hooguit gelijk oploopt met de
// lengte van de tekst, en met dezelfde uitkomst als voorheen. Een vijandige omschrijving geeft geen fout en geen trage server.
public class BankMatcherSafetyTests
{
    static BankTransaction T(string description, decimal amount = 121m) =>
        new() { Id = 1, Date = new DateTime(2026, 10, 2), Amount = amount, CounterpartyName = "Iemand", Description = description };

    [Fact]
    public void The_pattern_has_no_timeout_and_does_not_backtrack()
    {
        var p = BankMatcher.NumberPattern("2026-0007")!;
        Assert.Equal(Regex.InfiniteMatchTimeout, p.MatchTimeout);
        Assert.True(p.Options.HasFlag(RegexOptions.NonBacktracking));
        Assert.Null(BankMatcher.NumberPattern(new string('1', BankMatcher.MaxNumberLength + 1)));
        Assert.Null(BankMatcher.NumberPattern("a-b-c"));
    }

    [Theory]
    [InlineData("2026-0007", true)]
    [InlineData("(2026-0007)", true)]
    [InlineData("factuur:2026.0007, dank", true)]
    [InlineData("2026_0007", true)]
    [InlineData("2026/0007", true)]
    [InlineData("2026\t0007", true)]
    [InlineData("x2026-0007", false)]
    [InlineData("2026-0007x", false)]
    [InlineData("2026--0007", false)]
    [InlineData("2026-00070", false)]
    [InlineData("02026-0007", false)]
    [InlineData("2026-000", false)]
    public void Numbers_are_found_as_a_word_with_at_most_one_separator(string text, bool found) =>
        Assert.Equal(found, BankMatcher.NumberPattern("2026-0007")!.IsMatch(text));

    [Fact]
    public void A_megabyte_of_near_misses_is_searched_quickly_and_without_errors()
    {
        var p = BankMatcher.NumberPattern("2026-0007")!;
        var nearMisses = new StringBuilder();
        while (nearMisses.Length < 1_000_000) nearMisses.Append("2026-000 2026--0007 20260007x 2026-00070 ");
        var separators = "2026" + new string('-', 1_000_000) + "0007";
        var dashes = string.Concat(Enumerable.Repeat("2026-", 200_000));

        var watch = Stopwatch.StartNew();
        Assert.False(p.IsMatch(nearMisses.ToString()));
        Assert.True(p.IsMatch(nearMisses + "2026 0007."));
        Assert.False(p.IsMatch(separators));
        Assert.False(p.IsMatch(dashes));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void The_matcher_survives_a_hostile_description_and_still_finds_the_number_at_the_start()
    {
        var invoices = Enumerable.Range(1, 300)
            .Select(i => new InvoiceCandidate(i, $"2026-{i:0000}", $"Klant {i}", null, [$"Klant {i}"], 121m, InvoiceStatus.Sent, []))
            .ToList();
        var matcher = new BankMatcher(invoices, []);
        var junk = new StringBuilder("Factuur 2026-0042 ");
        while (junk.Length < 1_000_000) junk.Append("2026-00 2026--0042 x2026-0042 2026-00421 ");
        // Een nummer ver na het begin telt niet meer mee: een omschrijving uit de bank is hooguit 1000 tekens.
        junk.Append(" 2026-0099");

        var watch = Stopwatch.StartNew();
        var proposals = matcher.Propose(T(junk.ToString()));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.ElapsedMilliseconds} ms");
        var proposal = Assert.Single(proposals);
        Assert.Equal(42, proposal.InvoiceId);
        Assert.Equal("factuurnummer", proposal.Reason);

        // Namen van een megabyte vergelijken gaat ook snel.
        var longName = string.Concat(Enumerable.Repeat("Bakkerij Jansen ", 65_000));
        watch.Restart();
        Assert.True(BankMatcher.SameName(longName, "BAKKERIJ JANSEN BV"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.ElapsedMilliseconds} ms");
    }
}
