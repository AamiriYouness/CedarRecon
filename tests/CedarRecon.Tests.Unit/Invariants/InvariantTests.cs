using CedarRecon.Classification;
using CedarRecon.Core.Enums;
using CedarRecon.Indexing;
using CedarRecon.Reference;
using CedarRecon.Tests.Unit.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CedarRecon.Tests.Unit.Invariants;

/// <summary>
/// Property-style invariant tests — must hold for ANY input, unlike golden
/// files (which capture today's specific output and are expected to change
/// deliberately, e.g. once ReconQL reshapes matching in v1.0). These
/// invariants should hold regardless of which strategy/algorithm produces
/// the result — that's what makes them invariants rather than golden data.
/// </summary>
public class InvariantTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(99)]
    public async Task NoDoubleMatch_EveryTransactionClaimedAtMostOnce(int seed)
    {
        var (source, target) = ScenarioBuilder.BuildRaw(2_000, seed);

        var matches = await MatchingTestHelper.RunMatching(source, target);

        var sourceIds = matches.Select(m => m.Source.Id.Value).ToList();
        var targetIds = matches.Select(m => m.Target.Id.Value).ToList();

        sourceIds.ShouldBe(sourceIds.Distinct(), ignoreOrder: true);
        targetIds.ShouldBe(targetIds.Distinct(), ignoreOrder: true);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public async Task Conservation_MatchedPlusExceptionsEqualsTotalRecords(int seed)
    {
        // Composes matching + classification manually — this is a test-only
        // concern, not a production pipeline (no ReconciliationPipeline
        // orchestrator exists yet; building one is out of scope until
        // ReconQL/JobDefinition land in v1.0 — see docs/rfcs and ROADMAP).
        var (source, target) = ScenarioBuilder.BuildRaw(2_000, seed);
        var totalRecords = source.Count + target.Count;

        var matches = await MatchingTestHelper.RunMatching(source, target);

        var matchedSourceIds = matches.Select(m => m.Source.Id.Value).ToHashSet();
        var matchedTargetIds = matches.Select(m => m.Target.Id.Value).ToHashSet();

        var unmatchedSource = source.Where(t => !matchedSourceIds.Contains(t.Id.Value)).ToList();
        var unmatchedTarget = target.Where(t => !matchedTargetIds.Contains(t.Id.Value)).ToList();

        var classifier = new ColumnarExceptionClassifier(NullLogger<ColumnarExceptionClassifier>.Instance);
        var exceptions = classifier.Classify(unmatchedSource, unmatchedTarget, matches);

        // Each MatchedPair accounts for exactly 2 records (one source + one
        // target leg). Every record not claimed by a match must appear
        // exactly once in the classification output.
        (matches.Count * 2 + exceptions.Count).ShouldBe(totalRecords);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public async Task EveryRecord_HasTerminalProcessingState(int seed)
    {
        // ClassifyColumnar's own ToDiscrepancyType already throws
        // InvalidOperationException if any row is still ClassificationStateBytes.None
        // at enumeration time — so this invariant is already enforced by
        // production code on every call. This test asserts it directly and
        // explicitly rather than relying on "didn't throw" as an implicit
        // signal, and documents the invariant as a named, intentional check.
        var (source, target) = ScenarioBuilder.BuildRaw(2_000, seed);

        var matches = await MatchingTestHelper.RunMatching(source, target);
        var matchedSourceIds = matches.Select(m => m.Source.Id.Value).ToHashSet();
        var matchedTargetIds = matches.Select(m => m.Target.Id.Value).ToHashSet();
        var unmatchedSource = source.Where(t => !matchedSourceIds.Contains(t.Id.Value)).ToList();
        var unmatchedTarget = target.Where(t => !matchedTargetIds.Contains(t.Id.Value)).ToList();

        var classifier = new ColumnarExceptionClassifier(NullLogger<ColumnarExceptionClassifier>.Instance);
        var result = classifier.ClassifyColumnar(unmatchedSource, unmatchedTarget, matches);

        result.Source.ProcessingState.ShouldAllBe(state => state != ClassificationStateBytes.None);
        result.Target.ProcessingState.ShouldAllBe(state => state != ClassificationStateBytes.None);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public async Task ToleranceBalance_GroupSumsBalanceWithinDeclaredTolerance(int seed)
    {
        var (source, target) = ScenarioBuilder.BuildRaw(2_000, seed);
        var options = MatchingTestHelper.DefaultOptions;

        var matches = await MatchingTestHelper.RunMatching(source, target, options);

        // HashMatchingEngine.Match() returns AT MOST ONE MatchResult per
        // source transaction — a single source cannot currently claim
        // multiple target legs (or vice versa) in one call. Full split/
        // consolidated group resolution is a v1.0 capability (Aggregate
        // matching, per the roadmap) — today, PartialMatchStrategy claims
        // exactly one leg and leaves the remainder unclaimed, which the
        // classification cascade's own SplitPayment/ConsolidatedPayment
        // categories exist specifically to catch. So this invariant only
        // applies to groups the engine actually guarantees full resolution
        // for today: single-pair groups (Exact/Fuzzy), not multi-leg
        // Partial groups.
        foreach (var group in matches.GroupBy(m => m.Source.NormalizedReference.Value))
        {
            var pairs = group.ToList();
            if (pairs.Count > 1 || pairs[0].Strategy == MatchStrategy.PartialMatch)
                continue; // multi-leg resolution not guaranteed until Aggregate matching (v1.0)

            var pair = pairs[0];
            options.DefaultToleranceRule
                .IsAmountWithinTolerance(pair.Source.Amount.Amount, pair.Target.Amount.Amount)
                .ShouldBeTrue(
                    $"Group {group.Key}: source={pair.Source.Amount.Amount}, " +
                    $"target={pair.Target.Amount.Amount}, strategy={pair.Strategy}");
        }
    }

    [Fact]
    public void ReferenceInterner_RoundTrip_InternThenResolveIsIdentity()
    {
        var interner = new ReferenceInterner();

        string[] references =
        [
            "SIMPLE123",
            "",                          // empty string
            "WITH-DASH-AND-SLASH/123",   // characters that get stripped elsewhere (NormalizeReference), NOT here — interner is a raw string->int map, doesn't normalize
            "ünïcödé-Référence",         // unicode
            new string('X', 10_000),     // very long
        ];

        var ids = references.Select(r => interner.GetOrAdd(r)).ToList();

        for (var i = 0; i < references.Length; i++)
            interner.GetValue(ids[i]).ShouldBe(references[i]);
    }

    [Fact]
    public void ReferenceInterner_SameReferenceInternedTwice_ReturnsSameId()
    {
        var interner = new ReferenceInterner();

        var id1 = interner.GetOrAdd("REF-A");
        var id2 = interner.GetOrAdd("REF-A");

        id1.ShouldBe(id2);
        interner.Count.ShouldBe(1);
    }

    [Fact]
    public void ReferenceInterner_TryGetId_UnseenReference_ReturnsFalse()
    {
        var interner = new ReferenceInterner();
        interner.GetOrAdd("SEEN");

        interner.TryGetId("UNSEEN", out _).ShouldBeFalse();
        interner.TryGetId("SEEN", out var id).ShouldBeTrue();
        id.ShouldBe(0);
    }
}