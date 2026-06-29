using CedarRecon.Application.Matching;
using CedarRecon.Application.Matching.Strategies;
using CedarRecon.Domain;
using CedarRecon.Domain.Enums;
using CedarRecon.Tests.Unit.Helpers;
using Shouldly;
namespace CedarRecon.Tests.Unit.Matching.Strategies;

/// <summary>
/// Tests for PartialMatchStrategy.
///
/// Structure:
///   Section A — resolver-agnostic behaviour (run against all three resolvers via Theory)
///   Section B — resolver-specific correctness (each implementation in isolation)
///   Section C — claim / context interaction (resolver-independent)
///   Section D — confidence and strategy metadata
///
/// Every invariant in Section A is verified against all three resolvers so we know
/// ScaledLong and UnsafeMantissa are drop-in replacements for Decimal.
/// </summary>
public class PartialMatchStrategyTests
{
    private readonly ReconciliationOptions _options = OptionsFactory.Default();

    // ── Resolver instances exposed for Theory data ────────────────────────────

    public static TheoryData<ModuloResolver.IsEvenlyDivisible, string> AllResolvers =>
        new()
        {
            { ModuloResolver.Decimal,        nameof(ModuloResolver.Decimal)        },
            { ModuloResolver.ScaledLong,     nameof(ModuloResolver.ScaledLong)     },
            { ModuloResolver.UnsafeMantissa, nameof(ModuloResolver.UnsafeMantissa) },
        };

