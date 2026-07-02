using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Enums;
using CedarRecon.Domain.ValueObjects;
namespace CedarRecon.Tests.Unit.Helpers;

internal static class ScenarioBuilder
{
    public static (List<Transaction> Source, List<Transaction> Target, List<MatchedPair> Matched)
        Build(int totalReferences, int seed)
    {
        var rnd = new Random(seed);
        var source = new List<Transaction>(totalReferences);
        var target = new List<Transaction>(totalReferences);
        var matched = new List<MatchedPair>(Math.Max(1, totalReferences / 10));

        var noiseCount = (int)(totalReferences * 0.60);
        var dupCount = (int)(totalReferences * 0.15);
        var splitCount = (int)(totalReferences * 0.10);
        var consolCount = (int)(totalReferences * 0.10);
        var mismatchCount = totalReferences - noiseCount - dupCount - splitCount - consolCount;

        var refIndex = 0;

        for (var i = 0; i < noiseCount; i++)
        {
            source.Add(Tx($"NOISE-SRC-{refIndex}", 100m + i, rnd));
            target.Add(Tx($"NOISE-TGT-{refIndex}", 200m + i, rnd));
            refIndex++;
        }

        for (var i = 0; i < dupCount; i++)
        {
            var legs = rnd.Next(2, 4);
            var refKey = $"DUP-{refIndex++}";
            for (var l = 0; l < legs; l++)
                source.Add(Tx(refKey, 100m + l, rnd));
        }

        for (var i = 0; i < splitCount; i++)
        {
            var legs = rnd.Next(2, 5);
            var refKey = $"SPLIT-{refIndex++}";
            source.Add(Tx(refKey, 1000m, rnd));
            for (var l = 0; l < legs; l++)
                target.Add(Tx(refKey, 1000m / legs, rnd));
        }

        for (var i = 0; i < consolCount; i++)
        {
            var legs = rnd.Next(2, 5);
            var refKey = $"CONSOL-{refIndex++}";
            for (var l = 0; l < legs; l++)
                source.Add(Tx(refKey, 1000m / legs, rnd));
            target.Add(Tx(refKey, 1000m, rnd));
        }

        for (var i = 0; i < mismatchCount; i++)
        {
            var refKey = $"MISMATCH-{refIndex++}";
            source.Add(Tx(refKey, 100m, rnd));
            target.Add(Tx(refKey, 100m + rnd.Next(1, 50), rnd));
        }

        var matchedSampleSize = Math.Min(Math.Max(1, refIndex / 20), 50_000);
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
        Description = "test",
        SourceFileName = "test.csv",
        SourceRowNumber = 1,
    };
}