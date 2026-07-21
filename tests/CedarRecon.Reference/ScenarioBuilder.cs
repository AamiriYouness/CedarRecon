using CedarRecon.Core.Entities;
using CedarRecon.Core.Enums;
using CedarRecon.Core.ValueObjects;
namespace CedarRecon.Reference;

public static class ScenarioBuilder
{
    public static readonly DateTimeOffset BaseDate = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Original generator — produces source/target transactions PLUS a
    /// synthetic, hand-fabricated `matched` list (never actually run
    /// through HashMatchingEngine). Correct for isolating classification
    /// logic (ColumnarExceptionClassifier golden/equivalence tests), where
    /// the input to classification is "here are some already-matched pairs,
    /// classify the rest" and matching itself isn't under test.
    ///
    /// Do NOT use this for matching golden tests — see BuildRaw below.
    /// </summary>
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

    /// <summary>
    /// Raw generator for matching golden tests — produces ONLY source/target
    /// transactions, no fabricated MatchedPair list. Real matching (via
    /// MatchStrategyFactory / HashMatchingEngine) must be run against this
    /// output to produce actual matches; the test proves something real
    /// about the matching engine, unlike Build() above.
    ///
    /// Distribution mirrors Build()'s shape (noise/dup/split/consol/mismatch)
    /// so the same seed produces structurally comparable data, but every
    /// reference here is a genuine matching CANDIDATE — including the
    /// "MATCHED" category, which here is just two transactions sharing a
    /// reference and amount, exactly matchable by ExactMatchStrategy, not
    /// pre-declared as matched.
    /// </summary>
    public static (List<Transaction> Source, List<Transaction> Target)
        BuildRaw(int totalReferences, int seed)
    {
        var rnd = new Random(seed);
        var source = new List<Transaction>(totalReferences);
        var target = new List<Transaction>(totalReferences);

        var noiseCount = (int)(totalReferences * 0.55);
        var dupCount = (int)(totalReferences * 0.10);
        var splitCount = (int)(totalReferences * 0.10);
        var consolCount = (int)(totalReferences * 0.10);
        var mismatchCount = (int)(totalReferences * 0.05);
        var matchableCount = totalReferences - noiseCount - dupCount - splitCount - consolCount - mismatchCount;

        var refIndex = 0;

        // Genuinely matchable — ExactMatchStrategy candidates. Reference,
        // amount, currency, AND date must all match exactly (see
        // ExactMatchStrategy.IsExactMatch) — draw the date ONCE and reuse
        // it for both sides, since two independent rnd.Next() draws would
        // almost certainly produce different dates and silently fall
        // through to Fuzzy/unmatched instead.
        for (var i = 0; i < matchableCount; i++)
        {
            var refKey = $"MATCHABLE-{refIndex++}";
            var sharedDate = BaseDate.AddDays(-rnd.Next(0, 30));
            source.Add(Tx(refKey, 500m, sharedDate));
            target.Add(Tx(refKey, 500m, sharedDate));
        }

        // No counterpart on either side — pads dataset realistically,
        // never matches, never affects match-set correctness.
        for (var i = 0; i < noiseCount; i++)
        {
            source.Add(Tx($"NOISE-SRC-{refIndex}", 100m + i, rnd));
            target.Add(Tx($"NOISE-TGT-{refIndex}", 200m + i, rnd));
            refIndex++;
        }

        // Duplicate source legs, no target — none should match.
        for (var i = 0; i < dupCount; i++)
        {
            var legs = rnd.Next(2, 4);
            var refKey = $"DUP-{refIndex++}";
            for (var l = 0; l < legs; l++)
                source.Add(Tx(refKey, 100m + l, rnd));
        }

        // Split/consolidated — exercises PartialMatchStrategy candidates
        // (one side one leg, other side multiple legs, amounts sum to match
        // within tolerance).
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

        // Same reference both sides, amount differs slightly — exercises
        // FuzzyMatchStrategy's tolerance-window candidates.
        for (var i = 0; i < mismatchCount; i++)
        {
            var refKey = $"MISMATCH-{refIndex++}";
            source.Add(Tx(refKey, 100m, rnd));
            target.Add(Tx(refKey, 100m + rnd.Next(1, 5), rnd)); // small diff — within fuzzy tolerance
        }

        return (source, target);
    }

    private static Transaction Tx(string reference, decimal amount, Random rnd) =>
        Tx(reference, amount, BaseDate.AddDays(-rnd.Next(0, 30)));

    private static Transaction Tx(string reference, decimal amount, DateTimeOffset valueDate) => new()
    {
        Id = TransactionId.From(Guid.NewGuid()),
        NormalizedReference = TransactionReference.FromRaw(reference),
        Amount = Money.Of(amount, "USD"),
        ValueDate = valueDate,
        Description = "test",
        SourceFileName = "test.csv",
        SourceRowNumber = 1,
    };
}