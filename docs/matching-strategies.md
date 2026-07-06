# Matching Strategies

## Overview

CedarRecon uses a composable strategy pipeline. Each strategy is stateless
and independently testable. The engine tries them in order — first non-null
result wins. All claim logic flows through `MatchContext`, never through the
strategy itself.

```
ExactMatchStrategy     → confidence 1.00        (all fields match)
FuzzyMatchStrategy     → confidence 0.70–0.99   (within tolerance, scored)
PartialMatchStrategy   → confidence 0.50–0.69   (divisor relationship)
```

---

## IMatchStrategy

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

**Contract:**
- Return a claimed `MatchedPair` or `null`
- Use `context.TryClaim(candidate)` to consume a target — never mutate
  shared state directly
- Implementations must be stateless — all run state lives in `MatchContext`
- Candidates are always pre-filtered by `IndexKey` (same reference + currency)
  — strategies never see the full target set

---

## Strategy 1 — ExactMatchStrategy

**Confidence**: 1.00 (configurable via `ExactMatchConfidence`)

Requires all three fields to match exactly:

```
Amount.Amount    ==   target.Amount.Amount       (decimal equality)
Amount.Currency  ==   target.Amount.Currency     (ordinal string equality)
ValueDate (UTC date) == target.ValueDate (UTC date)
```

Date comparison uses `.UtcDateTime.Date` — time component is ignored.
Two transactions on the same UTC calendar day always match on date regardless
of the time they were booked.

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static bool IsExactMatch(Transaction source, Transaction target) =>
    source.Amount.Amount == target.Amount.Amount &&
    string.Equals(source.Amount.Currency, target.Amount.Currency, StringComparison.Ordinal) &&
    source.ValueDate.UtcDateTime.Date == target.ValueDate.UtcDateTime.Date;
```

`AggressiveInlining` is applied because this method is on the inner loop of
every exact match scan. The JIT would likely inline it anyway — the attribute
makes the intent explicit and ensures it survives future refactoring.

**Claim logic**: iterates candidates, calls `context.TryClaim()` on the
first match. No pre-filtering — the bucket is already small enough that
a sequential scan is faster than any pre-filter overhead.

---

## Strategy 2 — FuzzyMatchStrategy

**Confidence**: 0.70–0.99 (configurable via `FuzzyMatchMinConfidence` /
`FuzzyMatchMaxConfidence`)

Matches transactions within a configured tolerance window, scored by how
close the amount and date are to exact.

### Tolerance check

Both conditions must hold:

```
|source.Amount - candidate.Amount| ≤ AbsoluteTolerance
|source.ValueDate - candidate.ValueDate| ≤ DateWindowDays days
```

`ToleranceRule.Standard` defaults:
- `AbsoluteTolerance`: 0.01
- `PercentageTolerance`: 0.001 (not currently used in scoring, reserved)
- `DateWindowDays`: 2

### Scoring

```
score = FuzzyMatchMaxConfidence (0.99)
      − (amountDiff / AbsoluteTolerance) × 0.15
      − (daysDiff  / DateWindowDays)     × 0.10
