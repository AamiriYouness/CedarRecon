# Matching Engine

## Overview

`HashMatchingEngine` is an O(n) hash-based matching engine. It indexes all
target transactions once by a composite key, then matches each source
transaction via an O(1) dictionary lookup followed by a small strategy
cascade over the matching bucket.

**Never O(n²).** If you see a nested loop over all targets, it is a bug.

---

## Architecture

```
BuildTargetIndexAsync()
    │
    ├── O(n) scan of all target transactions
    ├── Register each target in MatchContext
    └── Build Dictionary<string, List<Transaction>>
            keyed by Transaction.IndexKey
            = NormalizedReference.Value + "|" + Amount.Currency

Match(source)
    │
    ├── O(1) dictionary lookup by source.IndexKey
    ├── No candidates found → MissingInTarget
    └── Candidates found → Strategy cascade
            ExactMatchStrategy
                ↓ null
            FuzzyMatchStrategy
                ↓ null
            PartialMatchStrategy
                ↓ null
            Unmatched → MissingInTarget

GetUnmatchedTargets()
    └── All targets never claimed by any strategy
```

---

## IndexKey

Every transaction carries an `IndexKey` computed once at construction:

```
IndexKey = NormalizedReference.Value + "|" + Amount.Currency
```

Only transactions sharing the same `IndexKey` are ever compared against each
other. A source transaction with reference `REF-001` and currency `MAD` will
never be compared to a target with reference `REF-001` and currency `EUR` —
they have different `IndexKey` values and land in different buckets.

This is what makes the engine O(n) amortized: the strategy cascade runs on
a small bucket (k candidates), not on all n targets.

---

## Two-Phase Execution

### Phase 1 — BuildTargetIndexAsync

```csharp
await engine.BuildTargetIndexAsync(targetTransactions, expectedCount, ct);
```

- Iterates `IAsyncEnumerable<Transaction>` — streaming, no full load into memory
- `expectedCount` pre-sizes the dictionary to avoid rehashing (default 65,536)
- Single-threaded write — no concurrent access during build
- `_targetIndex` and `_context` are swapped as a unit at the end of build —
  no partial state is ever visible to concurrent `Match()` callers

**Must be awaited before any `Match()` calls.** The engine does not enforce
this with a lock — it is a contract enforced by call order.

### Phase 2 — Match

```csharp
MatchResult result = engine.Match(source, options);
```

- Thread-safe — safe to call from `Parallel.ForEachAsync` concurrently
- O(1) dictionary lookup
- Strategy cascade runs on the bucket only — typically 1-5 candidates
- First strategy that returns non-null wins; remaining strategies are skipped
- All claim state flows through `MatchContext` (lock-free CAS)

---

## MatchContext — Claim State

`MatchContext` owns all mutable state for one reconciliation run. It is the
only place where target transactions are marked as consumed.

```csharp
public sealed class MatchContext
{
    private readonly ConcurrentDictionary<Guid, bool> _claimedIds;
    private readonly List<Transaction> _allTargets;

    public void RegisterTarget(Transaction tx);   // called during index build
    public bool TryClaim(Transaction target);      // atomic CAS — returns true if this caller won
    public bool IsClaimed(Transaction target);     // read-only check
    public IReadOnlyList<Transaction> GetUnmatchedTargets();
}
```

### TryClaim — the only safe way to consume a target

```csharp
if (context.TryClaim(candidate))
    return new MatchedPair(source, candidate, score, Strategy);
```

`TryClaim` uses `ConcurrentDictionary.TryAdd` — a single atomic operation.
There is no `ContainsKey` check before it. A `ContainsKey` + `TryAdd`
sequence would be a TOCTOU race: two threads could both see the target as
unclaimed, both proceed, and one would silently lose the claim. The single
`TryAdd` eliminates that race entirely.

**Rule**: strategies must NEVER mutate shared state directly. All claim
logic flows through `MatchContext.TryClaim`. Strategies are stateless.

---

