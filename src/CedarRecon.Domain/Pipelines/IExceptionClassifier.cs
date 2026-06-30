using CedarRecon.Domain.Entities;

namespace CedarRecon.Domain.Pipelines;

/// <summary>
/// Classifies unmatched transactions into specific DiscrepancyTypes.
/// Input: raw matched/unmatched results. Output: enriched with DiscrepancyType.
/// </summary>
public interface IExceptionClassifier
{
    IReadOnlyList<UnmatchedTransaction> Classify(
        IReadOnlyList<Transaction> unmatchedSource,
        IReadOnlyList<Transaction> unmatchedTarget,
        IReadOnlyList<MatchedPair> matchedPairs);
}
