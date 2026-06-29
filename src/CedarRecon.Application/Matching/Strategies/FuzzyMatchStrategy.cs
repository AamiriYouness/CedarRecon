using CedarRecon.Domain;
using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Enums;
using CedarRecon.Domain.ValueObjects;

namespace CedarRecon.Application.Matching.Strategies;

/// <summary>
/// Strategy 2: tolerance-based amount + date matching.
/// Scores all viable candidates, then claims highest-scoring one that's still available.
///
/// If that candidate was claimed between score and TryAdd, the match was silently dropped.
/// Now we iterate scored candidates in descending order and TryAdd each — first claim wins.
/// </summary>
public sealed class FuzzyMatchStrategy : IMatchStrategy
{
    public MatchStrategy Strategy => MatchStrategy.Fuzzy;

    public MatchedPair? TryMatch(
        Transaction source,
        IReadOnlyList<Transaction> candidates,
        ReconciliationOptions options,
        MatchContext context)
    {
        var tolerance = options.DefaultToleranceRule;

        // Score all viable candidates — O(k) where k = bucket size, typically tiny
        var scored = candidates
            .Where(c =>
                !context.IsClaimed(c) &&
                tolerance.IsAmountWithinTolerance(source.Amount.Amount, c.Amount.Amount) &&
                tolerance.IsDateWithinTolerance(source.ValueDate, c.ValueDate))
            .Select(c => (candidate: c, score: CalculateScore(source, c, tolerance, options)))
            .Where(x => x.score >= options.FuzzyMatchMinConfidence)
            .OrderByDescending(x => x.score);

        foreach (var (candidate, score) in scored)
        {
            if (context.TryClaim(candidate))
                return new MatchedPair(
                    source, candidate,
                    ConfidenceScore.Of(score),
                    Strategy);
        }

        return null;
    }

    private static decimal CalculateScore(
        Transaction source,
        Transaction candidate,
        ToleranceRule tolerance,
        ReconciliationOptions options)
    {
        var score = options.FuzzyMatchMaxConfidence;

        var amountDiff = Math.Abs(source.Amount.Amount - candidate.Amount.Amount);
        if (amountDiff > 0m && tolerance.AbsoluteTolerance > 0m)
            score -= (amountDiff / tolerance.AbsoluteTolerance) * 0.15m;

        var daysDiff = Math.Abs(
            (source.ValueDate.UtcDateTime.Date - candidate.ValueDate.UtcDateTime.Date).TotalDays);
        if (daysDiff > 0 && tolerance.DateWindowDays > 0)
            score -= (decimal)(daysDiff / tolerance.DateWindowDays) * 0.10m;

        return Math.Clamp(score, options.FuzzyMatchMinConfidence, options.FuzzyMatchMaxConfidence);
    }
}
