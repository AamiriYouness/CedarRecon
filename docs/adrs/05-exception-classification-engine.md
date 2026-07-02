# 05 — Exception Classification Engine

## Purpose

The exception classification engine takes the output of the matching engine
— a set of transactions that could not be fully matched — and assigns each
one a specific discrepancy type explaining why it was unmatched. This
classification is the primary input to downstream exception management,
audit reporting, and compliance workflows.

Classification is a pure function: given the same unmatched source
transactions, unmatched target transactions, and matched pairs, it always
produces the same result. There are no side effects, no external lookups,
and no stateful dependencies between classification runs.

---

## Domain Model

### Inputs

```csharp
IReadOnlyList<Transaction> unmatchedSource
IReadOnlyList<Transaction> unmatchedTarget
IReadOnlyList<MatchedPair> matchedPairs
```

`matchedPairs` is included even though those transactions are already
matched, because the matching engine may have partially matched a reference
group — e.g. one source transaction matched one of three target transactions
sharing the same reference. The remaining two unmatched targets can only be
correctly classified (as `SplitPayment` or `ConsolidatedPayment`) if the
engine knows how many legs were already matched.

### Output

```csharp
IReadOnlyList<UnmatchedTransaction>   // eager, backward-compatible
ClassificationResult                  // streaming, owns state arrays
```

Every input transaction appears in the output exactly once. The total output
count equals `unmatchedSource.Count + unmatchedTarget.Count`.

### DiscrepancyType

Eight values, assigned by the cascade in strict priority order:

| Value | Side | Condition |
|---|---|---|
| `DuplicateInSource` | Source | Multiple source rows share the same reference |
| `DuplicateInTarget` | Target | Multiple target rows share the same reference |
| `SplitPayment` | Source | One source leg, multiple target legs for the same reference |
| `ConsolidatedPayment` | Target | Multiple source legs, one target leg for the same reference |
| `AmountMismatch` | Source | Closest unclassified target by amount has a different amount |
| `DateMismatch` | Source | Closest unclassified target has same amount but different value date |
| `MissingInTarget` | Source | No unclassified target available for this source row |
| `MissingInSource` | Target | No unclassified source claimed this target row |

---

## The 8-Phase Cascade

Classification runs as a fixed cascade of eight phases. Each phase only
touches rows that have not yet been classified by an earlier phase. The
cascade is not configurable — the phase order encodes business priority
(duplicates before splits before mismatches before missing) and must not
be reordered.

### Phase 1 — DuplicateInSource

For every reference group where `SourceCount > 1`: mark all source rows in
that group as `DuplicateInSource`. These rows share a normalized reference
with at least one sibling — they are duplicates by definition, regardless
of amount or date.

### Phase 2 — DuplicateInTarget

Same logic for target side: `TargetCount > 1` → mark all target rows as
`DuplicateInTarget`.

### Phase 3 — SplitPayment

For every reference group where `TotalSourceLegs == 1` and
`TotalTargetLegs > 1`: mark the single unclassified source row as
`SplitPayment`. A split payment is one source payment that was received
as multiple target payments.

`TotalSourceLegs = SourceCount + MatchedSourceCount` — this is why matched
pairs are passed in. A group where one source leg was already matched and
one remains unmatched has `TotalSourceLegs == 2`, which disqualifies it
from `SplitPayment` even if `SourceCount == 1`.

### Phase 4 — ConsolidatedPayment

Mirror of phase 3 on the target side: `TotalSourceLegs > 1` and
`TotalTargetLegs == 1` → mark the single unclassified target row as
`ConsolidatedPayment`.

### Phases 5 & 6 — AmountMismatch / DateMismatch

For every still-unclassified source row in a group that has at least one
still-unclassified target row: find the target with the smallest absolute
amount difference. If the amounts differ → `AmountMismatch`. If the amounts
match but the value dates differ → `DateMismatch`. If both match (upstream
matching strategy failed to match them) → `MissingInTarget`.

This is the only phase that reads `AmountMinor` and `DayNumber` columns.
All other phases operate solely on group metadata (`RefGroup` fields) and
`ProcessingState`.

### Phases 7 & 8 — MissingInTarget / MissingInSource

Terminal sweep: any source row still unclassified after phases 1–6 becomes
`MissingInTarget`. Any target row still unclassified becomes `MissingInSource`.
These phases never iterate over groups — they are flat O(n) sweeps over the
state arrays.

---

## RefGroup

`RefGroup` is the core grouping structure. It describes all transactions
sharing one normalized reference, from the perspective of a single
classification run:

```csharp
public struct RefGroup
{
    public int ReferenceId;          // interned reference ID (MatchKeyId)
    public int SourceStart;          // first position in sorted source batch
    public int SourceCount;          // number of unmatched source rows
    public int TargetStart;          // first position in sorted target batch
    public int TargetCount;          // number of unmatched target rows
    public int MatchedSourceCount;   // source legs already matched
    public int MatchedTargetCount;   // target legs already matched

    public int TotalSourceLegs => SourceCount + MatchedSourceCount;
    public int TotalTargetLegs => TargetCount + MatchedTargetCount;
}
```

A reference that appears only in matched pairs (all legs matched, no
unmatched rows) gets no `RefGroup` — there is nothing to classify.

---

## IExceptionClassifier

The public interface, defined in the Domain layer:

```csharp
public interface IExceptionClassifier
{
    IReadOnlyList<UnmatchedTransaction> Classify(
        IReadOnlyList<Transaction> unmatchedSource,
        IReadOnlyList<Transaction> unmatchedTarget,
        IReadOnlyList<MatchedPair> matchedPairs);
}
```

`ColumnarExceptionClassifier` is the current production implementation.
It implements `IExceptionClassifier` via delegation to `ClassifyColumnar()
.ToList()` for backward compatibility, while `ClassifyColumnar()` is the
primary path returning a `ClassificationResult` that supports lazy
enumeration.

---

## Correctness Gate

`ExceptionClassifierEquivalenceTests` (28 tests) proves that
`ColumnarExceptionClassifier` produces output identical to the original
`ExceptionClassifier` (dictionary-based) across:

- Every cascade scenario (one test per discrepancy type)
- Scale tests up to N=15,000 references
- Adversarial cases (10-leg splits/consolidations, zero/negative amounts)
- 7 random seeds via `ScenarioBuilder`

These tests are the non-negotiable correctness gate. No performance
optimization is valid until all 28 pass. See
`Tests/Classification/ExceptionClassifierEquivalenceTests.cs`.

---

## ReferenceInterner

All string-keyed lookups during classification are eliminated at index-build
time by `ReferenceInterner`, which maps `NormalizedReference.Value` strings
to dense sequential integers (0..k-1):

```csharp
public sealed class ReferenceInterner
{
    public int GetOrAdd(string reference);  // forward: string → int
    public int Count { get; }
    public bool TryGetId(string reference, out int id);
}
```

Every subsequent lookup in the cascade is an array index operation on a
dense integer — O(1), no hashing, no string comparison. This is the
foundation that makes both histogram sort and `RefGroup` direct-indexing
possible.
