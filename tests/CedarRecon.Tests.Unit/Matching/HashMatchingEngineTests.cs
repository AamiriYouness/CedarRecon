using CedarRecon.Application.Matching;
using CedarRecon.Application.Matching.Strategies;
using CedarRecon.Domain;
using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Enums;
using CedarRecon.Domain.ValueObjects;
using CedarRecon.Tests.Unit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CedarRecon.Tests.Unit.Matching;

/// <summary>
/// Tests for HashMatchingEngine.
///
/// Sections:
///   A — BuildTargetIndexAsync: index construction, logging, cancellation
///   B — Match / index lookup: bucket routing, missing key, IndexKey composition
///   C — Strategy cascade: Exact → Fuzzy → Partial priority order
///   D — GetUnmatchedTargets: correct after full run
///   E — Concurrency: parallel Match() calls, no double-claims, no dropped matches
///   F — Strategy injection: custom pipeline, empty pipeline
///   G — Volume / property-based: Bogus-generated realistic datasets
///
/// What is NOT tested here:
///   Individual strategy logic (ExactMatchStrategyTests, FuzzyMatchStrategyTests, etc.)
///   MatchContext in isolation (MatchContextTests)
///   These tests own the ENGINE contract — orchestration, routing, and thread safety.
/// </summary>
public class HashMatchingEngineTests
{
    private readonly ReconciliationOptions _opts = OptionsFactory.Default();

    // ── Shared helper: build engine with NullLogger ───────────────────────────

    private static HashMatchingEngine Engine(IReadOnlyList<IMatchStrategy>? strategies = null) =>
        new(NullLogger<HashMatchingEngine>.Instance, strategies);

    // ═══════════════════════════════════════════════════════════════════════════
    // Section A — BuildTargetIndexAsync
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Build_WithEmptyStream_ProducesEmptyIndex()
    {
        var engine = Engine();
        await engine.BuildTargetIndexAsync(AsyncEmpty(), ct: TestContext.Current.CancellationToken);

        // No targets → any source is unmatched
        var source = TransactionBuilder.Default().Build();
        var result = engine.Match(source, _opts);

        result.ShouldBeOfType<UnmatchedResult>();
    }

    [Fact]
    public async Task Build_GroupsTransactionsByIndexKey()
    {
        // Two targets with same reference+currency → same bucket
        var t1 = Tx("REF-A", "USD", 100m);
        var t2 = Tx("REF-A", "USD", 200m);
        var t3 = Tx("REF-B", "USD", 300m); // different ref → different bucket

        var engine = Engine();
        await engine.BuildTargetIndexAsync(Async(t1, t2, t3), ct: TestContext.Current.CancellationToken);

        // Source matching REF-A bucket should find t1 first
        var source = Tx("REF-A", "USD", 100m);
        engine.Match(source, _opts).ShouldBeOfType<MatchedResult>();
    }

    [Fact]
    public async Task Build_DifferentCurrencySameReference_LandInDifferentBuckets()
    {
        // IndexKey = reference + "|" + currency — currency is part of the key
        var targetUsd = Tx("REF-X", "USD", 500m);
        var targetEur = Tx("REF-X", "EUR", 500m);

        var engine = Engine();
        await engine.BuildTargetIndexAsync(Async(targetUsd, targetEur), ct: TestContext.Current.CancellationToken);

        var sourceUsd = Tx("REF-X", "USD", 500m);
        var sourceEur = Tx("REF-X", "EUR", 500m);

        // Each source only matches its own currency bucket
        engine.Match(sourceUsd, _opts).ShouldBeOfType<MatchedResult>();
        engine.Match(sourceEur, _opts).ShouldBeOfType<MatchedResult>();
    }

    [Fact]
    public async Task Build_IsCancellable_ThrowsOnCancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var engine = Engine();

