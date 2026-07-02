# ADR — Columnar Classification Engine

**Status**: Accepted  
**Date**: 2026  
**Deciders**: Youness (Senior .NET Engineer, CedarRecon)

---

## Context

CedarRecon's exception classification engine (`ExceptionClassifier`) was
originally implemented using four `Dictionary<string, ...>` structures
built via `GroupBy().ToDictionary()`. At N=1M transactions, the dictionary
engine ran at **1,368 ms whole-method** with super-linear scaling (1.99x
growth from N=10K to N=1M).

Profiling and phase-isolation benchmarks identified two root causes:

1. **String hashing on the hot path**: every `TotalLegs()` lookup hashed
   a `NormalizedReference.Value` string — O(string length) per lookup,
   millions of times across the 8-phase cascade.

2. **Cache-unfriendly access patterns**: `Dictionary<string, List<Transaction>>`
   meant following two levels of indirection (hash bucket → list → Transaction)
   to reach data that classification needed, with no spatial locality.

A full investigation across 7 worklist items was conducted before adopting
the columnar approach. All hypotheses were benchmarked; negative results
are documented.

---

## Decision

**Adopt a columnar (struct-of-arrays) execution engine with histogram sort
for the exception classification path.**

The production implementation consists of:

- `ColumnarTransactionBatch` — struct-of-arrays batch type with a generic
  `byte[] ProcessingState` execution buffer
- `ColumnarIndexBuilder` — O(n+k) histogram sort builder, eliminating both
  comparison sort and random-read scatter
- `ColumnarExceptionClassifier` — columnar-native 8-phase cascade classifier
- `ClassificationResult` — lazy streaming result type, materializes
  `UnmatchedTransaction` objects only on enumeration

`IExceptionClassifier` is preserved unchanged. `ColumnarExceptionClassifier`
implements it via `ClassifyColumnar().ToList()` delegation, ensuring backward
compatibility with all existing callers and the 28-test equivalence suite.

---

## Consequences

### Performance (whole-method, N=1M)

| Engine | ms | vs baseline |
|---|---|---|
| Dictionary (original) | 1,368 ms | 1.00x |
| Columnar + histogram | **646 ms** | **0.47x** |

**The columnar engine is 2.1x faster than the dictionary engine at N=1M.**

### Scan phases (columnar vs struct-sort array-of-structs, N=1M)

| Phase | Before | After | Δ |
|---|---|---|---|
| MismatchScan | 12.95 µs/1K | 4.54 | **−65%** |
| MissingSweep | 2.96 µs/1K | 1.45 | **−51%** |
| DuplicateScan | 5.55 µs/1K | 5.12 | −8% |

### IndexBuild (columnar+histogram vs alternatives, N=1M)

| Approach | µs/1K | vs struct-sort |
|---|---|---|
| Struct-sort (array-of-structs) | 395 | baseline |
| Scatter sort (item 6) | 464 | +18% ❌ |
| Histogram sort (item 7) | **296** | **−25%** ✅ |

---

## Rejected Alternatives

The following approaches were explicitly evaluated and rejected.
Full benchmark evidence is in `07-index-build-algorithms.md`.

### FrozenDictionary for Split/Consolidated phases

**Hypothesis**: `FrozenDictionary` has better lookup performance than
`Dictionary` and would improve the steepest-growing phase.

**Result**: Won at N=10K (−22%) but lost at N=100K (−19%) and N=1M (−41%).
Rejected. The problem was scan pattern, not lookup speed.

### ReferenceInterner optional reverse list (item 3)

**Hypothesis**: `GetValue` (reverse int→string lookup) is diagnostics-only;
making the reverse `List<string>` optional by default would reduce
`IndexBuild` allocation with no time cost.

**Result**: Allocation down 10–16%, but time consistently +5–34% worse
across all N (confirmed with 15 iterations). Reverted. Also revealed that
3-iteration benchmark runs are insufficient for this effect size range.

### Index-sort: sort `int[] rowOrder` instead of struct array (item 4)

**Hypothesis**: Moving 4-byte ints during sort is cheaper than moving
~20-byte structs; the sort-cost saving would outweigh the one-extra-dereference
cost on every downstream read.

