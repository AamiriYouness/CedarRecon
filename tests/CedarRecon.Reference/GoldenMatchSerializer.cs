using CedarRecon.Core.Entities;
using CedarRecon.Core.Enums;

namespace CedarRecon.Reference;

/// <summary>
/// Comparable snapshot of a single MatchedPair, stripped of anything
/// non-deterministic (raw Transaction.Id GUIDs) or incidental to matching
/// correctness (Description, SourceFileName, SourceRowNumber). Same
/// design rationale as classification's golden DTOs — see
/// GoldenUnmatchedTransaction.
/// </summary>
public sealed record GoldenMatch(
    string SourceReference, decimal SourceAmount,
    string TargetReference, decimal TargetAmount,
    MatchStrategy Strategy, decimal Confidence);

public static class GoldenMatchSerializer
{
    /// <summary>
    /// Converts real MatchedPair results into a stable, comparable form.
    /// Ordering key: (SourceReference, SourceAmount, TargetReference) —
    /// three keys because a single source leg can legitimately match
    /// multiple target legs at different amounts (partial/split matches),
    /// so SourceReference+SourceAmount alone isn't always unique.
    /// </summary>
    public static IReadOnlyList<GoldenMatch> ToComparable(IReadOnlyList<MatchedPair> matches) =>
        matches
            .Select(m => new GoldenMatch(
                m.Source.NormalizedReference.Value, m.Source.Amount.Amount,
                m.Target.NormalizedReference.Value, m.Target.Amount.Amount,
                m.Strategy, m.Score.Value))
            .OrderBy(m => m.SourceReference)
            .ThenBy(m => m.SourceAmount)
            .ThenBy(m => m.TargetReference)
            .ToList();
}
