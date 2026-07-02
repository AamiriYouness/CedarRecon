using CedarRecon.Domain.Entities;
using System.Runtime.InteropServices;

namespace CedarRecon.Application.Classification;

/// <summary>
/// Single-pass dictionary construction for ExceptionClassifier's reference-keyed
/// lookups. Replaces GroupBy().ToDictionary() to avoid hashing every key twice.
///
/// Why this exists — measured, not assumed:
/// BuildDictionaries was found to be the dominant cost in ExceptionClassifier.Classify()
/// — ~56% of total time and ~65% of total allocation at N=1,000,000 unmatched
/// transactions (see docs/exception-classifier-scaling.md). GroupBy builds an
/// internal hash-grouping structure (one hash pass per element), then ToDictionary
/// hashes every key again to insert into the result dictionary — two full hash
/// passes per element, plus GroupBy's IGrouping wrapper allocations that are
/// discarded immediately after ToDictionary copies their contents out.
///
/// CollectionsMarshal.GetValueRefOrAddDefault hashes the key ONCE, returns a ref
/// to the dictionary slot (creating it with a default value if absent), and lets
/// us mutate that slot directly — no GroupBy, no IGrouping wrappers, no second hash.
///
/// This is NOT unsafe code. CollectionsMarshal is a sanctioned BCL API
/// (System.Runtime.InteropServices, .NET 6+) built specifically for this pattern.
/// No pointers, no manual memory management.
///
/// Dictionaries are pre-sized using <paramref name="expectedDistinctKeys"/> as a
/// capacity hint where the caller can estimate it, to avoid incremental resize-
/// and-rehash as the dictionary grows past its initial bucket count. Default
/// capacity hints are conservative — pass a real estimate when available
/// (e.g. from ReconciliationOptions.ExpectedTargetCount) for the full benefit.
/// </summary>
public static class DictionaryBuilder
{
    /// <summary>
    /// Groups transactions by NormalizedReference.Value in a single pass.
    /// Equivalent to:
    ///   transactions.GroupBy(t => t.NormalizedReference.Value, StringComparer.Ordinal)
    ///               .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal)
    /// but without the double-hash and intermediate IGrouping allocations.
    /// </summary>
    public static Dictionary<string, List<Transaction>> BuildGroupedByReference(
        IReadOnlyList<Transaction> transactions,
        int? expectedDistinctKeys = null)
    {
        var capacity = expectedDistinctKeys ?? EstimateCapacity(transactions.Count);
        var result = new Dictionary<string, List<Transaction>>(capacity, StringComparer.Ordinal);

        foreach (var tx in transactions)
        {
            var key = tx.NormalizedReference.Value;

            // Single hash lookup: returns a ref to the existing slot, or creates
            // one with the default value (null for List<Transaction>) and returns
            // a ref to that. 'exists' tells us which case we hit.
            ref var bucket = ref CollectionsMarshal.GetValueRefOrAddDefault(
                result, key, out var exists);

            if (!exists)
                bucket = new List<Transaction>(1); // most buckets are size 1 in practice

            bucket!.Add(tx);
        }

        return result;
    }

    /// <summary>
    /// Counts matched pairs by Source.NormalizedReference.Value in a single pass.
    /// Equivalent to:
    ///   matchedPairs.GroupBy(p => p.Source.NormalizedReference.Value, StringComparer.Ordinal)
    ///               .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)
    /// </summary>
    public static Dictionary<string, int> BuildSourceLegCounts(
        IReadOnlyList<MatchedPair> matchedPairs,
        int? expectedDistinctKeys = null)
    {
        var capacity = expectedDistinctKeys ?? EstimateCapacity(matchedPairs.Count);
        var result = new Dictionary<string, int>(capacity, StringComparer.Ordinal);

        foreach (var pair in matchedPairs)
        {
            var key = pair.Source.NormalizedReference.Value;
            ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(
                result, key, out _);
            count++;
        }

        return result;
    }

    /// <summary>
    /// Counts matched pairs by Target.NormalizedReference.Value in a single pass.
    /// Mirror of <see cref="BuildSourceLegCounts"/> for the target side.
    /// </summary>
    public static Dictionary<string, int> BuildTargetLegCounts(
        IReadOnlyList<MatchedPair> matchedPairs,
        int? expectedDistinctKeys = null)
    {
        var capacity = expectedDistinctKeys ?? EstimateCapacity(matchedPairs.Count);
        var result = new Dictionary<string, int>(capacity, StringComparer.Ordinal);

        foreach (var pair in matchedPairs)
        {
            var key = pair.Target.NormalizedReference.Value;
            ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(
                result, key, out _);
            count++;
        }

        return result;
    }

    /// <summary>
    /// Conservative capacity estimate when the caller has no better number.
    /// Reconciliation data tends toward mostly-unique references with a modest
    /// duplicate/split/consolidated tail — assuming 70% distinct keys avoids
    /// under-sizing (which causes resize-and-rehash) while not wildly over-
    /// allocating for datasets that are closer to fully unique.
    /// </summary>
    private static int EstimateCapacity(int itemCount) =>
        Math.Max(4, (int)(itemCount * 0.7));
}