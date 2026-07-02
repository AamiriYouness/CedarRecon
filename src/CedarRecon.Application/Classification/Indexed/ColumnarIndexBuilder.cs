using CedarRecon.Domain.Entities;
namespace CedarRecon.Application.Classification.Indexed;

/// <summary>
/// Builds ColumnarTransactionBatch arrays and RefGroup[] from raw unmatched
/// transactions and matched pairs using histogram (counting) sort.
///
/// Item 7 — replaces the sort-order + random-read scatter approach from
/// item 6. The previous implementation:
///   1. Built temporary columns in input order
///   2. Built sortOrder[] and sorted it via Array.Sort (O(n log n))
///   3. Scattered each column: final[i] = temp[sortOrder[i]]
///      — random reads from temp[] at N=1M, cache-hostile
///
/// This implementation:
///   1. Builds temporary columns in input order          O(n)
///   2. Counts rows per MatchKeyId (histogram)           O(n)
///   3. Prefix-sum → per-key start offsets              O(k)
///   4. Places rows into final columns via cursor[key]++ O(n)
///      — writes are sequential within each key bucket
///   5. Builds RefGroup[] directly from histogram        O(k)
///      — the merge-join pass from item 6 is eliminated
///      entirely: starts[] and counts[] ARE SourceStart/
///      SourceCount, no second traversal needed
///
/// Total: O(n + k) where k = distinct MatchKeyIds (≤ n).
/// For typical reconciliation data (high-reference-reuse), k ≪ n and
/// this is effectively O(n).
///
/// Why this fits ColumnarTransactionBatch:
/// MatchKeyId is a dense integer (0..interner.Count-1), interned once at
/// encode time — exactly the ideal key for counting sort. Comparison-based
/// sorts cannot beat O(n log n) on arbitrary keys; integer histogram sort
/// exploits the density and bounded range that interning provides.
/// </summary>
public sealed class ColumnarIndexBuilder
{
    public static ColumnarIndex Build(
        IReadOnlyList<Transaction> unmatchedSource,
        IReadOnlyList<Transaction> unmatchedTarget,
        IReadOnlyList<MatchedPair> matchedPairs)
    {
        var interner = new ReferenceInterner();

        var sourceBatch = BuildBatch(unmatchedSource, interner);
        var targetBatch = BuildBatch(unmatchedTarget, interner);

        // ── Matched-pair leg counts ───────────────────────────────────────────
        // Intern matched-pair references (defensive — they should already be
        // interned via source/target, but hand-built test fixtures may not be).
        var matchedSourceLegCounts = new int[interner.Count];
        var matchedTargetLegCounts = new int[interner.Count];

        foreach (var pair in matchedPairs)
        {
            var sourceRefId = interner.GetOrAdd(pair.Source.NormalizedReference.Value);
            var targetRefId = interner.GetOrAdd(pair.Target.NormalizedReference.Value);

            EnsureCapacity(ref matchedSourceLegCounts, sourceRefId);
            EnsureCapacity(ref matchedTargetLegCounts, targetRefId);

            matchedSourceLegCounts[sourceRefId]++;
            matchedTargetLegCounts[targetRefId]++;
        }

        // ── Build RefGroup[] directly from histograms ─────────────────────────
        // sourceBatch and targetBatch already carry their per-key counts and
        // start offsets from BuildBatch — no merge-join pass needed.
        var groups = BuildGroupsFromHistograms(
            sourceBatch, targetBatch,
            matchedSourceLegCounts, matchedTargetLegCounts,
            interner.Count);

        return new ColumnarIndex(
            sourceBatch.Batch, targetBatch.Batch,
            groups, interner,
            unmatchedSource, unmatchedTarget);
    }

    // ── Batch construction via histogram sort ─────────────────────────────────

    /// <summary>
    /// Intermediate result of BuildBatch — carries the sorted
    /// ColumnarTransactionBatch plus the histogram arrays needed to build
    /// RefGroup[] without a second pass.
    /// </summary>
    private readonly struct BatchWithHistogram(
        ColumnarTransactionBatch batch,
        int[] starts,
        int[] counts,
        int keyCount)
    {
        public readonly ColumnarTransactionBatch Batch = batch;
        public readonly int[] Starts = starts;  // starts[keyId] = first sorted position for keyId
        public readonly int[] Counts = counts;  // counts[keyId] = number of rows with this keyId
        public readonly int KeyCount = keyCount; // interner.Count at encode time
    }

