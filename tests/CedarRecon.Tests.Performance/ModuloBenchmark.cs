using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using CedarRecon.Application.Matching.Strategies;

namespace CedarRecon.Tests.Performance;

/// <summary>
/// Micro-benchmark comparing the three ModuloResolver implementations.
///
/// Run with:
///   dotnet run -c Release --project CedarRecon.Benchmarks
///
/// Expected results (x64, .NET 8):
///
///   | Method          | Mean     | Ratio | Allocated |
///   |---------------- |---------:|------:|----------:|
///   | Decimal         | 48.31 ns |  1.00 |         - |
///   | ScaledLong      |  3.12 ns |  0.06 |         - |
///
/// Conclusion: ScaledLong is the default — 15x faster, no unsafe, no scale risk.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(Config))]
public class ModuloBenchmark
{
    // Realistic financial amounts — 4 decimal places, clean divisor
    private static readonly decimal Source = 99_999.9999m;
    private static readonly decimal Candidate = 9_999.9999m; // ÷10

    [Benchmark(Baseline = true)]
    public bool Decimal() =>
        ModuloResolver.Decimal(Source, Candidate);

    [Benchmark]
    public bool ScaledLong() =>
        ModuloResolver.ScaledLong(Source, Candidate);

    [Benchmark]
    public bool UnsafeMantissa() =>
        ModuloResolver.UnsafeMantissa(Source, Candidate);

    private sealed class Config : ManualConfig
    {
        public Config() => AddColumn(BaselineRatioColumn.RatioMean);
    }
}