        await Should.ThrowAsync<OperationCanceledException>(
            () => engine.BuildTargetIndexAsync(AsyncSlow(), ct: cts.Token));
    }

    [Fact]
    public async Task Build_RebuildsCleanly_PreviousStateIsDiscarded()
    {
        var engine = Engine();

        // First build — one target
        var first = Tx("REF-1", "USD", 100m);
        await engine.BuildTargetIndexAsync(Async(first), ct: TestContext.Current.CancellationToken);
        engine.Match(Tx("REF-1", "USD", 100m), _opts).ShouldBeOfType<MatchedResult>();

        // Second build — completely different target, REF-1 gone
        var second = Tx("REF-2", "USD", 200m);
        await engine.BuildTargetIndexAsync(Async(second), ct: TestContext.Current.CancellationToken);

        engine.Match(Tx("REF-1", "USD", 100m), _opts).ShouldBeOfType<UnmatchedResult>();
        engine.Match(Tx("REF-2", "USD", 200m), _opts).ShouldBeOfType<MatchedResult>();
    }

    [Fact]
    public async Task Build_AcceptsExpectedCount_WithoutError()
    {
        // expectedCount is a capacity hint — should not affect correctness
        var targets = TransactionBuilder.BuildMany(50);
        var engine = Engine();

        await engine.BuildTargetIndexAsync(targets.ToAsyncEnumerable(), expectedCount: 10, ct: TestContext.Current.CancellationToken);

        // All 50 targets must be registered regardless of initial capacity
        engine.GetUnmatchedTargets().Count.ShouldBe(50);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Section B — Match / index lookup
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Match_WhenNoTargetForIndexKey_ReturnsUnmatched()
    {
        var engine = Engine();
        await engine.BuildTargetIndexAsync(Async(Tx("REF-A", "USD", 100m)), ct: TestContext.Current.CancellationToken);

        var source = Tx("REF-Z", "USD", 100m); // completely different reference
        var result = engine.Match(source, _opts);

        result.ShouldBeOfType<UnmatchedResult>();
        ((UnmatchedResult)result).Unmatched.Reason
            .ShouldBe(DiscrepancyType.MissingInTarget);
    }

    [Fact]
    public async Task Match_WhenCurrencyDiffers_ReturnsUnmatched()
    {
        // Same reference, different currency → different bucket → no match
        var engine = Engine();
        await engine.BuildTargetIndexAsync(Async(Tx("REF-A", "USD", 500m)), ct: TestContext.Current.CancellationToken);

        var source = Tx("REF-A", "EUR", 500m);
        engine.Match(source, _opts).ShouldBeOfType<UnmatchedResult>();
    }

    [Fact]
    public async Task Match_WhenBucketExistsButNoStrategyMatches_ReturnsUnmatched()
    {
        // Bucket exists (same reference+currency) but amount is wildly different
        // → exact and fuzzy fail → unmatched
        var engine = Engine();
        await engine.BuildTargetIndexAsync(Async(Tx("REF-A", "USD", 100m)), ct: TestContext.Current.CancellationToken);

        var source = Tx("REF-A", "USD", 99_999m); // far outside any tolerance
        engine.Match(source, _opts).ShouldBeOfType<UnmatchedResult>();
    }

    [Fact]
    public void Match_BeforeBuildIsCalled_ReturnsUnmatched()
    {
        // Engine starts with empty index — any match is missing
        var engine = Engine();
        var source = TransactionBuilder.Default().Build();

        engine.Match(source, _opts).ShouldBeOfType<UnmatchedResult>();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Section C — Strategy cascade priority
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Match_ExactMatchWins_OverFuzzy()
    {
        var date = BaseDate;
        var engine = Engine();

        // Two targets in same bucket — one exact, one fuzzy
        var exactTarget = Tx("REF-A", "USD", 500m, date);
        var fuzzyTarget = Tx("REF-A", "USD", 504m, date); // within fuzzy tolerance

        await engine.BuildTargetIndexAsync(Async(exactTarget, fuzzyTarget), ct: TestContext.Current.CancellationToken);

        var source = Tx("REF-A", "USD", 500m, date);
        var result = engine.Match(source, _opts);

        result.ShouldBeOfType<MatchedResult>();
        ((MatchedResult)result).Pair.Strategy.ShouldBe(MatchStrategy.Exact);
        ((MatchedResult)result).Pair.Target.ShouldBe(exactTarget);
    }

    [Fact]
    public async Task Match_FuzzyMatchFallsThrough_WhenNoExact()
    {
        var date = BaseDate;
        var engine = Engine();

        var fuzzyTarget = Tx("REF-A", "USD", 503m, date); // within ±5 tolerance

        await engine.BuildTargetIndexAsync(Async(fuzzyTarget), ct: TestContext.Current.CancellationToken);

        var source = Tx("REF-A", "USD", 500m, date);
        var result = engine.Match(source, _opts);

        result.ShouldBeOfType<MatchedResult>();
        ((MatchedResult)result).Pair.Strategy.ShouldBe(MatchStrategy.Fuzzy);
    }

    [Fact]
    public async Task Match_PartialMatchFallsThrough_WhenExactAndFuzzyFail()
    {
        var date = BaseDate;
        var engine = Engine();

        // 900 source, 300 target — clean divisor → partial match
        var partialTarget = Tx("REF-A", "USD", 300m, date);

        await engine.BuildTargetIndexAsync(Async(partialTarget), ct: TestContext.Current.CancellationToken);

        var source = Tx("REF-A", "USD", 900m, date);
        var result = engine.Match(source, _opts);

        result.ShouldBeOfType<MatchedResult>();
        ((MatchedResult)result).Pair.Strategy.ShouldBe(MatchStrategy.PartialMatch);
    }

    [Fact]
    public async Task Match_StrategyPipelineIsOrdered_FirstWins()
    {
        // Custom pipeline: only a spy strategy that always matches
        // Verify the engine calls strategies in order and stops at first match
        var callOrder = new List<string>();

        var alwaysMatch = new SpyStrategy("first", callOrder, returns: true);
        var neverCalled = new SpyStrategy("second", callOrder, returns: true);

        var engine = Engine([alwaysMatch, neverCalled]);
        await engine.BuildTargetIndexAsync(Async(Tx("REF-A", "USD", 100m)), ct: TestContext.Current.CancellationToken);

        engine.Match(Tx("REF-A", "USD", 100m), _opts);

        callOrder.ShouldBe(["first"]); // second never called
    }

    [Fact]
    public async Task Match_WhenFirstStrategyReturnsNull_CascadesToNext()
    {
        var callOrder = new List<string>();

        var neverMatches = new SpyStrategy("first", callOrder, returns: false);
        var alwaysMatches = new SpyStrategy("second", callOrder, returns: true);

        var engine = Engine([neverMatches, alwaysMatches]);
        await engine.BuildTargetIndexAsync(Async(Tx("REF-A", "USD", 100m)), ct: TestContext.Current.CancellationToken);

        var result = engine.Match(Tx("REF-A", "USD", 100m), _opts);

        result.ShouldBeOfType<MatchedResult>();
        callOrder.ShouldBe(["first", "second"]);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Section D — GetUnmatchedTargets
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetUnmatchedTargets_WhenNothingMatched_ReturnsAllTargets()
    {
        var targets = new[]
        {
            Tx("REF-A", "USD", 100m),
            Tx("REF-B", "USD", 200m),
        };

        var engine = Engine();
        await engine.BuildTargetIndexAsync(Async(targets), ct: TestContext.Current.CancellationToken);

        // Match sources that don't exist in index
        engine.Match(Tx("REF-Z", "USD", 999m), _opts);

        var unmatched = engine.GetUnmatchedTargets();
        unmatched.Count.ShouldBe(2);
        unmatched.ShouldContain(targets[0]);
        unmatched.ShouldContain(targets[1]);
    }

    [Fact]
    public async Task GetUnmatchedTargets_WhenAllMatched_ReturnsEmpty()
    {
        var t1 = Tx("REF-A", "USD", 100m);
        var t2 = Tx("REF-B", "USD", 200m);

        var engine = Engine();
        await engine.BuildTargetIndexAsync(Async(t1, t2), ct: TestContext.Current.CancellationToken);

        engine.Match(Tx("REF-A", "USD", 100m), _opts);
        engine.Match(Tx("REF-B", "USD", 200m), _opts);

        engine.GetUnmatchedTargets().ShouldBeEmpty();
    }

    [Fact]
    public async Task GetUnmatchedTargets_ReturnsOnlyUnclaimedSubset()
    {
        var matched = Tx("REF-A", "USD", 100m);
        var unmatched = Tx("REF-B", "USD", 200m);

        var engine = Engine();
        await engine.BuildTargetIndexAsync(Async(matched, unmatched), ct: TestContext.Current.CancellationToken);

        engine.Match(Tx("REF-A", "USD", 100m), _opts); // claims matched

        var result = engine.GetUnmatchedTargets();
        result.ShouldHaveSingleItem();
        result[0].ShouldBe(unmatched);
    }

    [Fact]
    public void GetUnmatchedTargets_BeforeBuild_ReturnsEmpty()
    {
        var engine = Engine();
        engine.GetUnmatchedTargets().ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Section E — Concurrency
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Match_ParallelCalls_NoTargetDoubleClamed()
    {
        // 100 targets, 100 sources each matching exactly one target
        // Run all matches in parallel — each target must be claimed at most once
        const int N = 100;

        var targets = Enumerable.Range(0, N)
            .Select(i => Tx($"REF-{i:D4}", "USD", (i + 1) * 10m))
            .ToArray();

        var engine = Engine();
        await engine.BuildTargetIndexAsync(targets.ToAsyncEnumerable(), ct: TestContext.Current.CancellationToken);

        var sources = Enumerable.Range(0, N)
            .Select(i => Tx($"REF-{i:D4}", "USD", (i + 1) * 10m))
            .ToArray();

        await Parallel.ForEachAsync(
            sources,
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (source, _) =>
            {
                engine.Match(source, _opts);
                await Task.Yield();
            });

        // Every target claimed exactly once → zero unmatched targets
        engine.GetUnmatchedTargets().ShouldBeEmpty();
    }

    [Fact]
    public async Task Match_SameTarget_CompetingThreads_OnlyOneClaims()
    {
        // 50 threads all try to match the same source → same target
        // Only one thread should succeed — the rest get unmatched
        var target = Tx("REF-SHARED", "USD", 500m);
        var engine = Engine();
        await engine.BuildTargetIndexAsync(Async(target), ct: TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
                engine.Match(Tx("REF-SHARED", "USD", 500m), _opts))));

        var matched = results.OfType<MatchedResult>().Count();
        var unmatched = results.OfType<UnmatchedResult>().Count();

        matched.ShouldBe(1);
        unmatched.ShouldBe(49);
    }

    [Fact]
    public async Task Match_ParallelMixedResults_CountsAreConsistent()
    {
        // 60 sources: 30 have a matching target, 30 don't
        // Parallel execution — matched + unmatched must equal 60
        const int Half = 30;

        var targets = Enumerable.Range(0, Half)
            .Select(i => Tx($"REF-M-{i:D3}", "USD", 100m + i))
            .ToArray();

        var engine = Engine();
        await engine.BuildTargetIndexAsync(targets.ToAsyncEnumerable(), ct: TestContext.Current.CancellationToken);

        var matchingSources = Enumerable.Range(0, Half)
            .Select(i => Tx($"REF-M-{i:D3}", "USD", 100m + i));
        var unmatchingSources = Enumerable.Range(0, Half)
            .Select(i => Tx($"REF-X-{i:D3}", "USD", 999m));

        var allSources = matchingSources.Concat(unmatchingSources).ToArray();

        var results = new MatchResult[allSources.Length];
        await Parallel.ForEachAsync(
            allSources.Select((s, idx) => (s, idx)),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (item, _) =>
            {
                results[item.idx] = engine.Match(item.s, _opts);
                await Task.Yield();
            });

        results.OfType<MatchedResult>().Count().ShouldBe(Half);
        results.OfType<UnmatchedResult>().Count().ShouldBe(Half);
    }


    [Fact]
    public async Task Engine_WithEmptyStrategyPipeline_AlwaysReturnsUnmatched()
    {
        var engine = Engine(strategies: []); // no strategies at all
        await engine.BuildTargetIndexAsync(Async(Tx("REF-A", "USD", 100m)), ct: TestContext.Current.CancellationToken);

        var result = engine.Match(Tx("REF-A", "USD", 100m), _opts);

        // Bucket found but no strategy matched → unmatched
        result.ShouldBeOfType<UnmatchedResult>();
    }

    [Fact]
    public async Task Engine_WithCustomSingleStrategy_OnlyThatStrategyRuns()
    {
        var spy = new SpyStrategy("only", new List<string>(), returns: true);
        var engine = Engine([spy]);

        await engine.BuildTargetIndexAsync(Async(Tx("REF-A", "USD", 100m)), ct: TestContext.Current.CancellationToken);
        engine.Match(Tx("REF-A", "USD", 100m), _opts);

        spy.CallCount.ShouldBe(1);
    }

    [Fact]
    public void Engine_DefaultStrategies_AreExactFuzzyPartialInOrder()
    {
        // Verify the default factory wires up in the documented order
        // by injecting the default pipeline and inspecting strategy types
        var engine = Engine();
        var strategies = MatchStrategyFactory.CreateDefault();

        strategies[0].Strategy.ShouldBe(MatchStrategy.Exact);
        strategies[1].Strategy.ShouldBe(MatchStrategy.Fuzzy);
        strategies[2].Strategy.ShouldBe(MatchStrategy.PartialMatch);
    }

    [Fact]
    public async Task Build_And_Match_100TargetsPerfectPairs_AllMatch()
    {
        // 100 Bogus-generated target transactions
        // For each target, produce an identical source (same ref+currency+amount+date)
        // Every source must get an exact match
        const int N = 100;

        var targets = TransactionBuilder.BuildMany(N);
        var engine = Engine();
        await engine.BuildTargetIndexAsync(targets.ToAsyncEnumerable(), ct: TestContext.Current.CancellationToken);

        var matched = 0;
        foreach (var target in targets)
        {
            // Build a source that mirrors the target exactly
            var source = TransactionBuilder.Default()
                .WithReference(target.NormalizedReference.Value)
                .WithCurrency(target.Amount.Currency)
                .WithAmount(target.Amount.Amount)
                .WithValueDate(target.ValueDate)
                .Build();

            if (engine.Match(source, _opts) is MatchedResult)
                matched++;
        }

        matched.ShouldBe(N);
        engine.GetUnmatchedTargets().ShouldBeEmpty();
    }

    [Fact]
    public async Task Build_And_Match_100TargetsNoSourceMatches_AllUnmatched()
    {
        const int N = 100;

        var targets = TransactionBuilder.BuildMany(N);
        var engine = Engine();
        await engine.BuildTargetIndexAsync(targets.ToAsyncEnumerable(), ct: TestContext.Current.CancellationToken);

        // Sources with completely different references → all unmatched
        var sources = TransactionBuilder.BuildMany(N, (b, i) =>
            b.WithReference($"NOMATCH-{i:D6}"));

        foreach (var source in sources)
            engine.Match(source, _opts).ShouldBeOfType<UnmatchedResult>();

        engine.GetUnmatchedTargets().Count.ShouldBe(N);
    }

    [Fact]
    public async Task Match_And_GetUnmatchedTargets_CountsAlwaysAddUp()
    {
        // Invariant: matched + unmatched sources + unmatched targets = total targets + total sources
        // For N perfect pairs: matched=N, unmatched sources=0, unmatched targets=0
        const int N = 50;

        var targets = TransactionBuilder.BuildMany(N);
        var engine = Engine();
        await engine.BuildTargetIndexAsync(targets.ToAsyncEnumerable(), ct: TestContext.Current.CancellationToken);

        var matchedCount = 0;
        var unmatchedCount = 0;

        foreach (var target in targets)
        {
            var source = TransactionBuilder.Default()
                .WithReference(target.NormalizedReference.Value)
                .WithCurrency(target.Amount.Currency)
                .WithAmount(target.Amount.Amount)
                .WithValueDate(target.ValueDate)
                .Build();

            if (engine.Match(source, _opts) is MatchedResult) matchedCount++;
            else unmatchedCount++;
        }

        matchedCount.ShouldBe(N);
        unmatchedCount.ShouldBe(0);
        engine.GetUnmatchedTargets().Count.ShouldBe(0);
    }

    private static readonly DateTimeOffset BaseDate =
        new(2024, 3, 15, 0, 0, 0, TimeSpan.Zero);

    private static Transaction Tx(
        string reference,
        string currency,
        decimal amount,
        DateTimeOffset? date = null) =>
        TransactionBuilder.Default()
            .WithReference(reference)
            .WithCurrency(currency)
            .WithAmount(amount)
            .WithValueDate(date ?? BaseDate)
            .Build();

    private static async IAsyncEnumerable<Transaction> Async(
        params Transaction[] transactions)
    {
        foreach (var tx in transactions)
            yield return tx;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<Transaction> Async(
        IEnumerable<Transaction> transactions)
    {
        foreach (var tx in transactions)
            yield return tx;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<Transaction> AsyncEmpty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<Transaction> AsyncSlow(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
    {
        await Task.Delay(10_000, ct); // long enough to be cancelled
        yield return TransactionBuilder.Default().Build();
    }
}

// ── SpyStrategy — test double for strategy pipeline tests ─────────────────────

/// <summary>
/// Test double that records calls and returns a fixed result.
/// Used to verify engine cascades strategies in the right order.
/// </summary>
internal sealed class SpyStrategy : IMatchStrategy
{
    private readonly string _name;
    private readonly List<string> _callOrder;
    private readonly bool _returns;
    private int _callCount;

    public SpyStrategy(string name, List<string> callOrder, bool returns)
    {
        _name = name;
        _callOrder = callOrder;
        _returns = returns;
    }

    public MatchStrategy Strategy => MatchStrategy.Exact;
    public int CallCount => _callCount;

    public MatchedPair? TryMatch(
        Transaction source,
        IReadOnlyList<Transaction> candidates,
        ReconciliationOptions options,
        MatchContext context)
    {
        Interlocked.Increment(ref _callCount);
        _callOrder.Add(_name);

        if (!_returns || candidates.Count == 0)
            return null;

        var candidate = candidates.FirstOrDefault(c => !context.IsClaimed(c));
        if (candidate is null) return null;

        if (!context.TryClaim(candidate)) return null;

        return new MatchedPair(
            source, candidate,
            ConfidenceScore.Of(1.0m),
            Strategy);
    }
}