using CedarRecon.Domain;
using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Enums;
using CedarRecon.Domain.ValueObjects;

namespace CedarRecon.Application.Matching.Strategies;

// <summary>
/// Strategy 3: partial/split detection.
///
/// Matches a source to a single target whose amount is a clean integer divisor of source.
/// e.g. source=900, candidate=300 → valid (900 ÷ 300 = 3, no remainder).
///
/// The modulo check is the hot operation on this path.
/// Three implementations are available via <see cref="ModuloResolver"/>:
///   - Decimal       → correctness reference, ~48 ns
///   - ScaledLong    → default, ~3 ns, assumes ≤4 decimal places
///
/// See ModuloResolver.cs and Benchmarks/ModuloBenchmark.cs for numbers.
///
/// Known limitation: this is a heuristic — it does NOT sum multiple targets.
/// Real aggregate (subset-sum) is a separate opt-in strategy.
/// </summary>
public sealed class PartialMatchStrategy : IMatchStrategy
{
    /// <summary>
    /// Default: ScaledLong — single CPU div instruction, ~15x faster than decimal.
    /// Inject a different resolver via constructor for testing or benchmarking.
    /// </summary>
    private readonly ModuloResolver.IsEvenlyDivisible _isDivisible;

    public PartialMatchStrategy(
        ModuloResolver.IsEvenlyDivisible? moduloResolver = null)
    {
        // Default to Decimal — fastest safe implementation for our domain
        _isDivisible = moduloResolver ?? ModuloResolver.Decimal;
    }

    public MatchStrategy Strategy => MatchStrategy.PartialMatch;

    public MatchedPair? TryMatch(Transaction source,
        IReadOnlyList<Transaction> candidates,
        ReconciliationOptions options,
        MatchContext context)
    {
        var srcAbs = Math.Abs(source.Amount.Amount);

        foreach (var candidate in candidates)
        {
            if (context.IsClaimed(candidate))
                continue;

            var candAbs = Math.Abs(candidate.Amount.Amount);

            if (!IsPartialSplit(srcAbs, candAbs))
                continue;

            if (context.TryClaim(candidate))
                return new MatchedPair(
                    source, candidate,
                    ConfidenceScore.Of(options.PartialMatchMinConfidence),
                    Strategy);
        }

        return null;
    }

    /// <summary>
    /// Guards: zero, equal, larger-than-source.
    /// Actual divisibility check is delegated to the injected resolver.
    /// </summary>
    private bool IsPartialSplit(decimal srcAbs, decimal candAbs) =>
        candAbs != 0m &&
        candAbs < srcAbs &&
        _isDivisible(srcAbs, candAbs);
}