    private static BatchWithHistogram BuildBatch(
        IReadOnlyList<Transaction> transactions,
        ReferenceInterner interner)
    {
        var n = transactions.Count;

        // ── Step 1: encode into temporary columns (input order) ───────────────
        var tmpOriginalIndex = new int[n];
        var tmpMatchKeyId = new int[n];
        var tmpAmountMinor = new long[n];
        var tmpDayNumber = new int[n];

        for (var i = 0; i < n; i++)
        {
            var tx = transactions[i];
            tmpOriginalIndex[i] = i;
            tmpMatchKeyId[i] = interner.GetOrAdd(tx.NormalizedReference.Value);
            tmpAmountMinor[i] = MoneyMinorUnitsConverter.ToMinorUnits(tx.Amount.Amount);
            tmpDayNumber[i] = DateOnly.FromDateTime(tx.ValueDate.UtcDateTime.Date).DayNumber;
        }

        var keyCount = interner.Count;

        // ── Step 2: histogram — count rows per MatchKeyId ─────────────────────
        var counts = new int[keyCount];
        for (var i = 0; i < n; i++)
            counts[tmpMatchKeyId[i]]++;

        // ── Step 3: prefix sum → per-key start offsets ────────────────────────
        // starts[k] = position in the sorted output where key k's rows begin.
        // After this pass, starts[k] + counts[k] == starts[k+1] for all k.
        var starts = new int[keyCount];
        var running = 0;
        for (var k = 0; k < keyCount; k++)
        {
            starts[k] = running;
            running += counts[k];
        }

        // ── Step 4: place rows into final sorted columns ──────────────────────
        // cursor[k] starts at starts[k] and advances as rows for key k are
        // placed — writes within each key bucket are contiguous (sequential),
        // eliminating the random-read scatter of the previous implementation.
        // Reads from tmp* are still sequential (we iterate i = 0..n-1 in
        // input order), so both reads and writes are cache-friendly.
        var cursor = new int[keyCount];
        Array.Copy(starts, cursor, keyCount); // cursor starts at each key's start offset

        var originalIndex = new int[n];
        var matchKeyId = new int[n];
        var amountMinor = new long[n];
        var dayNumber = new int[n];

        for (var i = 0; i < n; i++)
        {
            var key = tmpMatchKeyId[i];
            var pos = cursor[key]++;

            originalIndex[pos] = tmpOriginalIndex[i];
            matchKeyId[pos] = key;
            amountMinor[pos] = tmpAmountMinor[i];
            dayNumber[pos] = tmpDayNumber[i];
        }

        var batch = new ColumnarTransactionBatch
        {
            Count = n,
            OriginalIndex = originalIndex,
            MatchKeyId = matchKeyId,
            AmountMinor = amountMinor,
            DayNumber = dayNumber,
            ProcessingState = new byte[n], // zero = None
        };

        return new BatchWithHistogram(batch, starts, counts, keyCount);
    }

    // ── RefGroup[] from histograms — no merge-join needed ─────────────────────

    /// <summary>
    /// Builds RefGroup[] directly from the source/target histograms.
    ///
    /// The previous BuildGroups did a merge-join walk over both sorted arrays
    /// to discover group boundaries. After histogram sort, those boundaries
    /// are already known: starts[k] and counts[k] are exactly SourceStart and
    /// SourceCount for every key k that appears in source (and equivalently
    /// for target). This pass is O(k) over distinct keys, not O(n) over rows.
    ///
    /// A key may appear in source only, target only, both, or neither (matched-
    /// only references interned during the matched-pairs loop). Only keys with
    /// at least one unmatched row in source OR target get a RefGroup — same
    /// semantics as before.
    /// </summary>
    private static RefGroup[] BuildGroupsFromHistograms(
        BatchWithHistogram source,
        BatchWithHistogram target,
        int[] matchedSourceLegCounts,
        int[] matchedTargetLegCounts,
        int internedCount)
    {
        // internedCount may exceed source.KeyCount or target.KeyCount if
        // matched-pair interning added new IDs after batch encoding.
        // All three arrays need to cover 0..internedCount-1.
        var groups = new RefGroup[internedCount];
        var count = 0;

        for (var k = 0; k < internedCount; k++)
        {
            var sourceCount = k < source.KeyCount ? source.Counts[k] : 0;
            var targetCount = k < target.KeyCount ? target.Counts[k] : 0;

            var matchedSource = k < matchedSourceLegCounts.Length
                ? matchedSourceLegCounts[k] : 0;
            var matchedTarget = k < matchedTargetLegCounts.Length
                ? matchedTargetLegCounts[k] : 0;

            // Skip keys with zero unmatched rows on both sides — these are
            // matched-only references (no classification work needed), same
            // intentional exclusion as the previous merge-join approach.
            if (sourceCount == 0 && targetCount == 0) continue;

            groups[count++] = new RefGroup
            {
                ReferenceId = k,
                SourceStart = k < source.KeyCount ? source.Starts[k] : 0,
                SourceCount = sourceCount,
                TargetStart = k < target.KeyCount ? target.Starts[k] : 0,
                TargetCount = targetCount,
                MatchedSourceCount = matchedSource,
                MatchedTargetCount = matchedTarget,
            };
        }

        if (count < groups.Length)
            Array.Resize(ref groups, count);

        return groups;
    }

    private static void EnsureCapacity(ref int[] array, int requiredIndex)
    {
        if (requiredIndex < array.Length) return;
        var newSize = Math.Max(requiredIndex + 1, array.Length * 2);
        Array.Resize(ref array, newSize);
    }
}

/// <summary>
/// Immutable result of ColumnarIndexBuilder.Build() — the two sorted
/// columnar batches, the RefGroup[] index, the interner, and the original
/// input lists for lazy Transaction rehydration.
/// </summary>
public sealed class ColumnarIndex
{
    public ColumnarTransactionBatch Source { get; }
    public ColumnarTransactionBatch Target { get; }
    public RefGroup[] Groups { get; }
    public ReferenceInterner Interner { get; }
    public IReadOnlyList<Transaction> SourceOriginals { get; }
    public IReadOnlyList<Transaction> TargetOriginals { get; }

    public ColumnarIndex(
        ColumnarTransactionBatch source,
        ColumnarTransactionBatch target,
        RefGroup[] groups,
        ReferenceInterner interner,
        IReadOnlyList<Transaction> sourceOriginals,
        IReadOnlyList<Transaction> targetOriginals)
    {
        Source = source;
        Target = target;
        Groups = groups;
        Interner = interner;
        SourceOriginals = sourceOriginals;
        TargetOriginals = targetOriginals;
    }
}