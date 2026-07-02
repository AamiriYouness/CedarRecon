using CedarRecon.Domain.Entities;

namespace CedarRecon.Application.Classification.Indexed;

/// <summary>
/// Compact hot-path representation of a transaction for classification.
///
/// Why not use the domain Transaction object directly:
/// Transaction is a class (reference type) carrying ~10 fields including
/// strings (Description, Iban, CounterpartyName, SourceFileName) that
/// classification never touches. Every list of transactions is therefore a
/// list of pointers to scattered heap objects — iterating it means following
/// a pointer per element with no guarantee the next Transaction is anywhere
/// near the previous one in memory. That's the textbook cache-unfriendly
/// access pattern.
///
/// IndexedTransaction is a readonly struct: a flat value type containing only
/// what classification actually reads (ReferenceId, AmountMinor, DayNumber)
/// plus two pieces of bookkeeping (OriginalIndex, Original). An array of
/// these is contiguous memory — iterating it sequentially is exactly the
/// access pattern CPU prefetchers are built for.
///
/// OriginalIndex is the position in the original flat input list (source or
/// target) — assigned once during EncodeRows, BEFORE the array is sorted by
/// ReferenceId. Array.Sort reorders the struct array itself; it does not
/// touch the OriginalIndex field inside each struct, so this value stays a
/// stable pointer back into the original unmatchedSource/unmatchedTarget
/// list regardless of where the row ends up after sorting. It is used both
/// to index into the flat byte[] state arrays in IndexedExceptionClassifier
/// (see ClassificationState) and, as of this change, to rehydrate the full
/// Transaction lazily at result-materialization time via
/// ReferenceIndex.SourceOriginals/TargetOriginals — see ReferenceIndex.
///
/// This struct does NOT hold a reference back to the domain Transaction
/// object (removed in this change — see below). It is intentionally only
/// the fields classification's scanning phases actually read: ReferenceId,
/// AmountMinor, DayNumber, plus OriginalIndex for indexing. A reference type
/// field defeats much of the point of compacting this struct in the first
/// place — every row would still carry an 8-byte pointer to a scattered
/// heap object, which is exactly the cache-unfriendly pattern this struct
/// exists to avoid. Original-object rehydration now happens exactly once,
/// lazily, only for rows that survive to result materialization, via
/// OriginalIndex indexing into the original input list — never during the
/// hot scanning phases (Duplicate/Split/Consolidated/Mismatch/Missing).
///
/// COMPACTION (worklist item 1): decimal Amount -> long AmountMinor,
/// DateOnly ValueDate -> int DayNumber, Transaction Original -> removed
/// (rehydrated via OriginalIndex instead, see above).
///
/// AmountMinor: scaled integer money amount, see MoneyMinorUnitsConverter
/// for the scale factor (10^4) and conversion rules — this directly targets
/// the MismatchScan regression, since decimal arithmetic (including
/// Math.Abs) is software-emulated on .NET while long arithmetic is a single
/// CPU instruction. Conversion happens once at EncodeRows time in
/// ReferenceIndexBuilder, not per-comparison.
///
/// DayNumber: this is DateOnly.DayNumber directly (the proleptic Gregorian
/// day count .NET already exposes as a plain int, day 1 = 0001-01-01) — NOT
/// a custom epoch. There is no separate conversion helper or precision
/// concern for this field the way there is for AmountMinor: DateOnly already
/// stores its value as an internal day-number-like int, and .DayNumber is
/// the public, documented, exact way to read it as one. Equality and
/// difference comparisons on int are already what DateOnly == effectively
/// reduces to internally; this just removes the intermediate struct so
/// IndexedTransaction itself stays a flat 4/8-byte-aligned value type with
/// no embedded struct field.
///
/// Struct size: previously ~40 bytes (4 ints/refs + 16-byte decimal + DateOnly
/// + object reference, with padding). Now: OriginalIndex(4) + ReferenceId(4)
/// + AmountMinor(8) + DayNumber(4) = 20 bytes logical, expected to align to
/// 24 bytes — hits the "~20-24 bytes" target range from the worklist.
/// Dropping the Original object reference (8 bytes on 64-bit) was necessary
/// to land in that range at all; keeping it would have capped the struct at
/// ~28-32 bytes regardless of how tightly Amount/ValueDate were packed.
/// Verify actual size via Marshal.SizeOf or sizeof in the accompanying
/// benchmark, do not assume without measuring.
/// </summary>
public readonly struct IndexedTransaction
{
    public readonly int OriginalIndex;
    public readonly int ReferenceId;
    public readonly long AmountMinor;
    public readonly int DayNumber;

    public IndexedTransaction(
        int originalIndex,
        int referenceId,
        long amountMinor,
        int dayNumber)
    {
        OriginalIndex = originalIndex;
        ReferenceId = referenceId;
        AmountMinor = amountMinor;
        DayNumber = dayNumber;
    }
}