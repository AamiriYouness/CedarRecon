using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using CedarRecon.Classification;
using CedarRecon.Core.Entities;
using CedarRecon.Reference;
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