**Result**: Scan phases regressed +32–61% at N=1M. `SplitConsolidatedScan`
(+61%) alone would have reversed the whole-method gain. Rejected.
Indirection costs more than it saves at production scale — consistent with
the FrozenDictionary result.

### Scatter sort for columnar layout (item 6, v1)

**Hypothesis**: Sort `sortOrder[]` by `MatchKeyId`, then scatter each
column: `final[i] = temp[sortOrder[i]]`. Smaller sort element (4-byte int
vs 20-byte struct) would win.

**Result**: `IndexBuild` regressed +18% at N=1M due to random reads from
`temp[sortOrder[i]]` when temp arrays exceed L3 cache. Replaced by
histogram sort (item 7) which eliminates both the comparison sort and the
random-read scatter entirely.

---

## What Was Kept From the Investigation

### ISortedRowView abstraction (items 1–4)

`ISortedRowView` was introduced to allow struct-sort and index-sort to be
benchmarked without duplicating classifier logic. Once index-sort was
rejected and the columnar path made row-at-a-time access obsolete,
`ISortedRowView` and all row-view types were retired. A `ColumnarRowView
: ISortedRowView` was explicitly considered and rejected — columnar storage
with a row-at-a-time API defeats the purpose of the layout change
("architecture theater").

### MoneyMinorUnitsConverter (item 1)

`decimal Amount → long AmountMinor` (scale 10^4) delivered a −50–65%
improvement to `MismatchScan` at N=1M and is kept permanently. Scale
factor is 10^4 (not 10^2) to cover 3-decimal-place currencies (KWD, BHD,
OMR) without silent precision loss. Values with >4 decimal places throw
rather than silently rounding.

### ProcessingState as generic execution buffer

`ClassificationState[]` was initially typed; the final implementation uses
`byte[] ProcessingState` with operator-local byte constants. This allows
future operators (matching, quality gates) to reuse the same column without
reallocation. The byte values must not be exposed outside the engine —
translate to stable domain types (`DiscrepancyType`) before returning results.

---

## Open Items

### Item 8 (potential) — Eliminate temp-array allocation in ColumnarIndexBuilder

The four temporary columns in `BuildBatch` (~20 bytes/row) are the
remaining source of super-linear `IndexBuild` growth at N=1M. A two-pass
encode (first pass counts keys, second pass places directly into
pre-sized final arrays) would eliminate them. Not yet scoped — requires
benchmark evidence that temp-array allocation is the actual bottleneck
rather than L3 cache capacity.

### Build strategy selector (planner)

When the engine grows beyond classification, a planner will select the
build strategy based on key characteristics:

```
Dense integer key (0..k-1):  HistogramColumnarBuilder   O(n+k)
Sparse/composite integer key: RadixColumnarBuilder       O(n·d)
Arbitrary comparison key:     StructSortBuilder          O(n log n)
```

`MatchKeyId` in the current classification path is always dense (interned,
0..interner.Count-1), so the answer is always histogram. The planner
abstraction is deferred until a second operator with different key
characteristics is added.

### CurrencyId column

`ColumnarTransactionBatch` has a `// CurrencyId deferred` placeholder.
Adding it requires currency-aware minor-unit scaling (MAD/EUR = 2dp,
KWD/BHD/OMR = 3dp) and a currency interner. Scoped as a separate item —
mixing it with the AoS→SoA transposition would have conflated two
independent concerns.

---

## References

- `docs/05-exception-classification-engine.md` — domain model, cascade semantics
- `docs/06-columnar-execution-engine.md` — execution model, ProcessingState, planner sketch
- `docs/07-index-build-algorithms.md` — full investigation history with all benchmark tables
- `docs/08-histogram-columnar-builder.md` — histogram sort algorithm deep-dive
- `docs/exception-classifier-scaling.md` — dictionary-engine investigation (prior work)
- `Tests/Classification/ExceptionClassifierEquivalenceTests.cs` — correctness gate
- `Tests/Performance/ColumnarClassifierPhaseBenchmark.cs` — phase benchmark
- `Tests/Performance/ExceptionClassifierBenchmark.cs` — whole-method benchmark
