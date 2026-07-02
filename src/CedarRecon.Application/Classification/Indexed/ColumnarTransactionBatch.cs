using System;
using System.Collections.Generic;
using System.Text;

namespace CedarRecon.Application.Classification.Indexed;

/// <summary>
/// A columnar (struct-of-arrays) representation of a set of transactions
/// for use by the indexed classification engine — and, in future, by any
/// other execution operator (matching, deduplication, quality gates, etc.)
/// that needs to process the same rows.
///
/// WHY COLUMNAR (struct-of-arrays vs array-of-structs):
/// The array-of-structs layout (IndexedTransaction[]) stores all fields for
/// one row contiguously: [OriginalIndex|MatchKeyId|AmountMinor|DayNumber|...].
/// When a scan phase touches only ONE field (e.g. DuplicateScan only reads
/// MatchKeyId, MismatchScan only reads AmountMinor and DayNumber), every
/// cache line loaded still contains all the other fields — wasted bandwidth.
/// Struct-of-arrays stores each field in its own flat array:
///   MatchKeyId[0..N-1], AmountMinor[0..N-1], DayNumber[0..N-1], ...
/// A phase that only needs MatchKeyId touches ONLY the MatchKeyId array —
/// every cache line loaded contains exclusively the data that phase reads.
/// At N=1M with ~64-byte cache lines, the difference is measurable (see
/// IndexedClassifierPhaseBenchmark before/after item 6).
///
/// PROCESSINGSTATE — GENERIC EXECUTION BUFFER:
/// ProcessingState is a plain byte[] whose MEANING depends on which
/// execution operator is currently using this batch. It is not a typed
/// ClassificationState enum array — the engine reuses the same buffer
/// across phases without re-allocating. Each operator defines its own
/// byte constants (see ClassificationStateBytes, and future MatchingStateBytes,
/// QualityStateBytes, etc.). This is the same design a database execution
/// engine uses for its per-row status column: one buffer, operator-defined
/// interpretation, reset between pipeline stages.
///
/// One caution: because ProcessingState's meaning changes between operators,
/// it must NOT be exposed or persisted outside the engine. When an operator
/// finishes, it translates ProcessingState into stable domain types
/// (DiscrepancyType, MatchedPair, QualityIssue) before returning results.
/// The buffer is internal execution scratch, not a public contract.
///
/// CurrencyId is intentionally absent — deferred until currency-aware
/// minor-unit scaling lands. When added, it will be a ushort[] (65,535
/// distinct currencies is more than sufficient) interned via ReferenceInterner
/// or a dedicated CurrencyInterner.
///
/// Rows are in MatchKeyId-SORTED order (sorted once at build time by
/// ColumnarIndexBuilder, enabling the O(n) merge-join pass that produces
/// RefGroup[]). OriginalIndex[i] is the position of row i in the ORIGINAL
/// unsorted input list — the bridge back to the full Transaction object
/// for result materialization.
/// </summary>
public sealed class ColumnarTransactionBatch
{
    public int Count { get; init; }

    /// <summary>
    /// Position of each row in the original unsorted input list.
    /// Used at result-materialization time to rehydrate the full Transaction
    /// from ClassificationResult.SourceOriginals/TargetOriginals.
    /// Never read during scanning phases.
    /// </summary>
    public required int[] OriginalIndex { get; init; }

    /// <summary>
    /// Interned reference ID — the sort key. Rows are sorted ascending by
    /// this column, enabling the merge-join in ColumnarIndexBuilder and
    /// group-range scanning in every classifier phase.
    /// Renamed from ReferenceId to MatchKeyId per the architecture brief —
    /// this column will serve as the join key for the matching engine too,
    /// not just for classification.
    /// </summary>
    public required int[] MatchKeyId { get; init; }

    /// <summary>
    /// Transaction amount in minor units (scale 10^4, see
    /// MoneyMinorUnitsConverter). Used only by MismatchScan (phases 5/6).
    /// Stored separately so DuplicateScan/SplitConsolidatedScan never
    /// load this data into cache.
    /// </summary>
    public required long[] AmountMinor { get; init; }

    /// <summary>
    /// Value date as DateOnly.DayNumber (proleptic Gregorian day count,
    /// no custom epoch). Used only by MismatchScan (phase 6 date comparison).
    /// </summary>
    public required int[] DayNumber { get; init; }

    // CurrencyId intentionally deferred.
    // public required ushort[] CurrencyId { get; init; }
    // Added when currency-aware minor-unit scaling lands (separate item).

    /// <summary>
    /// Generic per-row execution state — one byte per row, meaning defined
    /// by the currently executing operator. During classification, interpreted
    /// via ClassificationStateBytes constants. Must not be exposed or
    /// persisted outside the engine; translate to stable domain types before
    /// returning results.
    /// </summary>
    public required byte[] ProcessingState { get; init; }
}

/// <summary>
/// Byte constants for ProcessingState during the classification execution
/// phase. These are the classification operator's private interpretation of
/// the shared execution buffer — not an enum, not a public type.
///
/// Values match the ClassificationState enum's byte values (which is
/// : byte) so that existing ClassificationState-typed code can be migrated
/// incrementally: cast (ClassificationState)batch.ProcessingState[i] still
/// works during transition, but the hot path uses these constants directly.
/// </summary>
internal static class ClassificationStateBytes
{
    public const byte None = 0;
    public const byte DuplicateInSource = 1;
    public const byte DuplicateInTarget = 2;
    public const byte SplitPayment = 3;
    public const byte ConsolidatedPayment = 4;
    public const byte AmountMismatch = 5;
    public const byte DateMismatch = 6;
    public const byte MissingInTarget = 7;
    public const byte MissingInSource = 8;
}
