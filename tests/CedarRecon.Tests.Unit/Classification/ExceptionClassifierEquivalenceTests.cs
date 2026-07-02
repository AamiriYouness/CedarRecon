using CedarRecon.Application.Classification;
using CedarRecon.Application.Classification.Indexed;
using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Enums;
using CedarRecon.Domain.ValueObjects;
using CedarRecon.Tests.Unit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CedarRecon.Tests.Unit.Classification;

/// <summary>
/// Proves IndexedExceptionClassifier produces output IDENTICAL to the
/// existing Dictionary-based ExceptionClassifier, for the same input.
///
/// This is the actual gate for trusting the indexed engine. A faster wrong
/// answer is worse than a slower right one — none of the benchmark numbers
/// for the indexed engine matter until every test in this file passes.
///
/// Sections:
///   A — Every existing cascade scenario from ExceptionClassifierTests,
///       re-run through both classifiers, asserting identical output
///   B — Equivalence at scale via ScenarioBuilder (realistic mixed distribution)
///   C — Adversarial / edge-case equivalence
///   D — Property-style: many random seeds, every one must agree
/// </summary>
public class ExceptionClassifierEquivalenceTests
{
    private static readonly DateTimeOffset BaseDate =
        new(2024, 3, 15, 0, 0, 0, TimeSpan.Zero);

    private static ExceptionClassifier Dictionary() =>
        new(NullLogger<ExceptionClassifier>.Instance,
            new ClassifierOptions { DegreeOfParallelism = 1 }); // sequential — isolates logic, not parallel merge

    private static ColumnarExceptionClassifier Columnar() =>
        new(NullLogger<ColumnarExceptionClassifier>.Instance);