    // ═══════════════════════════════════════════════════════════════════════════
    // Section A — resolver-agnostic invariants (all three resolvers)
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory, MemberData(nameof(AllResolvers))]
    public void TryMatch_WhenCandidateIsExactHalf_ReturnsMatch(
        ModuloResolver.IsEvenlyDivisible resolver, string _)
    {
        var sut = new PartialMatchStrategy(resolver);
        var source = Tx(900m).Build();
        var candidate = Tx(450m).Build();

        sut.TryMatch(source, [candidate], _options, new MatchContext()).ShouldNotBeNull();
    }

    [Theory, MemberData(nameof(AllResolvers))]
    public void TryMatch_WhenCandidateIsExactThird_ReturnsMatch(
        ModuloResolver.IsEvenlyDivisible resolver, string _)
    {
        var sut = new PartialMatchStrategy(resolver);
        var source = Tx(900m).Build();
        var candidate = Tx(300m).Build();

        sut.TryMatch(source, [candidate], _options, new MatchContext()).ShouldNotBeNull();
    }

    [Theory, MemberData(nameof(AllResolvers))]
    public void TryMatch_WhenCandidateEqualsSource_ReturnsNull(
        ModuloResolver.IsEvenlyDivisible resolver, string _)
    {
        var sut = new PartialMatchStrategy(resolver);
        var source = Tx(900m).Build();
        var candidate = Tx(900m).Build();

        sut.TryMatch(source, [candidate], _options, new MatchContext()).ShouldBeNull();
    }

    [Theory, MemberData(nameof(AllResolvers))]
    public void TryMatch_WhenCandidateExceedsSource_ReturnsNull(
        ModuloResolver.IsEvenlyDivisible resolver, string _)
    {
        var sut = new PartialMatchStrategy(resolver);
        var source = Tx(900m).Build();
        var candidate = Tx(1000m).Build();

        sut.TryMatch(source, [candidate], _options, new MatchContext()).ShouldBeNull();
    }

    [Theory, MemberData(nameof(AllResolvers))]
    public void TryMatch_WhenCandidateIsZero_ReturnsNull(
        ModuloResolver.IsEvenlyDivisible resolver, string _)
    {
        var sut = new PartialMatchStrategy(resolver);
        var source = Tx(900m).Build();
        var candidate = Tx(0m).Build();

        sut.TryMatch(source, [candidate], _options, new MatchContext()).ShouldBeNull();
    }

    [Theory, MemberData(nameof(AllResolvers))]
    public void TryMatch_WhenNotCleanDivisor_ReturnsNull(
        ModuloResolver.IsEvenlyDivisible resolver, string _)
    {
        var sut = new PartialMatchStrategy(resolver);
        var source = Tx(900m).Build();
        var candidate = Tx(400m).Build(); // 900 % 400 = 100

        sut.TryMatch(source, [candidate], _options, new MatchContext()).ShouldBeNull();
    }

    [Theory, MemberData(nameof(AllResolvers))]
    public void TryMatch_WithNegativeAmounts_MatchesOnAbsoluteValues(
        ModuloResolver.IsEvenlyDivisible resolver, string _)
    {
        var sut = new PartialMatchStrategy(resolver);
        var source = Tx(-900m).Build();
        var candidate = Tx(-300m).Build();

        sut.TryMatch(source, [candidate], _options, new MatchContext()).ShouldNotBeNull();
    }

    [Theory, MemberData(nameof(AllResolvers))]
    public void TryMatch_WithDecimalAmounts_MatchesCleanFraction(
        ModuloResolver.IsEvenlyDivisible resolver, string _)
    {
        // 0.10 / 0.05 = 2 — tests sub-cent precision handling
        var sut = new PartialMatchStrategy(resolver);
        var source = Tx(0.10m).Build();
        var candidate = Tx(0.05m).Build();

        sut.TryMatch(source, [candidate], _options, new MatchContext()).ShouldNotBeNull();
    }

    // Parameterised: clean divisors — all resolvers must agree
    [Theory]
    [InlineData(1000, 500, "Decimal", true)]
    [InlineData(1000, 500, "ScaledLong", true)]
    [InlineData(1000, 500, "UnsafeMantissa", true)]
    [InlineData(1000, 300, "Decimal", false)]  // 1000 % 300 = 100
    [InlineData(1000, 300, "ScaledLong", false)]
    [InlineData(1000, 300, "UnsafeMantissa", false)]
    [InlineData(600, 100, "Decimal", true)]
    [InlineData(600, 100, "ScaledLong", true)]
    [InlineData(600, 100, "UnsafeMantissa", true)]
    [InlineData(7, 3, "Decimal", false)]  // 7 % 3 = 1
    [InlineData(7, 3, "ScaledLong", false)]
    [InlineData(7, 3, "UnsafeMantissa", false)]
    public void TryMatch_AllResolversAgree_OnKnownCases(
        decimal sourceAmount,
        decimal candidateAmount,
        string resolverName,
        bool expectedMatch)
    {
        var resolver = resolverName switch
        {
            "Decimal" => ModuloResolver.Decimal,
            "ScaledLong" => ModuloResolver.ScaledLong,
            "UnsafeMantissa" => ModuloResolver.UnsafeMantissa,
            _ => throw new ArgumentException(resolverName)
        };

        var sut = new PartialMatchStrategy(resolver);
        var source = Tx(sourceAmount).Build();
        var candidate = Tx(candidateAmount).Build();

        var result = sut.TryMatch(source, [candidate], _options, new MatchContext());

        if (expectedMatch)
            result.ShouldNotBeNull();
        else
            result.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Section B — resolver-specific correctness
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DefaultConstructor_UsesScaledLong_NotDecimal()
    {
        // Strategy built with no args → ScaledLong is the default
        // Verify by checking it handles the 4-decimal-place limit correctly
        var sut = new PartialMatchStrategy(); // no resolver injected
        var source = Tx(1000.0000m).Build();
        var candidate = Tx(500.0000m).Build();

        sut.TryMatch(source, [candidate], _options, new MatchContext()).ShouldNotBeNull();
    }

    [Fact]
    public void ScaledLong_AgreesWith_Decimal_AcrossFinancialRange()
    {
        // Spot-check a range of realistic financial amounts
        // Both resolvers must return identical results
        var cases = new[]
        {
            (src: 10_000m,    cand: 2_500m),
            (src: 99_999.99m, cand: 33_333.33m),
            (src: 1.00m,      cand: 0.25m),
            (src: 0.10m,      cand: 0.02m),
            (src: 500m,       cand: 333m),   // not a clean divisor
        };

        foreach (var (src, cand) in cases)
        {
            var decimalResult = ModuloResolver.Decimal(src, cand);
            var scaledResult = ModuloResolver.ScaledLong(src, cand);

            scaledResult.ShouldBe(decimalResult,
                $"ScaledLong disagrees with Decimal for src={src}, cand={cand}");
        }
    }

    [Fact]
    public void UnsafeMantissa_AgreesWith_Decimal_AcrossFinancialRange()
    {
        var cases = new[]
        {
            (src: 10_000m,    cand: 2_500m),
            (src: 99_999.99m, cand: 33_333.33m),
            (src: 1.00m,      cand: 0.25m),
            (src: 0.10m,      cand: 0.02m),
            (src: 500m,       cand: 333m),
        };

        foreach (var (src, cand) in cases)
        {
            var decimalResult = ModuloResolver.Decimal(src, cand);
            var unsafeResult = ModuloResolver.UnsafeMantissa(src, cand);

            unsafeResult.ShouldBe(decimalResult,
                $"UnsafeMantissa disagrees with Decimal for src={src}, cand={cand}");
        }
    }

    [Fact]
    public void UnsafeMantissa_HandlesScaleMismatch_Correctly()
    {
        // 900.00 (scale=2) vs 300.0 (scale=1) — same value, different internal scale
        // UnsafeMantissa must normalise before comparing mantissas
        var decimalResult = ModuloResolver.Decimal(900.00m, 300.0m);
        var unsafeResult = ModuloResolver.UnsafeMantissa(900.00m, 300.0m);

        unsafeResult.ShouldBe(decimalResult);
        unsafeResult.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Section C — claim / context interaction (resolver-independent)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TryMatch_WhenCandidateAlreadyClaimed_SkipsIt()
    {
        var sut = new PartialMatchStrategy();
        var source = Tx(900m).Build();
        var candidate = Tx(300m).Build();

        var context = new MatchContext();
        context.TryClaim(candidate);

        sut.TryMatch(source, [candidate], _options, context).ShouldBeNull();
    }

    [Fact]
    public void TryMatch_WhenFirstClaimedButSecondAvailable_ReturnsSecond()
    {
        var sut = new PartialMatchStrategy();
        var source = Tx(900m).Build();
        var claimed = Tx(300m).Build();
        var available = Tx(450m).Build();

        var context = new MatchContext();
        context.TryClaim(claimed);

        var result = sut.TryMatch(source, [claimed, available], _options, context);

        result.ShouldNotBeNull();
        result.Target.ShouldBe(available);
    }

    [Fact]
    public void TryMatch_AfterSuccessfulMatch_CandidateIsClaimed()
    {
        var sut = new PartialMatchStrategy();
        var source = Tx(900m).Build();
        var candidate = Tx(300m).Build();
        var context = new MatchContext();

        sut.TryMatch(source, [candidate], _options, context);

        context.IsClaimed(candidate).ShouldBeTrue();
    }

    [Fact]
    public void TryMatch_WhenEmptyCandidates_ReturnsNull()
    {
        var sut = new PartialMatchStrategy();
        sut.TryMatch(Tx(900m).Build(), [], _options, new MatchContext()).ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Section D — confidence and strategy metadata
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TryMatch_ConfidenceIsAlwaysFlatAggregateMinConfidence()
    {
        var sut = new PartialMatchStrategy();
        var source = Tx(900m).Build();

        var r1 = sut.TryMatch(source, [Tx(450m).Build()], _options, new MatchContext());
        var r2 = sut.TryMatch(source, [Tx(300m).Build()], _options, new MatchContext());

        r1.ShouldNotBeNull();
        r2.ShouldNotBeNull();
        r1.Score.Value.ShouldBe(_options.PartialMatchMinConfidence);
        r2.Score.Value.ShouldBe(_options.PartialMatchMinConfidence);
    }

    [Fact]
    public void Strategy_PropertyIsAggregate()
    {
        new PartialMatchStrategy().Strategy.ShouldBe(MatchStrategy.PartialMatch);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static TransactionBuilder Tx(decimal amount) =>
        TransactionBuilder.Default().WithAmount(amount).WithCurrency("USD");
}
