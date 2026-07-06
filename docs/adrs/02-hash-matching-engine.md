# ADR-02 — Hash-Based O(n) Matching Engine

**Status**: Accepted  
**Date**: 2026  
**Deciders**: Youness (Senior .NET Engineer, CedarRecon)

---

## Context

Financial reconciliation requires matching every source transaction against
a set of target transactions. The naive approach — compare each source to
every target — is O(n²). At N=1M transactions, O(n²) means one trillion
comparisons. Even at 1 nanosecond per comparison, that is 1,000 seconds
per reconciliation run. Not acceptable for any production system.

The matching engine needs to be:
- **O(n) amortized** — each transaction processed once, not n times
- **Streaming-capable** — target transactions arrive as `IAsyncEnumerable`,
  not as a fully-loaded list. Memory is bounded regardless of dataset size.
- **Thread-safe for concurrent matching** — source transactions can be
  matched in parallel after the index is built
- **Correct under race conditions** — two threads must not claim the same
  target transaction

---

## Decision

**Build a hash index over all target transactions keyed by a composite
`IndexKey`, then match each source transaction via O(1) dictionary lookup
followed by a small strategy cascade over the matching bucket.**

### IndexKey

```
IndexKey = NormalizedReference.Value + "|" + Amount.Currency
```

Only transactions sharing the same `IndexKey` are ever compared against
each other. A source with reference `REF-001` and currency `MAD` is never
compared to a target with reference `REF-001` and currency `EUR` — they
have different index keys and land in different buckets.

The `|` separator is a domain choice — it prevents collisions between
reference values that are prefixes of each other (`REF-1` + `EUR` must
not hash to the same key as `REF` + `1|EUR`).

### Two-phase execution

**Phase 1 — BuildTargetIndexAsync** (single-threaded, streaming):

```
IAsyncEnumerable<Transaction>
    → RegisterTarget in MatchContext
    → Insert into Dictionary<string, List<Transaction>>
    → Swap _targetIndex and _context as a unit
```

The index is built by a single writer. `IAsyncEnumerable` means the engine
never holds more than a working set of transactions in memory at once —
backpressure is applied naturally. Pre-sizing the dictionary to
`expectedCount` (default 65,536) avoids rehashing on growth.

**Phase 2 — Match** (concurrent-safe):

```
source.IndexKey
    → O(1) dictionary lookup → bucket (k candidates)
    → Strategy cascade over bucket only
    → First non-null result wins
```

After `BuildTargetIndexAsync` completes, `_targetIndex` is read-only.
Concurrent `Match()` calls are safe — multiple threads read the same
immutable index simultaneously.

### Atomic state swap

`_targetIndex` and `_context` are always swapped together at the end of
`BuildTargetIndexAsync`. There is no window where the new index is visible
but the old context is still in use. Any `Match()` call after the swap
sees a consistent pair of state objects.

---

## Consequences

### Performance

| Operation | Complexity | Notes |
|---|---|---|
| BuildTargetIndexAsync | O(n) | One pass, streaming |
| Match (lookup) | O(1) | Dictionary by IndexKey |
| Match (cascade) | O(k) | k = bucket size, typically 1–5 |
| Full run | O(n) amortized | O(n·k) worst case |

Worst case O(n·k) occurs when all transactions share the same `IndexKey`
(same reference and currency). In practice, buckets are small because
`IndexKey` includes both reference and currency — most references are
unique or near-unique within a reconciliation dataset.

### Correctness under concurrency

`MatchContext` uses `ConcurrentDictionary<Guid, bool>` for claim tracking.
`TryClaim` calls `TryAdd` — a single atomic operation. There is no
`ContainsKey` check before `TryAdd`. A `ContainsKey` + `TryAdd` sequence
would be a TOCTOU race: two threads could both observe the target as
unclaimed, both proceed, and one would silently lose the claim. The single
`TryAdd` eliminates this race entirely. The losing thread receives `false`
and continues to the next candidate.

### Known limitation — IndexKey encodes domain assumptions

`IndexKey = NormalizedReference + "|" + Currency` works for banking
reconciliation where a reference reliably identifies a transaction group
and currency is always present and consistent.

It does not work for:
- Insurance claims (reference formats differ between cedant and reinsurer)
- Card transactions (IBAN is the join key, not a reference number)
- ERP intercompany (cost center + GL account + period is the join key)
- Treasury FX (trade date + counterparty + currency pair)

This limitation is intentional and scoped. CedarRecon v0.9.x targets
banking reconciliation. v1.1.0 introduces `ReconRecord` and a configurable
`MatchKeyDefinition` that allows operators to declare which fields constitute
the join key for their specific domain. See ADR-06 (planned).

---

## Rejected Alternatives

### Nested foreach — O(n²)

```csharp
foreach (var source in sources)
    foreach (var target in targets)
        TryMatch(source, target);
```

Correct, simple, catastrophically slow at scale. At N=1M, one trillion
comparisons. Rejected unconditionally — this is the pattern the architecture
brief explicitly forbids ("NEVER O(n²). If you see a nested foreach over
all targets, flag immediately").

### Sort-merge join — O(n log n)

Sort both source and target by reference, then walk both arrays in lockstep.
Better than O(n²), but O(n log n) instead of O(n). Also requires loading
all transactions into memory simultaneously — incompatible with streaming
ingestion. Rejected in favor of the hash approach.

### Database-side join

Offload matching to a SQL JOIN. Correct, but introduces a hard dependency
on a database for every reconciliation run. Incompatible with the in-memory
architecture (ADR-01) and the correctness requirement that results must not
depend on infrastructure. Rejected — matching is a pure computational
problem, not a persistence problem.

---

## References

- `src/CedarRecon.Application/Matching/HashMatchingEngine.cs`
- `src/CedarRecon.Application/Matching/MatchContext.cs`
- `docs/matching-engine.md`