    /// <summary>
    /// Runs both classifiers on the same input and asserts the result SETS
    /// are identical — (TransactionId, DiscrepancyType) pairs, order-independent
    /// since neither classifier guarantees output order.
    /// </summary>
    private static void AssertEquivalent(
        IReadOnlyList<Transaction> unmatchedSource,
        IReadOnlyList<Transaction> unmatchedTarget,
        IReadOnlyList<MatchedPair> matchedPairs)
    {
        var dictResult = Dictionary().Classify(unmatchedSource, unmatchedTarget, matchedPairs);
        var columnarResult = Columnar().Classify(unmatchedSource, unmatchedTarget, matchedPairs);

        columnarResult.Count.ShouldBe(dictResult.Count,
            "Total classified count differs between classifiers.");

        var dictSet = dictResult.Select(r => (r.Transaction.Id.Value, r.Reason)).ToHashSet();
        var columnarSet = columnarResult.Select(r => (r.Transaction.Id.Value, r.Reason)).ToHashSet();

        dictSet.ShouldBeSubsetOf(columnarSet);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Section A — Every cascade scenario from ExceptionClassifierTests
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Equivalent_DuplicateInSource_TwoSameReference()
    {
        var s1 = Source("REF-A", 100m);
        var s2 = Source("REF-A", 100m);

        AssertEquivalent([s1, s2], [], []);
    }

    [Fact]
    public void Equivalent_DuplicateInSource_ThreeSameReference()
    {
        var txs = new[] { Source("REF-A", 100m), Source("REF-A", 200m), Source("REF-A", 300m) };
        AssertEquivalent(txs, [], []);
    }

    [Fact]
    public void Equivalent_DuplicateInTarget_TwoSameReference()
    {
        var t1 = Target("REF-A", 100m);
        var t2 = Target("REF-A", 100m);

        AssertEquivalent([], [t1, t2], []);
    }

    [Fact]
    public void Equivalent_SplitPayment_PureUnmatched()
    {
        var source = Source("REF-A", 900m);
        var target1 = Target("REF-A", 300m);
        var target2 = Target("REF-A", 600m);

        AssertEquivalent([source], [target1, target2], []);
    }

    [Fact]
    public void Equivalent_SplitPayment_SiblingMatchedElsewhere_RemainingTargetIsMissingInSource()
    {
        var matchedSource = Source("REF-A", 300m);
        var matchedTarget = Target("REF-A", 300m);
        var unmatchedTarget = Target("REF-A", 600m);

        var pair = new MatchedPair(matchedSource, matchedTarget,
            ConfidenceScore.Of(1.0m), MatchStrategy.Exact);

        AssertEquivalent([], [unmatchedTarget], [pair]);
    }

    [Fact]
    public void Equivalent_ConsolidatedPayment_PureUnmatched()
    {
        var source1 = Source("REF-A", 300m);
        var source2 = Source("REF-A", 600m);
        var target = Target("REF-A", 900m);

        AssertEquivalent([source1, source2], [target], []);
    }

    [Fact]
    public void Equivalent_ConsolidatedPayment_WithMatchedSibling()
    {
        var matchedSource = Source("REF-A", 300m);
        var matchedTarget = Target("REF-A", 300m);
        var unmatchedSource = Source("REF-A", 600m);

        var pair = new MatchedPair(matchedSource, matchedTarget,
            ConfidenceScore.Of(1.0m), MatchStrategy.Exact);

        AssertEquivalent([unmatchedSource], [], [pair]);
    }

    [Fact]
    public void Equivalent_AmountMismatch_SingleRefBothSides()
    {
        var source = Source("REF-A", 100m);
        var target = Target("REF-A", 150m);

        AssertEquivalent([source], [target], []);
    }

    [Fact]
    public void Equivalent_AmountAndDateBothDiffer_AmountTakesPriority()
    {
        var source = Source("REF-A", 100m, BaseDate);
        var target = Target("REF-A", 150m, BaseDate.AddDays(5));

        AssertEquivalent([source], [target], []);
    }

    [Fact]
    public void Equivalent_DateMismatch_SameAmountDifferentDate()
    {
        var source = Source("REF-A", 100m, BaseDate);
        var target = Target("REF-A", 100m, BaseDate.AddDays(5));

        AssertEquivalent([source], [target], []);
    }

    [Fact]
    public void Equivalent_SameUtcDayDifferentTime_NotDateMismatch()
    {
        var source = Source("REF-A", 100m, new DateTimeOffset(2024, 3, 15, 9, 0, 0, TimeSpan.Zero));
        var target = Target("REF-A", 100m, new DateTimeOffset(2024, 3, 15, 23, 0, 0, TimeSpan.Zero));

        AssertEquivalent([source], [target], []);
    }

    [Fact]
    public void Equivalent_MissingInTarget_SingleSource()
    {
        AssertEquivalent([Source("REF-GHOST", 100m)], [], []);
    }

    [Fact]
    public void Equivalent_MissingInSource_SingleTarget()
    {
        AssertEquivalent([], [Target("REF-GHOST", 100m)], []);
    }

    [Fact]
    public void Equivalent_RealisticMixedScenario_AllEightTypesPresent()
    {
        var matchedSrc = Source("REF-OK", 500m);
        var matchedTgt = Target("REF-OK", 500m);
        var pair = new MatchedPair(matchedSrc, matchedTgt, ConfidenceScore.Of(1.0m), MatchStrategy.Exact);

        var dupSrc1 = Source("REF-DUP", 100m);
        var dupSrc2 = Source("REF-DUP", 100m);

        var splitSrc = Source("REF-SPLIT", 1000m);
        var splitTgt1 = Target("REF-SPLIT", 400m);
        var splitTgt2 = Target("REF-SPLIT", 600m);

        var amtSrc = Source("REF-AMT", 100m);
        var amtTgt = Target("REF-AMT", 150m);

        var dateSrc = Source("REF-DATE", 200m, BaseDate);
        var dateTgt = Target("REF-DATE", 200m, BaseDate.AddDays(5));

        var missingInTgt = Source("REF-GHOST-SRC", 300m);
        var missingInSrc = Target("REF-GHOST-TGT", 400m);

        var unmatchedSources = new[] { dupSrc1, dupSrc2, splitSrc, amtSrc, dateSrc, missingInTgt };
        var unmatchedTargets = new[] { splitTgt1, splitTgt2, amtTgt, dateTgt, missingInSrc };

        AssertEquivalent(unmatchedSources, unmatchedTargets, [pair]);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Section B — Equivalence at scale
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(100)]
    [InlineData(5_000)]
    [InlineData(15_000)] // crosses ExceptionClassifier's parallel threshold too, if DoP > 1 were used
    public void Equivalent_AtScale_RealisticDistribution(int totalReferences)
    {
        var (source, target, matched) = ScenarioBuilder.Build(totalReferences, seed: 123);

        AssertEquivalent(source, target, matched);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Section C — Adversarial / edge cases
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Equivalent_AllEmpty()
    {
        AssertEquivalent([], [], []);
    }

    [Fact]
    public void Equivalent_OnlyMatchedPairs_NoUnmatchedAnywhere()
    {
        var pair = new MatchedPair(
            Source("REF-A", 100m), Target("REF-A", 100m),
            ConfidenceScore.Of(1.0m), MatchStrategy.Exact);

        AssertEquivalent([], [], [pair]);
    }

    [Fact]
    public void Equivalent_SingleReferenceManyDuplicatesBothSides()
    {
        // 10 source + 10 target, all same reference — stresses the duplicate
        // phase's group-range iteration with a large single group.
        var sources = Enumerable.Range(0, 10).Select(i => Source("REF-A", 100m + i)).ToList();
        var targets = Enumerable.Range(0, 10).Select(i => Target("REF-A", 200m + i)).ToList();

        AssertEquivalent(sources, targets, []);
    }

    [Fact]
    public void Equivalent_ManyDistinctSingleTransactionReferences()
    {
        // Every transaction has its own unique reference — exercises the
        // "every group has SourceCount==1, TargetCount==0" path heavily.
        var sources = Enumerable.Range(0, 200)
            .Select(i => Source($"REF-{i:D6}", 100m + i))
            .ToList();

        AssertEquivalent(sources, [], []);
    }

    [Fact]
    public void Equivalent_ZeroAndNegativeAmounts()
    {
        var source = Source("REF-A", 0m);
        var target = Target("REF-A", -50m);

        AssertEquivalent([source], [target], []);
    }

    [Fact]
    public void Equivalent_LargeSplitGroup_TenTargetLegs()
    {
        var source = Source("REF-A", 1000m);
        var targets = Enumerable.Range(0, 10)
            .Select(i => Target("REF-A", 100m + i))
            .ToList();

        AssertEquivalent([source], targets, []);
    }

    [Fact]
    public void Equivalent_LargeConsolidatedGroup_TenSourceLegs()
    {
        var sources = Enumerable.Range(0, 10)
            .Select(i => Source("REF-A", 100m + i))
            .ToList();
        var target = Target("REF-A", 1000m);

        AssertEquivalent(sources, [target], []);
    }

    [Fact]
    public void Equivalent_MultipleReferencesEachWithDifferentCascadeOutcome()
    {
        // Forces the index/dictionary builders to interleave many distinct
        // group shapes in a single Classify() call — duplicates, splits,
        // consolidations, and plain mismatches all coexisting.
        var sources = new List<Transaction>();
        var targets = new List<Transaction>();

        for (var i = 0; i < 20; i++)
        {
            switch (i % 4)
            {
                case 0: // duplicate pair
                    sources.Add(Source($"DUP-{i}", 100m));
                    sources.Add(Source($"DUP-{i}", 100m));
                    break;
                case 1: // split
                    sources.Add(Source($"SPLIT-{i}", 900m));
                    targets.Add(Target($"SPLIT-{i}", 300m));
                    targets.Add(Target($"SPLIT-{i}", 600m));
                    break;
                case 2: // consolidated
                    sources.Add(Source($"CONSOL-{i}", 300m));
                    sources.Add(Source($"CONSOL-{i}", 600m));
                    targets.Add(Target($"CONSOL-{i}", 900m));
                    break;
                case 3: // plain amount mismatch
                    sources.Add(Source($"MISMATCH-{i}", 100m));
                    targets.Add(Target($"MISMATCH-{i}", 150m));
                    break;
            }
        }

        AssertEquivalent(sources, targets, []);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Section D — Property-style: many random seeds, every one must agree
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(100)]
    [InlineData(9999)]
    public void Equivalent_AcrossManySeeds_AlwaysAgree(int seed)
    {
        var (source, target, matched) = ScenarioBuilder.Build(totalReferences: 2_000, seed: seed);

        AssertEquivalent(source, target, matched);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Transaction Source(string reference, decimal amount, DateTimeOffset? date = null) =>
        TransactionBuilder.Default()
            .WithReference(reference).WithAmount(amount).WithCurrency("USD")
            .WithValueDate(date ?? BaseDate).WithSourceFileName("source.csv").Build();

    private static Transaction Target(string reference, decimal amount, DateTimeOffset? date = null) =>
        TransactionBuilder.Default()
            .WithReference(reference).WithAmount(amount).WithCurrency("USD")
            .WithValueDate(date ?? BaseDate).WithSourceFileName("target.csv").Build();
}
