# ADR-03 — Composable Strategy Pipeline with MatchContext CAS Claiming

**Status**: Accepted  
**Date**: 2026  
**Deciders**: Youness (Senior .NET Engineer, CedarRecon)

---

## Context

Financial reconciliation is not a single matching algorithm. Different
transaction types, different domains, and different data quality levels
require different matching approaches:

- **Exact match**: amount + currency + date all align — highest confidence
- **Fuzzy match**: within configured tolerance — scored by proximity
- **Partial match**: split payment detection — divisor relationship

These are not mutually exclusive options — they form a priority cascade.
A transaction that can be matched exactly should never be matched fuzzily.
A target claimed by exact matching must be permanently unavailable to fuzzy
and partial strategies, even under concurrent execution.

The design question is: how do strategies share knowledge of what has
already been claimed, without coupling to each other or to the engine?

---

## Decision

**Strategies are stateless. All claim state lives in `MatchContext`.
Strategies interact with the world only through `MatchContext.TryClaim`.**

### IMatchStrategy

```csharp
public interface IMatchStrategy
{
    MatchStrategy Strategy { get; }

    MatchedPair? TryMatch(
        Transaction source,
        IReadOnlyList<Transaction> candidates,
        ReconciliationOptions options,
        MatchContext context);
}
```

Each strategy receives:
- `source`: the transaction being matched
- `candidates`: pre-filtered bucket from the hash index (same IndexKey)
- `options`: all thresholds and tolerances — no hardcoded values
- `context`: the only channel for claiming targets

Each strategy returns a claimed `MatchedPair` or `null`. Returning `null`
means "I cannot match this source" — the engine tries the next strategy.

### MatchContext — single source of claim truth

```csharp
public sealed class MatchContext
{
    private readonly ConcurrentDictionary<Guid, bool> _claimedIds;

    public bool TryClaim(Transaction target) =>
        _claimedIds.TryAdd(target.Id.Value, true);

    public bool IsClaimed(Transaction target) =>
        _claimedIds.ContainsKey(target.Id.Value);
}
```

`TryClaim` is the **only** way to consume a target. It uses
`ConcurrentDictionary.TryAdd` — a single atomic CAS operation. There is
no intermediate "check then act" — only "act and observe the result."

A strategy that calls `TryClaim` and receives `false` knows another thread
claimed that target first. It continues to the next candidate. No lock,
no coordination, no silent data loss.

### MatchStrategyFactory — single registration point

```csharp
public static IReadOnlyList<IMatchStrategy> CreateDefault() =>
[
    new ExactMatchStrategy(),    // confidence 1.00 — tries first
    new FuzzyMatchStrategy(),    // confidence 0.70–0.99
    new PartialMatchStrategy(),  // confidence 0.50–0.69 — tries last
];
```

`MatchStrategyFactory` is the only place that knows which strategies exist
and in what order they run. Adding a new strategy means registering it here
and nowhere else. The engine, the context, and the existing strategies are
unaware of new strategies.

### Pipeline execution

```csharp
foreach (var strategy in _strategies)
{
    var pair = strategy.TryMatch(source, candidates, options, _context);
    if (pair is not null)
        return new MatchedResult(pair);
}
return Unmatched(source, DiscrepancyType.MissingInTarget);
```

First non-null result wins. The cascade stops immediately — remaining
strategies are never called for a source that has already been matched.

---

## Consequences

### Stateless strategies are independently testable

Any strategy can be tested in isolation by constructing a fresh
`MatchContext` and calling `TryMatch` directly. No engine setup, no
index build, no other strategies involved:

```csharp
var context = new MatchContext();
var strategy = new ExactMatchStrategy();
var result = strategy.TryMatch(source, candidates, options, context);
```

### TryClaim eliminates TOCTOU races

An earlier design had strategies check `IsClaimed` before attempting
`TryClaim`:

```csharp
// WRONG — TOCTOU race
if (!context.IsClaimed(candidate))
    if (context.TryClaim(candidate))   // another thread may have claimed between these two lines
        return ...
```

Two threads executing concurrently could both observe the target as
unclaimed at the `IsClaimed` check, both proceed to `TryClaim`, and one
would silently lose. The correct pattern is:

```csharp
// CORRECT — single atomic operation
if (context.TryClaim(candidate))
    return ...
```

`TryAdd` on `ConcurrentDictionary` is the atomic gate. The `IsClaimed`
check exists only for pre-filtering in `FuzzyMatchStrategy` (to avoid
scoring candidates that are already claimed) — it is never used as a
guard before `TryClaim`.

### Strategy ordering is a business rule

The order in `MatchStrategyFactory` encodes business priority. Exact matches
have higher confidence than fuzzy matches. A target that can be exactly
matched must not be consumed by a fuzzy strategy first. The factory enforces
this ordering — individual strategies have no knowledge of each other.

### New strategies require no changes to existing code

Adding `SemanticMatchStrategy` (planned for reference similarity, v1.1.0)
requires:
1. Implementing `IMatchStrategy`
2. Registering in `MatchStrategyFactory` at the correct position

No changes to `HashMatchingEngine`, `MatchContext`, or any existing strategy.

---

## Rejected Alternatives

### Strategies own their own claim sets

An earlier design had each strategy maintain its own
`HashSet<Guid>` of claimed targets:

```csharp
// REJECTED — strategies could not see each other's claims
public sealed class ExactMatchStrategy
{
    private readonly HashSet<Guid> _claimed = new();
}
```

`ExactMatchStrategy` could claim a target that `FuzzyMatchStrategy` had
already scored and was about to claim — or vice versa. Strategies had no
consistent view of what was available. Centralising in `MatchContext` fixed
this by making claim state a single shared truth.

### Engine coordinates between strategies

A design where the engine passes "already claimed" sets between strategy
calls, rebuilding the available candidate list before each strategy:

```csharp
var remaining = candidates.Where(c => !claimed.Contains(c.Id)).ToList();
var pair = strategy.TryMatch(source, remaining, options);
```

This allocates a new list per strategy per source transaction — O(k)
allocation on every cascade step. At N=1M with 3 strategies, that is
3M list allocations per run. `IsClaimed` on `ConcurrentDictionary` is
a single dictionary lookup with no allocation. The `MatchContext` approach
is both safer and faster.

### Lock-based claiming

Using `lock` around a `HashSet<Guid>` instead of `ConcurrentDictionary`:

```csharp
lock (_lock)
{
    if (!_claimed.Contains(id))
    {
        _claimed.Add(id);
        return true;
    }
    return false;
}
```

Correct under concurrency but serialises all claim operations. With
`Parallel.ForEachAsync` over N=1M source transactions, contention on a
single lock becomes a bottleneck. `ConcurrentDictionary.TryAdd` uses
fine-grained locking internally — it does not serialise unrelated keys.
Rejected in favour of the lock-free CAS approach.

---

## References

- `src/CedarRecon.Application/Matching/IMatchStrategy.cs`
- `src/CedarRecon.Application/Matching/MatchContext.cs`
- `src/CedarRecon.Application/Matching/MatchStrategyFactory.cs`
- `src/CedarRecon.Application/Matching/Strategies/ExactMatchStrategy.cs`
- `src/CedarRecon.Application/Matching/Strategies/FuzzyMatchStrategy.cs`
- `src/CedarRecon.Application/Matching/Strategies/PartialMatchStrategy.cs`
- `docs/matching-strategies.md`
