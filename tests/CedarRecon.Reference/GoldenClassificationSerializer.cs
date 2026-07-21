using CedarRecon.Core.Entities;
using CedarRecon.Core.Enums;

namespace CedarRecon.Reference;

/// <summary>
/// Comparable snapshot of a single UnmatchedTransaction classification
/// result — same GUID/incidental-field stripping rationale as GoldenMatch.
/// ValueDateOffsetDays is relative to a caller-supplied base date, not an
/// absolute DateTimeOffset, purely for golden-JSON readability (given
/// ScenarioBuilder's fixed BaseDate, the underlying value is already fully
/// deterministic — this is a legibility choice, not a correctness one).
/// </summary>
public sealed record GoldenUnmatchedTransaction(
    string Reference, decimal Amount, string Currency,
    int ValueDateOffsetDays, DiscrepancyType Reason);

public static class GoldenClassificationSerializer
{
    /// <summary>
    /// Ordering key: (Reference, Reason, Amount). Amount is a necessary
    /// third key, not just Reference+Reason — DuplicateInSource/
    /// DuplicateInTarget legitimately produce multiple rows for the same
    /// reference, so the first two keys alone aren't always unique.
    /// </summary>
    public static IReadOnlyList<GoldenUnmatchedTransaction> ToComparable(
        IReadOnlyList<UnmatchedTransaction> results, DateTimeOffset baseDate) =>
        results
            .Select(r => new GoldenUnmatchedTransaction(
                r.Transaction.NormalizedReference.Value,
                r.Transaction.Amount.Amount,
                r.Transaction.Amount.Currency,
                (r.Transaction.ValueDate - baseDate).Days,
                r.Reason))
            .OrderBy(r => r.Reference)
            .ThenBy(r => r.Reason)
            .ThenBy(r => r.Amount)
            .ToList();
}