## Strategy Pipeline

Registered in `MatchStrategyFactory` — the only place that defines pipeline
order. To add a new strategy, register it here and nowhere else.

```csharp
// Default pipeline
public static IReadOnlyList<IMatchStrategy> CreateDefault() =>
[
    new ExactMatchStrategy(),    // confidence: 1.00
    new FuzzyMatchStrategy(),    // confidence: 0.70–0.99
    new PartialMatchStrategy(),  // confidence: 0.50–0.69
];
```

Order is critical. Earlier strategies have higher confidence and priority.
A target claimed by `ExactMatchStrategy` is never seen by `FuzzyMatchStrategy`.

### Custom pipelines

```csharp
// Swap the modulo resolver for benchmarking
MatchStrategyFactory.CreateWithModuloResolver(ModuloResolver.ScaledLong);

// Isolate a single strategy for testing
MatchStrategyFactory.Create(new ExactMatchStrategy());
```

---

## ConfidenceScore Bands

Defined in `ReconciliationOptions` — all tunables, no hardcoded values:

| Strategy | Min | Max | Meaning |
|---|---|---|---|
| Exact | 1.00 | 1.00 | All fields match exactly |
| Fuzzy | 0.70 | 0.99 | Within tolerance, scored by proximity |
| Partial | 0.50 | 0.69 | Divisor relationship detected |

`ConfidenceScore` is a `readonly record struct` enforcing [0.0, 1.0] at
construction. Invalid scores cannot propagate.

```csharp
public bool IsHighConfidence   => Value >= 0.90m;
public bool IsMediumConfidence => Value >= 0.70m && Value < 0.90m;
public bool IsLowConfidence    => Value <  0.70m;
```

---

## ReconciliationOptions

All engine tunables are bound from `appsettings.json`:

```json
{
  "Reconciliation": {
    "ExactMatchConfidence": 1.0,
    "FuzzyMatchMaxConfidence": 0.99,
    "FuzzyMatchMinConfidence": 0.70,
    "PartialMatchMinConfidence": 0.50,
    "DefaultToleranceRule": {
      "AbsoluteTolerance": 0.01,
      "PercentageTolerance": 0.001,
      "DateWindowDays": 2
    }
  }
}
```

No defaults are hardcoded in strategy logic. Every threshold reads from
`ReconciliationOptions` passed into `TryMatch`.

---

## Thread Safety

| Component | Thread safety |
|---|---|
| `HashMatchingEngine._targetIndex` | Read-only after build — safe for concurrent reads |
| `HashMatchingEngine._context` | Swapped atomically at end of build |
| `MatchContext._claimedIds` | `ConcurrentDictionary` — lock-free CAS |
| `MatchContext._allTargets` | Written only during build (single-threaded) |
| `IMatchStrategy` implementations | Stateless — safe for any concurrency |

---

## Performance Characteristics

| Operation | Complexity | Notes |
|---|---|---|
| `BuildTargetIndexAsync` | O(n) | Single scan, no sorting |
| `Match` (lookup) | O(1) | Dictionary lookup by IndexKey |
| `Match` (cascade) | O(k) | k = bucket size, typically small |
| Full run | O(n) amortized | O(n·k) worst case (all same IndexKey) |

Worst case O(n·k) is bounded by the number of transactions sharing the same
`IndexKey`. In practice, buckets are small (1–5 candidates) because
`IndexKey` includes both reference and currency.

---

## Extending the Engine

### Add a new strategy

1. Implement `IMatchStrategy`
2. Register in `MatchStrategyFactory.CreateDefault()` at the correct position
3. Add confidence band to `ReconciliationOptions`
4. Add unit test asserting the strategy claims correctly and is stateless

### Add a new index key field

`IndexKey` is computed on `Transaction`. Adding a field (e.g. counterparty)
makes the index more selective — smaller buckets, faster cascade — but
means transactions that differ only on that field will never be compared.
Consider the trade-off before changing `IndexKey`.
