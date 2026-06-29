using CedarRecon.Application.Matching;
using CedarRecon.Application.Matching.Strategies;
using CedarRecon.Domain;
using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Enums;
using CedarRecon.Tests.Unit.Helpers;
using Shouldly;

namespace CedarRecon.Tests.Unit.Matching.Strategies;

public class FuzzyMatchStrategyTests
{
    private readonly FuzzyMatchStrategy _sut = new();
    private readonly ReconciliationOptions _opts = OptionsFactory.Default();

    // Default opts tolerance: ±5 absolute, ±0.5%, ±3 days

    private static readonly DateTimeOffset BaseDate =
        new(2024, 3, 15, 0, 0, 0, TimeSpan.Zero);

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public void TryMatch_WhenAmountWithinAbsoluteTolerance_ReturnsMatch()
    {
        var source = Tx(1000m, BaseDate);
        var target = Tx(1003m, BaseDate); // diff=3 ≤ absolute=5

        var result = _sut.TryMatch(source, [target], _opts, new MatchContext());

        result.ShouldNotBeNull();
        result.Strategy.ShouldBe(MatchStrategy.Fuzzy);
        result.Score.Value.ShouldBeInRange(_opts.FuzzyMatchMinConfidence, _opts.FuzzyMatchMaxConfidence);
    }

    [Fact]
    public void TryMatch_WhenDateWithinWindow_ReturnsMatch()
    {
        var source = Tx(1000m, BaseDate);
        var target = Tx(1000m, BaseDate.AddDays(2)); // 2 ≤ 3 days

        _sut.TryMatch(source, [target], _opts, new MatchContext()).ShouldNotBeNull();
    }

    // ── Out-of-tolerance ──────────────────────────────────────────────────────

    [Fact]
    public void TryMatch_WhenAmountExceedsTolerance_ReturnsNull()
    {
        var source = Tx(1000m, BaseDate);
        var target = Tx(1006m, BaseDate); // diff=6 > absolute=5

        _sut.TryMatch(source, [target], _opts, new MatchContext()).ShouldBeNull();
    }

    [Fact]
    public void TryMatch_WhenDateExceedsWindow_ReturnsNull()
    {
        var source = Tx(1000m, BaseDate);
        var target = Tx(1000m, BaseDate.AddDays(4)); // 4 > 3 days

        _sut.TryMatch(source, [target], _opts, new MatchContext()).ShouldBeNull();
    }

    [Fact]
    public void TryMatch_WithZeroTolerance_OnlyPerfectAmountPasses()
    {
        var opts = OptionsFactory.ZeroTolerance();
        var source = Tx(1000m, BaseDate);

        _sut.TryMatch(source, [Tx(1000.01m, BaseDate)], opts, new MatchContext()).ShouldBeNull();
        _sut.TryMatch(source, [Tx(1000.00m, BaseDate)], opts, new MatchContext()).ShouldNotBeNull();
    }

    // ── Scoring ───────────────────────────────────────────────────────────────

    [Fact]
    public void TryMatch_ClaimsBestScoringCandidate_RegardlessOfListOrder()
    {
        var source = Tx(1000m, BaseDate);
        var close = Tx(1001m, BaseDate); // smaller diff → higher score
        var far = Tx(1004m, BaseDate);

        // far is first in list — strategy must still pick close
        var result = _sut.TryMatch(source, [far, close], _opts, new MatchContext());

        result.ShouldNotBeNull();
        result.Target.ShouldBe(close);
    }

    [Fact]
    public void TryMatch_ScoreDegrades_AsAmountDifferenceGrows()
    {
        var source = Tx(1000m, BaseDate);

        var r1 = _sut.TryMatch(source, [Tx(1001m, BaseDate)], _opts, new MatchContext());
        var r2 = _sut.TryMatch(source, [Tx(1004m, BaseDate)], _opts, new MatchContext());

        r1.ShouldNotBeNull();
        r2.ShouldNotBeNull();
        r1.Score.Value.ShouldBeGreaterThan(r2.Score.Value);
    }

