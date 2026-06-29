using BenchmarkDotNet.Attributes;
using CedarRecon.Application.Matching.Strategies;

namespace CedarRecon.Tests.Performance;

/// <summary>
/// Bucket-size benchmark — shows how each resolver scales as k grows.
/// Run this if the bucket cap is being raised in a config discussion.
/// </summary>
[MemoryDiagnoser]
public class ModuloBucketBenchmark
{
    [Params(5, 15, 50, 100)]
    public int BucketSize { get; set; }

    private decimal[] _candidates = [];
    private static readonly decimal Source = 900_000m;

    [GlobalSetup]
    public void Setup()
    {
        _candidates = Enumerable.Range(1, BucketSize)
            .Select(i => i % 3 == 0 ? 300_000m : 123_456m + i) // mix valid/invalid divisors
            .ToArray();
    }

    [Benchmark(Baseline = true)]
    public int Decimal()
    {
        var hits = 0;
        foreach (var c in _candidates)
            if (ModuloResolver.Decimal(Source, c)) hits++;
        return hits;
    }

    [Benchmark]
    public int ScaledLong()
    {
        var hits = 0;
        foreach (var c in _candidates)
            if (ModuloResolver.ScaledLong(Source, c)) hits++;
        return hits;
    }
}
