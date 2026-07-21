using CedarRecon.Core.Entities;
using CedarRecon.Core.Options;
using CedarRecon.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace CedarRecon.Tests.Unit.Matching;

/// <summary>
/// Shared helper for running real matching against ScenarioBuilder.BuildRaw
/// output — extracted so MatchingGoldenTests, invariant tests, etc. don't
/// each duplicate this invocation and risk the same divergence that hit
/// ScenarioBuilder itself (Tests.Unit vs Tests.Performance drifting apart).
/// </summary>
public static class MatchingTestHelper
{
    public static readonly ReconciliationOptions DefaultOptions =
        ReconciliationOptionsBuilder.Create().Build();

    public static async Task<List<MatchedPair>> RunMatching(
        List<Transaction> source, List<Transaction> target,
        ReconciliationOptions? options = null)
    {
        var engine = new HashMatchingEngine(NullLogger<HashMatchingEngine>.Instance);
        await engine.BuildTargetIndexAsync(ToAsyncEnumerable(target));

        var matches = new List<MatchedPair>();
        foreach (var sourceTx in source)
        {
            var result = engine.Match(sourceTx, options ?? DefaultOptions);
            if (result is MatchedResult matched)
                matches.Add(matched.Pair);
        }

        return matches;
    }

    private static async IAsyncEnumerable<Transaction> ToAsyncEnumerable(IEnumerable<Transaction> items)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask;
    }
}