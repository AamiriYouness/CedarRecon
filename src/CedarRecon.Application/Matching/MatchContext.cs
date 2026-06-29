using CedarRecon.Domain.Entities;
using System.Collections.Concurrent;

namespace CedarRecon.Application.Matching;

/// <summary>
/// Shared mutable state for one reconciliation run.
/// Passed by reference to each strategy so they can atomically claim targets
/// without coupling to the engine or each other.
///
/// Thread-safe: TryClaim uses ConcurrentDictionary CAS — safe for parallel Match() calls.
/// </summary>
public sealed class MatchContext
{
    private readonly ConcurrentDictionary<Guid, bool> _claimedIds = new();
    private readonly List<Transaction> _allTargets = [];

    public void RegisterTarget(Transaction tx) => _allTargets.Add(tx);

    /// <summary>
    /// Atomically mark a target as consumed.
    /// Returns true only if this caller won the race — false means another thread claimed it first.
    /// </summary>
    public bool TryClaim(Transaction target) =>
        _claimedIds.TryAdd(target.Id.Value, true);

    public bool IsClaimed(Transaction target) =>
        _claimedIds.ContainsKey(target.Id.Value);

    /// <summary>
    /// All targets that were never claimed by any strategy.
    /// Call after all source transactions are processed.
    /// </summary>
    public IReadOnlyList<Transaction> GetUnmatchedTargets() =>
        _allTargets.Where(t => !_claimedIds.ContainsKey(t.Id.Value)).ToList();
}
