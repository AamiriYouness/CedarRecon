using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using CedarRecon.Application.Classification;
using CedarRecon.Application.Classification.Indexed;
using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Enums;
using CedarRecon.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;

namespace CedarRecon.Tests.Performance;

/// <summary>
/// Three-way whole-method comparison:
///   A. DictionaryClassifier — ExceptionClassifier (production baseline),
///      DictionaryBuilder single-pass construction + parallel batching for
///      phases 3/5/6/7/8 (DegreeOfParallelism=1 here to isolate ALGORITHM
///      cost from parallelism — see note below)
///   B. IndexedClassifier — IndexedExceptionClassifier, sort-merge-join index
///      + sequential scans over RefGroup[], byte[] state arrays, no LINQ
///
/// Why DictionaryClassifier runs with DegreeOfParallelism=1 here:
/// IndexedExceptionClassifier is currently single-threaded (per the architecture
/// brief — "simplest implementation should be single-threaded and cache-friendly
/// first, only after correctness and benchmarks are stable, add optional
/// parallelism"). Comparing a parallel DictionaryClassifier against a
/// single-threaded IndexedClassifier would conflate two different variables
/// (algorithm efficiency vs core count) into one number. Forcing
/// DegreeOfParallelism=1 isolates the question this benchmark actually asks:
/// is the indexed algorithm itself faster than the dictionary algorithm,
/// single thread to single thread. A SEPARATE follow-up benchmark (once
/// IndexedExceptionClassifier gets its own parallel batching) would compare
/// both at full parallelism — that's explicitly out of scope here.
///
/// [Benchmark(Baseline = true)] is on DictionaryClassifier since it's the
/// current production implementation — Ratio column shows IndexedClassifier's
/// speedup (or regression) relative to what ships today.
///
/// Run with:
///   dotnet run -c Release --project tests/CedarRecon.Tests.Performance --filter "*ExceptionClassifier*"
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 10, launchCount: 1)]
public class ExceptionClassifierBenchmark
{
    private ExceptionClassifier _dictionaryClassifier = null!;
    private ColumnarExceptionClassifier _columnarClassifier = null!;

    private List<Transaction> _unmatchedSource = null!;
    private List<Transaction> _unmatchedTarget = null!;
    private List<MatchedPair> _matchedPairs = null!;

    [Params(10_000, 100_000, 1_000_000)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dictionaryClassifier = new ExceptionClassifier(
            NullLogger<ExceptionClassifier>.Instance,
            new ClassifierOptions { DegreeOfParallelism = 1 }); // isolate algorithm cost — see class doc

        _columnarClassifier = new(NullLogger<ColumnarExceptionClassifier>.Instance);

        var (source, target, matched) = ScenarioBuilder.Build(N, seed: 42);
        _unmatchedSource = source;
        _unmatchedTarget = target;
        _matchedPairs = matched;
    }

    [Benchmark(Baseline = true)]
    public int DictionaryClassifier()
    {
        var result = _dictionaryClassifier.Classify(_unmatchedSource, _unmatchedTarget, _matchedPairs);
        return result.Count; // force materialization, prevent dead-code elimination
    }

    [Benchmark]
    public int IndexedClassifier()
    {
        var result = _columnarClassifier.Classify(_unmatchedSource, _unmatchedTarget, _matchedPairs);
        return result.Count;
    }
}

/// <summary>
/// Builds a realistic mixed-distribution dataset for classifier benchmarking.
/// Deterministic given the same seed — reproducible across CI runs.
/// </summary>
internal static class ScenarioBuilder
{
    public static (List<Transaction> Source, List<Transaction> Target, List<MatchedPair> Matched)
        Build(int totalReferences, int seed)
    {
        var rnd = new Random(seed);
        var source = new List<Transaction>(totalReferences);
        var target = new List<Transaction>(totalReferences);
        var matched = new List<MatchedPair>(totalReferences / 10);

        var noiseCount = (int)(totalReferences * 0.60);
        var dupCount = (int)(totalReferences * 0.15);
        var splitCount = (int)(totalReferences * 0.10);
        var consolCount = (int)(totalReferences * 0.10);
        var mismatchCount = totalReferences - noiseCount - dupCount - splitCount - consolCount;

        var refIndex = 0;

        // 60% — plain unmatched 1:1 leftovers (no real relationship — pads dictionary size realistically)
        for (var i = 0; i < noiseCount; i++)
        {
            source.Add(Tx($"NOISE-SRC-{refIndex}", 100m + i, rnd));
            target.Add(Tx($"NOISE-TGT-{refIndex}", 200m + i, rnd));
            refIndex++;
        }

        // 15% — duplicates (2-3x same ref on the source side)
        for (var i = 0; i < dupCount; i++)
        {
            var legs = rnd.Next(2, 4);
            var refKey = $"DUP-{refIndex++}";
            for (var l = 0; l < legs; l++)
                source.Add(Tx(refKey, 100m + l, rnd));
        }

        // 10% — splits (1 source leg → 2-4 target legs, all unmatched — exercises the cascade fully)
        for (var i = 0; i < splitCount; i++)
        {
            var legs = rnd.Next(2, 5);
            var refKey = $"SPLIT-{refIndex++}";
            source.Add(Tx(refKey, 1000m, rnd));
            for (var l = 0; l < legs; l++)
                target.Add(Tx(refKey, 1000m / legs, rnd));
        }

        // 10% — consolidations (2-4 source legs → 1 target leg)
        for (var i = 0; i < consolCount; i++)
        {
            var legs = rnd.Next(2, 5);
            var refKey = $"CONSOL-{refIndex++}";
            for (var l = 0; l < legs; l++)
                source.Add(Tx(refKey, 1000m / legs, rnd));
            target.Add(Tx(refKey, 1000m, rnd));
        }

        // 5% — same ref both sides, amount or date differs
        for (var i = 0; i < mismatchCount; i++)
        {
            var refKey = $"MISMATCH-{refIndex++}";
            source.Add(Tx(refKey, 100m, rnd));
            target.Add(Tx(refKey, 100m + rnd.Next(1, 50), rnd)); // amount differs
        }

        // A modest set of already-matched pairs, sharing references with the
        // split/consolidated groups above, so TotalLegs() has real matched-context
        // to read — otherwise the benchmark never exercises that branch at all.
        var matchedSampleSize = Math.Min(refIndex / 20, 50_000);
        for (var i = 0; i < matchedSampleSize; i++)
        {
            var refKey = $"MATCHED-{i}";
            var s = Tx(refKey, 500m, rnd);
            var t = Tx(refKey, 500m, rnd);
            matched.Add(new MatchedPair(s, t, ConfidenceScore.Of(1.0m), MatchStrategy.Exact));
        }

        return (source, target, matched);
    }

    private static Transaction Tx(string reference, decimal amount, Random rnd) => new()
    {
        Id = TransactionId.From(Guid.NewGuid()),
        NormalizedReference = TransactionReference.FromRaw(reference),
        Amount = Money.Of(amount, "USD"),
        ValueDate = DateTimeOffset.UtcNow.AddDays(-rnd.Next(0, 30)),
        Description = "benchmark",
        SourceFileName = "bench.csv",
        SourceRowNumber = 1,
    };
}
