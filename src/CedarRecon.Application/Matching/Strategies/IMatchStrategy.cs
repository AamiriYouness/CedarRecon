using CedarRecon.Domain;
using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Enums;
using Microsoft.Extensions.FileSystemGlobbing.Internal;

namespace CedarRecon.Application.Matching.Strategies;

/// <summary>
/// A single step in the match cascade.
/// Implementations are stateless — all mutable state lives in <see cref="MatchContext"/>.
/// Ordered by the factory; engine iterates and stops on first success.
/// </summary>
public interface IMatchStrategy
{
    /// <summary>Human-readable name used in logs and MatchedPair.Strategy.</summary>
    MatchStrategy Strategy { get; }

    /// <summary>
    /// Attempt to match <paramref name="source"/> against <paramref name="candidates"/>.
    /// Returns a claimed <see cref="MatchedPair"/> or null if this strategy cannot match.
    /// Implementations must use <see cref="MatchContext.TryClaim"/> to atomically
    /// consume a target — never mutate shared state directly.
    /// </summary>
    MatchedPair? TryMatch(
        Transaction source,
        IReadOnlyList<Transaction> candidates,
        ReconciliationOptions options,
        MatchContext context);
}
