using CedarRecon.Domain.Entities;

namespace CedarRecon.Application.Classification.Indexed;

/// <summary>
/// Streaming counterpart to IExceptionClassifier — returns a
/// ClassificationResult that owns its state arrays and supports lazy
/// enumeration, rather than forcing full UnmatchedTransaction materialization
/// upfront.
///
/// Worklist item 5, Option B: added as a PARALLEL interface alongside
/// IExceptionClassifier rather than replacing it. IExceptionClassifier and
/// its existing equivalence tests (ExceptionClassifierEquivalenceTests, 28
/// tests) are preserved unchanged — they remain the correctness gate for
/// the entire indexed-engine investigation and must not be broken while the
/// materialization path is being redesigned.
///
/// IndexedExceptionClassifier implements both interfaces during the transition:
///   IExceptionClassifier        — old eager path, delegates to the new path
///   IStreamingExceptionClassifier — new lazy path, primary implementation
///
/// Generic over TSourceView/TTargetView: consistent with
/// ReferenceIndex&lt;TSourceView, TTargetView&gt; and
/// IndexedExceptionClassifier&lt;TSourceView, TTargetView&gt; — the sort strategy
/// flows through the type system without boxing or virtual dispatch on the
/// hot path.
/// </summary>
public interface IStreamingExceptionClassifier
{
    /// <summary>
    /// Classifies unmatched transactions and returns a ClassificationResult
    /// that owns the computed state. The result can be enumerated lazily via
    /// GetResults(), enumerated multiple times, or materialized eagerly via
    /// ToList() — the classifier runs exactly once regardless of how many
    /// times the result is consumed downstream.
    /// </summary>
    ClassificationResult ClassifyStreaming(
        IReadOnlyList<Transaction> unmatchedSource,
        IReadOnlyList<Transaction> unmatchedTarget,
        IReadOnlyList<MatchedPair> matchedPairs);
}