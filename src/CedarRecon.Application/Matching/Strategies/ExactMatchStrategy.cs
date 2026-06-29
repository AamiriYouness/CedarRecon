using CedarRecon.Domain;
using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Enums;
using CedarRecon.Domain.ValueObjects;
using System.Runtime.CompilerServices;

namespace CedarRecon.Application.Matching.Strategies;

/// <summary>
/// Strategy 1: amount + currency + date must match exactly.
/// Highest confidence, first in the cascade.
/// </summary>
public sealed class ExactMatchStrategy : IMatchStrategy
{
    public MatchStrategy Strategy => MatchStrategy.Exact;

    public MatchedPair? TryMatch(
        Transaction source,
        IReadOnlyList<Transaction> candidates,
        ReconciliationOptions options,
        MatchContext context)
    {
        foreach (var candidate in candidates)
        {
            if (!IsExactMatch(source, candidate))
                continue;

            // TryAdd is the only gate — ContainsKey check before it is a TOCTOU race, skip it
            if (context.TryClaim(candidate))
                return new MatchedPair(
                    source, candidate,
                    ConfidenceScore.Of(options.ExactMatchConfidence),
                    Strategy);
        }

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsExactMatch(Transaction source, Transaction target) =>
        source.Amount.Amount == target.Amount.Amount &&
        string.Equals(source.Amount.Currency, target.Amount.Currency, StringComparison.Ordinal) &&
        source.ValueDate.UtcDateTime.Date == target.ValueDate.UtcDateTime.Date;
}
