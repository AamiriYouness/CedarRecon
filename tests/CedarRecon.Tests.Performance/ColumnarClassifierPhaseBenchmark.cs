using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using CedarRecon.Application.Classification.Indexed;
using CedarRecon.Domain.Entities;

namespace CedarRecon.Tests.Performance;

/// <summary>
/// Isolates each phase of ColumnarExceptionClassifier.ClassifyColumnar()
/// into its own benchmark.
///
/// Columnar-engine counterpart to the retired IndexedClassifierPhaseBenchmark
/// (row-view-based, deleted in item 6). Phase-to-phase comparison:
///
///   IndexBuild       — ColumnarIndexBuilder.Build(): interning, sort-then-
///                      scatter into column arrays, merge-join -> RefGroup[]
///   DuplicateScan    — phases 1+2: touches Groups[] + ProcessingState only
///   SplitConsolidatedScan — phases 3+4: same
///   MismatchScan     — phases 5+6: touches AmountMinor[] + DayNumber[] +
///                      ProcessingState only — key columnar win, these columns
///                      are now separated from MatchKeyId/OriginalIndex
///   MissingSweep     — phases 7+8: ProcessingState only, flat O(n)
///   ResultMaterialization — lazy GetResults() forced to ToList(), measures
///                      full materialization cost including OriginalIndex
///                      rehydration via SourceOriginals/TargetOriginals
///
/// Run with:
///   dotnet run -c Release --project tests/CedarRecon.Tests.Performance
///     --filter "*ColumnarClassifierPhase*"
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 15, launchCount: 1)]
public class ColumnarClassifierPhaseBenchmark
{
    private List<Transaction> _unmatchedSource = null!;
    private List<Transaction> _unmatchedTarget = null!;
    private List<MatchedPair> _matchedPairs = null!;
    private ColumnarIndex _index = null!;

    [Params(10_000, 100_000, 1_000_000)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var (source, target, matched) = ScenarioBuilder.Build(N, seed: 42);
        _unmatchedSource = source;
        _unmatchedTarget = target;
        _matchedPairs = matched;
        _index = ColumnarIndexBuilder.Build(source, target, matched);
    }

    // ── Phase: index construction ─────────────────────────────────────────────
    // Full build cost: interning, temp column encoding, sortOrder sort,
    // scatter into final columns, merge-join -> RefGroup[].

    [Benchmark]
    public int IndexBuild()
    {
        var index = ColumnarIndexBuilder.Build(_unmatchedSource, _unmatchedTarget, _matchedPairs);
        return index.Groups.Length;
    }

    // ── Phase: Duplicate detection (steps 1+2) ────────────────────────────────
    // Touches: Groups[] (ref readonly), ProcessingState (write).
    // Does NOT touch: MatchKeyId[], AmountMinor[], DayNumber[], OriginalIndex[].

    [Benchmark]
    public int DuplicateScan()
    {
        var src = _index.Source;
        var tgt = _index.Target;

        // Fresh state arrays — zero-initialized = None, same as real Classify()
        var srcState = new byte[src.Count];
        var tgtState = new byte[tgt.Count];
        var count = 0;

        for (var g = 0; g < _index.Groups.Length; g++)
        {
            ref readonly var group = ref _index.Groups[g];

            if (group.SourceCount > 1)
                for (var i = group.SourceStart; i < group.SourceStart + group.SourceCount; i++)
                {
                    srcState[i] = ClassificationStateBytes.DuplicateInSource;
                    count++;
                }

            if (group.TargetCount > 1)
                for (var i = group.TargetStart; i < group.TargetStart + group.TargetCount; i++)
                {
                    tgtState[i] = ClassificationStateBytes.DuplicateInTarget;
                    count++;
                }
        }

        return count;
    }

    // ── Phase: Split + Consolidated (steps 3+4) ───────────────────────────────
    // Touches: Groups[], ProcessingState (read/write).

    [Benchmark]
    public int SplitConsolidatedScan()
    {
        var src = _index.Source;
        var tgt = _index.Target;
        var srcState = new byte[src.Count];
        var tgtState = new byte[tgt.Count];
        var count = 0;

        for (var g = 0; g < _index.Groups.Length; g++)
        {
            ref readonly var group = ref _index.Groups[g];

            if (group.TotalSourceLegs == 1 && group.TotalTargetLegs > 1)
                for (var i = group.SourceStart; i < group.SourceStart + group.SourceCount; i++)
                    if (srcState[i] == ClassificationStateBytes.None)
                    {
                        srcState[i] = ClassificationStateBytes.SplitPayment;
                        count++;
                    }

            if (group.TotalSourceLegs > 1 && group.TotalTargetLegs == 1)
                for (var i = group.TargetStart; i < group.TargetStart + group.TargetCount; i++)
                    if (tgtState[i] == ClassificationStateBytes.None)
                    {
                        tgtState[i] = ClassificationStateBytes.ConsolidatedPayment;
                        count++;
                    }
        }

        return count;
    }

