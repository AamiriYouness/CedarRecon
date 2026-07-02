using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Enums;

namespace CedarRecon.Application.Classification.Indexed;

/// <summary>
/// The result of a columnar classification run — owns the two
/// ColumnarTransactionBatch instances (which include ProcessingState) and
/// the original input lists needed for lazy Transaction rehydration.
///
/// Non-generic (contrast with the prior generic ClassificationResult
/// TSourceView/TTargetView): with ISortedRowView retired from the main
/// classifier path, there is no sort-strategy type parameter to thread
/// through. ColumnarTransactionBatch is the one concrete batch type.
///
/// GetResults() enumerates lazily — one UnmatchedTransaction per MoveNext(),
/// never the full set at once. ToList() forces eager materialization for
/// callers that need all results in memory (backward-compatible with the old
/// IExceptionClassifier return type).
///
/// ProcessingState translation: GetResults() translates Source.ProcessingState
/// and Target.ProcessingState bytes into DiscrepancyType values using
/// ClassificationStateBytes constants — the stable domain type is only
/// constructed at enumeration time, not during scanning.
/// </summary>
public sealed class ClassificationResult
{
    public required ColumnarTransactionBatch Source { get; init; }
    public required ColumnarTransactionBatch Target { get; init; }
    public required IReadOnlyList<Transaction> SourceOriginals { get; init; }
    public required IReadOnlyList<Transaction> TargetOriginals { get; init; }

    /// <summary>
    /// Lazily enumerates all classified rows as UnmatchedTransaction objects.
    /// Source rows first (sort-order 0..Source.Count-1), then target rows.
    /// Safe to enumerate multiple times — each call is a fresh iterator over
    /// the same already-computed ProcessingState arrays.
    /// </summary>
    public IEnumerable<UnmatchedTransaction> GetResults()
    {
        for (var i = 0; i < Source.Count; i++)
        {
            var original = SourceOriginals[Source.OriginalIndex[i]];
            yield return new UnmatchedTransaction(
                original,
                ToDiscrepancyType(Source.ProcessingState[i]));
        }

        for (var i = 0; i < Target.Count; i++)
        {
            var original = TargetOriginals[Target.OriginalIndex[i]];
            yield return new UnmatchedTransaction(
                original,
                ToDiscrepancyType(Target.ProcessingState[i]));
        }
    }

    /// <summary>
    /// Forces full materialization — equivalent to the old eager
    /// IReadOnlyList&lt;UnmatchedTransaction&gt; return from IExceptionClassifier.
    /// Prefer GetResults() for streaming consumers.
    /// </summary>
    public IReadOnlyList<UnmatchedTransaction> ToList() => [.. GetResults()];

    private static DiscrepancyType ToDiscrepancyType(byte state) => state switch
    {
        ClassificationStateBytes.DuplicateInSource => DiscrepancyType.DuplicateInSource,
        ClassificationStateBytes.DuplicateInTarget => DiscrepancyType.DuplicateInTarget,
        ClassificationStateBytes.SplitPayment => DiscrepancyType.SplitPayment,
        ClassificationStateBytes.ConsolidatedPayment => DiscrepancyType.ConsolidatedPayment,
        ClassificationStateBytes.AmountMismatch => DiscrepancyType.AmountMismatch,
        ClassificationStateBytes.DateMismatch => DiscrepancyType.DateMismatch,
        ClassificationStateBytes.MissingInTarget => DiscrepancyType.MissingInTarget,
        ClassificationStateBytes.MissingInSource => DiscrepancyType.MissingInSource,
        ClassificationStateBytes.None => throw new InvalidOperationException(
            "A row reached result enumeration still in None state — every row must be " +
            "classified by phase 7 or 8 at the latest. This indicates a bug in the cascade."),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state,
            "Unknown ProcessingState byte value during classification result enumeration."),
    };
}