```

Score is clamped to `[FuzzyMatchMinConfidence, FuzzyMatchMaxConfidence]`.
Candidates below `FuzzyMatchMinConfidence` are excluded before scoring.

A candidate exactly at tolerance limits scores approximately:
- Amount at full tolerance: −0.15 → score ~0.84
- Date at full window:      −0.10 → score ~0.89
- Both at limits:           −0.25 → score ~0.74 (above 0.70 minimum)

### Claim logic

All viable candidates are scored first, then sorted descending by score.
The engine iterates in score order, calling `TryClaim` on each until one
succeeds. This handles concurrent races correctly: if the highest-scoring
candidate was claimed between scoring and `TryClaim`, the next-best candidate
is tried rather than the match being silently dropped.

```csharp
foreach (var (candidate, score) in scored)   // descending by score
{
    if (context.TryClaim(candidate))          // atomic — first claim wins
        return new MatchedPair(...);
}
```

### ⚠️ Open item — Reference similarity algorithm

The current implementation matches on `IndexKey` (reference + currency) which
means fuzzy matching only applies to amount and date — not to the reference
string itself. Two transactions with similar but not identical references
(e.g. `REF-001` vs `REF-0001`, or `INVOICE/2026/001` vs `INV-2026-001`)
will never be candidates for each other.

A reference similarity algorithm is planned to address this. Candidates
under consideration:

| Algorithm | Characteristics |
|---|---|
| Levenshtein distance | Edit distance, good for typos and truncation |
| Jaro-Winkler | Weighted for prefix matches, good for reference codes |
| Soundex / Metaphone | Phonetic, less useful for financial references |
| N-gram similarity | Good for substring matches and reordered tokens |

The chosen algorithm will be configurable via `ReconciliationOptions` and
injectable via `IMatchStrategy` constructor, following the same pattern as
`ModuloResolver` in `PartialMatchStrategy`. This will also require a change
to `IndexKey` or a secondary index keyed on reference-only (without currency)
to surface cross-currency reference candidates.

**Not yet implemented.** Tracked as a backlog item.

---

## Strategy 3 — PartialMatchStrategy

**Confidence**: 0.50 (configurable via `PartialMatchMinConfidence`)

Detects split payment relationships: one source amount that is evenly
divisible by a candidate amount.

```
source.Amount = 900
candidate.Amount = 300
→ 900 ÷ 300 = 3 (no remainder) → valid partial match
```

This is a heuristic. It detects the one-to-one leg of a split — a source
matched to one of its target components. It does NOT sum multiple targets.
Full aggregate (subset-sum) matching is a separate planned strategy.

### Guards

```csharp
private bool IsPartialSplit(decimal srcAbs, decimal candAbs) =>
    candAbs != 0m        &&   // avoid division by zero
    candAbs < srcAbs     &&   // candidate must be smaller than source
    _isDivisible(srcAbs, candAbs);
```

Only candidates strictly smaller than the source are considered. A candidate
equal to or larger than the source is not a split leg.

### ModuloResolver — pluggable divisibility check

The divisibility check is the hot operation on this path. Three
implementations are available:

| Resolver | Implementation | Performance | Notes |
|---|---|---|---|
| `Decimal` | `srcAbs % candAbs == 0m` | ~48 ns | Reference — safe for all inputs |
| `ScaledLong` | Scale to long, integer mod | ~3 ns | 15× faster — assumes ≤4 decimal places |
| `UnsafeMantissa` | Direct mantissa bit ops | fastest | Unsafe — experimental only |

**Default**: `Decimal` — correct for all inputs, no decimal-place assumption.

From the benchmarks (`ModuloBenchmark.cs`): `ScaledLong` is ~15× faster but
was rejected for the modulo use case specifically because its 3.36× speed
advantage did not materialize in whole-pipeline benchmarks — the operation
is fast enough that it is not the bottleneck. `Decimal` is kept as default
for correctness safety. See `docs/07-index-build-algorithms.md` for the full
investigation.

```csharp
// Inject for benchmarking or testing
new PartialMatchStrategy(ModuloResolver.ScaledLong);

// Production default
new PartialMatchStrategy();   // uses ModuloResolver.Decimal
```

### Known limitation

This strategy matches one source to one target leg only. A source of 900
split into targets of 300, 300, and 300 will match the first unclaimed 300
it finds, leaving the other two as `MissingInSource`. Full many-to-one
aggregate matching requires the planned aggregate strategy (v2.3.0).

---

## Adding a New Strategy

1. Implement `IMatchStrategy` — stateless, all state in `MatchContext`
2. Register in `MatchStrategyFactory.CreateDefault()` at the correct position
3. Add confidence band constants to `ReconciliationOptions`
4. Unit test: strategy claims correctly, is stateless, handles concurrent TryClaim races
5. Update this document

### Strategy positioning rules

- Higher confidence strategies must come earlier in the pipeline
- A strategy must not claim targets that a higher-confidence strategy
  could still match — this would permanently remove them from consideration
- When in doubt, add after existing strategies and raise confidence threshold
  only after benchmarking on real data

---

## Strategy Configuration Reference

All thresholds in `appsettings.json` under `"Reconciliation"`:

```json
{
  "Reconciliation": {
    "ExactMatchConfidence": 1.0,
    "FuzzyMatchMaxConfidence": 0.99,
    "FuzzyMatchMinConfidence": 0.70,
    "PartialMatchMaxConfidence": 0.69,
    "PartialMatchMinConfidence": 0.50,
    "MinimumConfidenceThreshold": 0.50,
    "DefaultToleranceRule": {
      "AbsoluteTolerance": 0.01,
      "PercentageTolerance": 0.001,
      "DateWindowDays": 2
    }
  }
}
```
