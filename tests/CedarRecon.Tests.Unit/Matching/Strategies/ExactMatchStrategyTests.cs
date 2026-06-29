using CedarRecon.Application.Matching;
using CedarRecon.Application.Matching.Strategies;
using CedarRecon.Domain;
using CedarRecon.Domain.Enums;
using CedarRecon.Tests.Unit.Helpers;
using Shouldly;

namespace CedarRecon.Tests.Unit.Matching.Strategies;

/// <summary>
/// Tests for ExactMatchStrategy.
///
/// Invariants under test:
///   - Returns a match only when amount + currency + date all match exactly
///   - Any single field differing → no match
///   - Does not claim a candidate that is already claimed
///   - Claims the first matching candidate in iteration order
///   - Returns null when candidate list is empty
///   - Concurrent claims: only one thread successfully claims a candidate
/// </summary>
public class ExactMatchStrategyTests
{
    private readonly ExactMatchStrategy _sut = new();
    private readonly ReconciliationOptions _opts = OptionsFactory.Default();

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public void TryMatch_WhenAllFieldsMatch_ReturnsMatchedPair()
    {
        var (source, target) = Fakers.MatchingPair();

        var result = _sut.TryMatch(source, [target], _opts, new MatchContext());

        result.ShouldNotBeNull();
        result.Strategy.ShouldBe(MatchStrategy.Exact);
        result.Score.Value.ShouldBe(_opts.ExactMatchConfidence);
    }

    [Fact]
    public void TryMatch_WithMultipleCandidates_ClaimsFirstExactMatch()
    {
        var (source, correct) = Fakers.MatchingPair(amount: 200m);
        var wrong = TransactionBuilder.Default().WithAmount(999m).Build();

        var result = _sut.TryMatch(source, [wrong, correct], _opts, new MatchContext());

        result.ShouldNotBeNull();
        result.Target.ShouldBe(correct);
    }

    // ── One field off at a time ───────────────────────────────────────────────

    [Fact]
    public void TryMatch_WhenAmountDiffers_ReturnsNull()
    {
        var (source, _) = Fakers.MatchingPair(amount: 500m);
        var target = TransactionBuilder.Default()
            .WithAmount(500.01m).WithCurrency("USD")
            .WithReference(source.NormalizedReference.Value)
            .Build();

        _sut.TryMatch(source, [target], _opts, new MatchContext()).ShouldBeNull();
    }

    [Fact]
    public void TryMatch_WhenCurrencyDiffers_ReturnsNull()
    {
        var source = TransactionBuilder.Default().WithCurrency("USD").Build();
        var target = TransactionBuilder.Default().WithCurrency("EUR").Build();

        _sut.TryMatch(source, [target], _opts, new MatchContext()).ShouldBeNull();
    }

    [Fact]
    public void TryMatch_WhenDateDiffersByOneDay_ReturnsNull()
    {
        var date = new DateTimeOffset(2024, 3, 15, 0, 0, 0, TimeSpan.Zero);
        var source = TransactionBuilder.Default().WithValueDate(date).Build();
        var target = TransactionBuilder.Default().WithValueDate(date.AddDays(1)).Build();

        _sut.TryMatch(source, [target], _opts, new MatchContext()).ShouldBeNull();
    }

    [Fact]
    public void TryMatch_WhenCurrencyDiffersByCase_ReturnsNotNull()
    {
        var source = TransactionBuilder.Default().WithCurrency("USD").Build();
        var target = TransactionBuilder.Default().WithCurrency("usd").Build();

        _sut.TryMatch(source, [target], _opts, new MatchContext()).ShouldNotBeNull();
    }

    // ── Date: same UTC day, different time component ──────────────────────────

    [Fact]
    public void TryMatch_SameUtcDayDifferentTimes_ReturnsMatch()
    {
        var (source, _) = Fakers.MatchingPair();
        var target = TransactionBuilder.Default()
            .WithAmount(source.Amount.Amount)
            .WithCurrency(source.Amount.Currency)
            .WithReference(source.NormalizedReference.Value)
            .WithValueDate(new DateTimeOffset(2024, 3, 15, 23, 59, 59, TimeSpan.Zero))
            .Build();

        var src = TransactionBuilder.Default()
            .WithAmount(source.Amount.Amount)
            .WithCurrency(source.Amount.Currency)
            .WithReference(source.NormalizedReference.Value)
            .WithValueDate(new DateTimeOffset(2024, 3, 15, 0, 0, 0, TimeSpan.Zero))
            .Build();

        _sut.TryMatch(src, [target], _opts, new MatchContext()).ShouldNotBeNull();
    }

    // ── Empty candidates ──────────────────────────────────────────────────────

    [Fact]
    public void TryMatch_WhenEmptyCandidates_ReturnsNull()
    {
        var (source, _) = Fakers.MatchingPair();
        _sut.TryMatch(source, [], _opts, new MatchContext()).ShouldBeNull();
    }

    // ── Claim / context ───────────────────────────────────────────────────────

    [Fact]
    public void TryMatch_WhenCandidateAlreadyClaimed_ReturnsNull()
    {
        var (source, target) = Fakers.MatchingPair();
        var context = new MatchContext();
        context.TryClaim(target);

        _sut.TryMatch(source, [target], _opts, context).ShouldBeNull();
    }

    [Fact]
    public void TryMatch_ConsumesCandidate_SecondCallReturnsNull()
    {
        var (source, target) = Fakers.MatchingPair();
        var context = new MatchContext();

        _sut.TryMatch(source, [target], _opts, context).ShouldNotBeNull();
        _sut.TryMatch(source, [target], _opts, context).ShouldBeNull();
    }

    // ── Concurrency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TryMatch_UnderConcurrentCalls_ExactlyOneThreadWins()
    {
        var (_, target) = Fakers.MatchingPair();
        var context = new MatchContext();

        // Each source must match target on all three fields IsExactMatch checks:
        // amount + currency + date. Reference is irrelevant — the strategy never sees it,
        // that filtering happens in the engine before TryMatch is called.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            {
                var src = TransactionBuilder.Default()
                    .WithAmount(target.Amount.Amount)
                    .WithCurrency(target.Amount.Currency)
                    .WithValueDate(target.ValueDate)
                    .Build();
                return _sut.TryMatch(src, [target], _opts, context);
            })));

        results.Count(r => r is not null).ShouldBe(1);
    }
}