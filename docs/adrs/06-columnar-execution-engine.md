# 06 — Columnar Execution Engine

## Overview

The columnar execution engine is the infrastructure layer beneath
`ColumnarExceptionClassifier`. It defines how transaction data is stored
in memory during a classification run, how that data is sorted and grouped,
and how per-row execution state flows through the processing pipeline.

The design is explicitly modelled on database execution engines:
a batch of rows is represented as a set of parallel arrays (one per field),
a generic byte column carries per-row operator state, and the engine
processes one operator at a time over the full batch rather than processing
one row at a time through all operators.

---

## ColumnarTransactionBatch

The core data structure. Replaces `IndexedTransaction[]` (array-of-structs)
with a struct-of-arrays:

```csharp
public sealed class ColumnarTransactionBatch
{
    public int Count { get; init; }
    public required int[]  OriginalIndex   { get; init; }
    public required int[]  MatchKeyId      { get; init; }
    public required long[] AmountMinor     { get; init; }
    public required int[]  DayNumber       { get; init; }
    // public required ushort[] CurrencyId { get; init; }  // deferred
    public required byte[] ProcessingState { get; init; }
}
```

Rows are stored in `MatchKeyId`-ascending order (sorted at build time by
`ColumnarIndexBuilder`). `OriginalIndex[i]` is the position of sorted row `i`
in the original unsorted input list — the bridge back to the full `Transaction`
object for result materialization.

### Why struct-of-arrays

Array-of-structs (`IndexedTransaction[]`) stores all fields for one row
contiguously. When a scan phase touches only one field, every cache line
loaded contains all other fields — wasted memory bandwidth.

Struct-of-arrays stores each field in its own flat array. A phase that only
needs `MatchKeyId` touches only `MatchKeyId[]`. A phase that only needs
`AmountMinor` and `DayNumber` touches only those two arrays. At N=1M with
64-byte cache lines, this difference is directly measurable — see item 6
benchmark results in `07-index-build-algorithms.md`.

### Column access by phase

| Phase | Reads | Writes |
|---|---|---|
| DuplicateScan (1+2) | — | `ProcessingState` |
| SplitConsolidatedScan (3+4) | `ProcessingState` (read) | `ProcessingState` |
| MismatchScan (5+6) | `AmountMinor`, `DayNumber`, `ProcessingState` | `ProcessingState` |
| MissingSweep (7+8) | `ProcessingState` | `ProcessingState` |
| ResultMaterialization | `OriginalIndex`, `ProcessingState` | — |

`MatchKeyId` is read only during index build (histogram pass and `RefGroup`
construction), never during scan phases. `OriginalIndex` is read only at
materialization. This column isolation is the structural reason the columnar
layout wins.

---

## ProcessingState — Generic Execution Buffer

`ProcessingState` is a `byte[]` whose meaning is defined by the currently
executing operator. It is not typed to a specific enum — the same array is
reused across operators without reallocation.

During classification, the buffer is interpreted via `ClassificationStateBytes`
constants:

```csharp
internal static class ClassificationStateBytes
{
    public const byte None                = 0;
    public const byte DuplicateInSource   = 1;
    public const byte DuplicateInTarget   = 2;
    public const byte SplitPayment        = 3;
    public const byte ConsolidatedPayment = 4;
    public const byte AmountMismatch      = 5;
    public const byte DateMismatch        = 6;
    public const byte MissingInTarget     = 7;
    public const byte MissingInSource     = 8;
}
```

A future matching operator would define its own constants:

```csharp
internal static class MatchingStateBytes
{
    public const byte None    = 0;
    public const byte Claimed = 1;
    public const byte Matched = 2;
}
```

The column does not change — only the interpretation changes between
operators. This is the same design a database engine uses for its per-row
status vector: a shared execution buffer with operator-local semantics.

**Critical constraint**: `ProcessingState` must never be exposed or persisted
outside the engine. When an operator finishes, it translates the byte values
into stable domain types (`DiscrepancyType`, `MatchedPair`, `QualityIssue`)
before returning results. The buffer is internal execution scratch, not a
public contract.

---

## ClassificationResult

The output of `ClassifyColumnar()` — owns the two `ColumnarTransactionBatch`
instances (including their `ProcessingState`) and the original input lists:

```csharp
public sealed class ClassificationResult
{
    public required ColumnarTransactionBatch Source         { get; init; }
    public required ColumnarTransactionBatch Target         { get; init; }
    public required IReadOnlyList<Transaction> SourceOriginals { get; init; }
    public required IReadOnlyList<Transaction> TargetOriginals { get; init; }

    public IEnumerable<UnmatchedTransaction> GetResults();
    public IReadOnlyList<UnmatchedTransaction> ToList();
}
```

`GetResults()` is lazy — one `UnmatchedTransaction` allocated per
`MoveNext()`. `ToList()` forces full materialization. The classifier runs
exactly once regardless of how many times the result is consumed.

Rehydration path: `SourceOriginals[Source.OriginalIndex[i]]` recovers the
full `Transaction` for sorted row `i`. This lookup is deferred to
materialization time — never during scanning phases.

---

## ColumnarExceptionClassifier

Implements `IExceptionClassifier` and is the sole production classifier:

```csharp
public sealed class ColumnarExceptionClassifier : IExceptionClassifier
{
    public IReadOnlyList<UnmatchedTransaction> Classify(...) =>
        ClassifyColumnar(...).ToList();

    public ClassificationResult ClassifyColumnar(...);
}
```

`Classify()` is a one-liner delegation — no logic lives there. All eight
cascade phases live in `ClassifyColumnar()`. This ensures the two paths
can never diverge in behaviour.

### Hot-path discipline

- `ProcessingState` is read and written as raw bytes using
  `ClassificationStateBytes` constants — no enum boxing on the hot path
- Local variable aliases (`var srcAmount = src.AmountMinor`) are assigned
  before the phase loops — avoids repeated property access through the batch
  reference on every iteration
- `ref readonly var group = ref index.Groups[g]` — avoids copying the
  `RefGroup` struct on each group iteration

---

## Planner sketch (future)

When the engine grows beyond classification into matching, deduplication,
and quality gates, a build strategy selector will choose between:

```
If MatchKeyId is dense integer (0..k-1, k reasonable):
    → HistogramColumnarBuilder    O(n + k), no comparison sort

Else if key is integer but sparse or composite:
    → RadixColumnarBuilder        O(n · d), d = digit passes

Else:
    → StructSortBuilder           O(n log n), general fallback
```

The selection criterion comes from a key descriptor provided by whoever
built the batch — the planner reads it, does not infer it. In CedarRecon's
current classification path, `MatchKeyId` is always dense (interned
references, 0..interner.Count-1), so the answer is always histogram. The
planner abstraction exists for future operators that may have different key
characteristics.

See ADR-XXX for the decision to adopt the columnar model and the benchmark
evidence supporting it.
