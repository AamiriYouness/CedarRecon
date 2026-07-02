namespace CedarRecon.Application.Classification.Indexed;

/// <summary>
/// All data needed to classify every transaction sharing one reference.
///
/// Points into the sorted IndexedTransaction[] arrays rather than owning a
/// copy of the rows — SourceStart/SourceCount and TargetStart/TargetCount
/// describe a contiguous RANGE in the (by-ReferenceId-sorted) source/target
/// arrays. This is the replacement for Dictionary&lt;string, List&lt;Transaction&gt;&gt;:
/// instead of N small heap-allocated List&lt;Transaction&gt; objects scattered
/// across the heap (one per distinct reference, each its own allocation with
/// its own backing array), there is exactly ONE array per side, sorted once,
/// and groups are just (offset, length) pairs into it — zero additional
/// allocation per reference.
///
/// MatchedSourceCount / MatchedTargetCount carry the matched-leg counts that
/// used to live in two separate Dictionary&lt;string,int&gt; (matchedSourceLegCounts,
/// matchedTargetLegCounts) — folded into this same struct so a single array
/// access gets everything TotalLegs() used to need four dictionary lookups for.
/// </summary>
public struct RefGroup
{
    public int ReferenceId;

    public int SourceStart;
    public int SourceCount;

    public int TargetStart;
    public int TargetCount;

    public int MatchedSourceCount;
    public int MatchedTargetCount;

    /// <summary>Total source legs for this reference = unmatched + already-matched.</summary>
    public readonly int TotalSourceLegs => SourceCount + MatchedSourceCount;

    /// <summary>Total target legs for this reference = unmatched + already-matched.</summary>
    public readonly int TotalTargetLegs => TargetCount + MatchedTargetCount;
}

/// <summary>
/// Per-row classification outcome, stored as a single byte indexed by
/// IndexedTransaction.OriginalIndex.
///
/// Replaces HashSet&lt;Guid&gt; classifiedSourceIds / classifiedTargetIds.
/// A HashSet&lt;Guid&gt;.Contains call hashes a 16-byte Guid and walks a bucket
/// chain on collision — cheap per call, but still real work multiplied by
/// every classification check across 8 cascade phases. A byte[] indexed by
/// a dense int (OriginalIndex, assigned 0..N-1 at index-build time) replaces
/// every "Contains" check with a single array read, and every "Add" with a
/// single array write — no hashing, no collision resolution, and the whole
/// array fits in far less memory than an equivalent HashSet&lt;Guid&gt; (1 byte
/// per row vs. a HashSet entry's much larger per-element overhead).
/// </summary>
public enum ClassificationState : byte
{
    None = 0,
    DuplicateInSource = 1,
    DuplicateInTarget = 2,
    SplitPayment = 3,
    ConsolidatedPayment = 4,
    AmountMismatch = 5,
    DateMismatch = 6,
    MissingInTarget = 7,
    MissingInSource = 8,
}