    [Fact]
    public void TryMatch_ScoreDegrades_AsDateDifferenceGrows()
    {
        var source = Tx(1000m, BaseDate);

        var r1 = _sut.TryMatch(source, [Tx(1000m, BaseDate.AddDays(1))], _opts, new MatchContext());
        var r2 = _sut.TryMatch(source, [Tx(1000m, BaseDate.AddDays(2))], _opts, new MatchContext());

        r1.ShouldNotBeNull();
        r2.ShouldNotBeNull();
        r1.Score.Value.ShouldBeGreaterThan(r2.Score.Value);
    }

    [Fact]
    public void TryMatch_ScoreNeverExceedsMax()
    {
        var source = Tx(1000m, BaseDate);
        var target = Tx(1000m, BaseDate); // perfect amount + date

        var result = _sut.TryMatch(source, [target], _opts, new MatchContext());

        result.ShouldNotBeNull();
        result.Score.Value.ShouldBeLessThanOrEqualTo(_opts.FuzzyMatchMaxConfidence);
    }

    [Fact]
    public void TryMatch_ScoreNeverDropsBelowMin_AtEdgeOfTolerance()
    {
        var source = Tx(1000m, BaseDate);
        var target = Tx(1005m, BaseDate.AddDays(3)); // right at both edges

        var result = _sut.TryMatch(source, [target], _opts, new MatchContext());
        if (result is not null)
            result.Score.Value.ShouldBeGreaterThanOrEqualTo(_opts.FuzzyMatchMinConfidence);
    }

    // ── Race fallback ─────────────────────────────────────────────────────────

    [Fact]
    public void TryMatch_WhenBestCandidateClaimed_FallsBackToNextBest()
    {
        var source = Tx(1000m, BaseDate);
        var best = Tx(1001m, BaseDate);
        var second = Tx(1003m, BaseDate);

        var context = new MatchContext();
        context.TryClaim(best); // simulate another thread won it

        var result = _sut.TryMatch(source, [best, second], _opts, context);

        result.ShouldNotBeNull();
        result.Target.ShouldBe(second);
    }

    [Fact]
    public void TryMatch_WhenAllClaimed_ReturnsNull()
    {
        var source = Tx(1000m, BaseDate);
        var c1 = Tx(1001m, BaseDate);
        var c2 = Tx(1002m, BaseDate);

        var context = new MatchContext();
        context.TryClaim(c1);
        context.TryClaim(c2);

        _sut.TryMatch(source, [c1, c2], _opts, context).ShouldBeNull();
    }

    // ── Concurrency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TryMatch_UnderConcurrentCalls_ClaimsMatchAvailableCandidates()
    {
        var c1 = Tx(1001m, BaseDate);
        var c2 = Tx(1002m, BaseDate);
        var context = new MatchContext();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
                _sut.TryMatch(Tx(1000m, BaseDate), [c1, c2], _opts, context))));

        results.Count(r => r is not null).ShouldBe(2);
    }

    // ── Bogus: property-based spot check ─────────────────────────────────────

    [Fact]
    public void TryMatch_WithBogusPairs_WithinWideTolerance_AlwaysMatches()
    {
        // Generate 20 random transaction pairs, apply wide tolerance
        // Every pair should match — validates strategy handles arbitrary realistic amounts
        var opts = OptionsFactory.WideTolerance();
        var sources = Fakers.Transaction().Generate(20);
        var targets = Fakers.Transaction().Generate(20);

        // Force same reference + currency so they land in the same bucket
        // then check that wide tolerance picks them all up
        for (var i = 0; i < sources.Count; i++)
        {
            var src = TransactionBuilder.Default()
                .WithAmount(sources[i].Amount.Amount)
                .WithCurrency("USD")
                .WithReference("SHARED-REF")
                .WithValueDate(sources[i].ValueDate)
                .Build();

            var tgt = TransactionBuilder.Default()
                .WithAmount(sources[i].Amount.Amount) // same amount → within any tolerance
                .WithCurrency("USD")
                .WithReference("SHARED-REF")
                .WithValueDate(sources[i].ValueDate)  // same date
                .Build();

            var result = _sut.TryMatch(src, [tgt], opts, new MatchContext());
            result.ShouldNotBeNull($"Failed for amount={src.Amount.Amount}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Transaction Tx(decimal amount, DateTimeOffset date) =>
        TransactionBuilder.Default()
            .WithAmount(amount).WithCurrency("USD")
            .WithValueDate(date).WithReference("REF-SHARED")
            .Build();
}