    // ── Phase: Amount/Date mismatch (steps 5+6) ───────────────────────────────
    // Touches: AmountMinor[] and DayNumber[] (read), ProcessingState (read/write).
    // Does NOT touch: MatchKeyId[], OriginalIndex[].
    // This is the key columnar win for this phase — AmountMinor and DayNumber
    // now live in their own arrays, no unrelated fields loaded into cache.

    [Benchmark]
    public int MismatchScan()
    {
        var src = _index.Source;
        var tgt = _index.Target;
        var srcState = new byte[src.Count];
        var tgtState = new byte[tgt.Count];

        var srcAmount = src.AmountMinor;
        var tgtAmount = tgt.AmountMinor;
        var srcDay = src.DayNumber;
        var tgtDay = tgt.DayNumber;
        var count = 0;

        for (var g = 0; g < _index.Groups.Length; g++)
        {
            ref readonly var group = ref _index.Groups[g];
            if (group.TargetCount == 0) continue;

            for (var si = group.SourceStart; si < group.SourceStart + group.SourceCount; si++)
            {
                if (srcState[si] != ClassificationStateBytes.None) continue;

                var sourceAmount = srcAmount[si];
                var bestTargetIndex = -1;
                var bestDiff = long.MaxValue;

                for (var ti = group.TargetStart; ti < group.TargetStart + group.TargetCount; ti++)
                {
                    if (tgtState[ti] != ClassificationStateBytes.None) continue;

                    var diff = Math.Abs(tgtAmount[ti] - sourceAmount);
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        bestTargetIndex = ti;
                    }
                }

                if (bestTargetIndex < 0) continue;

                srcState[si] = srcAmount[si] != tgtAmount[bestTargetIndex]
                    ? ClassificationStateBytes.AmountMismatch
                    : srcDay[si] != tgtDay[bestTargetIndex]
                        ? ClassificationStateBytes.DateMismatch
                        : ClassificationStateBytes.MissingInTarget;
                count++;
            }
        }

        return count;
    }

    // ── Phase: terminal sweep (steps 7+8) ─────────────────────────────────────
    // Touches: ProcessingState only — flat O(n), no column data at all.

    [Benchmark]
    public int MissingSweep()
    {
        var srcState = new byte[_index.Source.Count];
        var tgtState = new byte[_index.Target.Count];
        var count = 0;

        for (var i = 0; i < srcState.Length; i++)
            if (srcState[i] == ClassificationStateBytes.None)
            {
                srcState[i] = ClassificationStateBytes.MissingInTarget;
                count++;
            }

        for (var i = 0; i < tgtState.Length; i++)
            if (tgtState[i] == ClassificationStateBytes.None)
            {
                tgtState[i] = ClassificationStateBytes.MissingInSource;
                count++;
            }

        return count;
    }

    // ── Phase: result materialization ─────────────────────────────────────────
    // Forces GetResults().ToList() — measures full eager materialization
    // including OriginalIndex[] rehydration via SourceOriginals/TargetOriginals.
    // Worst case: all rows pre-filled as Missing*, so every row materializes.

    [Benchmark]
    public int ResultMaterialization()
    {
        var src = _index.Source;
        var tgt = _index.Target;

        var srcState = new byte[src.Count];
        var tgtState = new byte[tgt.Count];
        Array.Fill(srcState, ClassificationStateBytes.MissingInTarget);
        Array.Fill(tgtState, ClassificationStateBytes.MissingInSource);

        // Patch ProcessingState into the batch for GetResults() to read —
        // we can't mutate the shared _index.Source/Target.ProcessingState
        // directly (that would corrupt subsequent iterations), so we build
        // a lightweight result wrapper with fresh state arrays.
        var result = new ClassificationResult
        {
            Source = new ColumnarTransactionBatch
            {
                Count = src.Count,
                OriginalIndex = src.OriginalIndex,
                MatchKeyId = src.MatchKeyId,
                AmountMinor = src.AmountMinor,
                DayNumber = src.DayNumber,
                ProcessingState = srcState,
            },
            Target = new ColumnarTransactionBatch
            {
                Count = tgt.Count,
                OriginalIndex = tgt.OriginalIndex,
                MatchKeyId = tgt.MatchKeyId,
                AmountMinor = tgt.AmountMinor,
                DayNumber = tgt.DayNumber,
                ProcessingState = tgtState,
            },
            SourceOriginals = _index.SourceOriginals,
            TargetOriginals = _index.TargetOriginals,
        };

        return result.ToList().Count;
    }
}