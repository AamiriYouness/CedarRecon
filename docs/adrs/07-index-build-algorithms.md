# 07 — Index Build Algorithms: Investigation and Results

## Overview

This document records the complete investigation into how to sort and index
transaction rows for the classification engine. The investigation ran across
items 1–7 of the compaction/columnar worklist. Every hypothesis tested,
every benchmark result (including negative results), and every rejected
approach is documented here. The final adopted approach is histogram sort,
described in detail in `08-histogram-columnar-builder.md`.

All benchmarks use `ScenarioBuilder` fixed-distribution data:
60% noise/1:1 pairs, 15% duplicates, 10% splits, 10% consolidations,
5% amount mismatches, plus matched-pair sampling sharing refs with
split/consolidated groups. Pure-random data is explicitly avoided —
it exercises only the cheap `MissingInTarget`/`MissingInSource` fallback
paths and hides the interesting cascade costs. Job config:
`warmupCount: 3, iterationCount: 15, launchCount: 1`.

---

## Starting Point: Dictionary-Based Classifier

The original `ExceptionClassifier` used four `Dictionary<string, ...>`
structures built via `GroupBy().ToDictionary()`:

```
sourceByRef              Dictionary<string, List<Transaction>>
targetByRef              Dictionary<string, List<Transaction>>
matchedSourceLegCounts   Dictionary<string, int>
matchedTargetLegCounts   Dictionary<string, int>
```

Every classification lookup hashed a `NormalizedReference.Value` string
(O(string length)) and walked a bucket chain on collision. At N=1M with
`TotalLegs()` called per transaction in the Split/Consolidated phases,
this was millions of string hash computations on the hot path.

Whole-method baseline at N=1M: **1,368 ms** (after `DictionaryBuilder`
optimization; original was higher — see `docs/exception-classifier-scaling.md`).

---

## Item 1 — IndexedTransaction Compaction

### Hypothesis

`decimal Amount` (16 bytes, software-emulated math) and `DateOnly ValueDate`
(struct) inside `IndexedTransaction` were targets for compaction:
- `decimal → long AmountMinor` (scale 10^4): `Math.Abs` on `long` is a
  single CPU instruction vs software-emulated decimal arithmetic
- `DateOnly → int DayNumber` (`DateOnly.DayNumber` directly, no custom epoch)
- `Transaction Original` reference field removed from the hot struct,
  rehydrated lazily via `OriginalIndex` at materialization time

Scale factor: 10^4 (not 10^2) — covers 3-decimal-place currencies
(KWD, BHD, OMR) and sub-cent rounding residue without silent precision loss.
Values with more than 4 decimal places throw rather than silently rounding.

### Result ✅

`MismatchScan` phase benchmark (µs/1K rows, before → after):

| N | Before | After | Δ |
|---|---|---|---|
| 10K | 7.76 | 5.20 | −33% |
| 100K | 10.79 | 6.86 | −36% |
| 1M | 12.57 | 6.34 | **−50%** |

Growth ratio: 1.6x → 1.22x. The decimal→long swap delivered as predicted.
`ResultMaterialization` was unaffected (+0% to −4% within noise).

---

## Item 2 — BuildGroups Double-Allocation

### Hypothesis

`List<RefGroup>(internedCount)` + `[.. groups]` spread allocated two
`RefGroup[]` arrays: the `List<T>` backing array (capacity `internedCount`)
and a second array of the actual count from the spread. Replacing with
`new RefGroup[internedCount]` + count cursor + conditional `Array.Resize`
would eliminate the second allocation.

### Result ❌ NEGATIVE

`IndexBuild` `Allocated` column: **0.0% change** at every N. The reason:
`ScenarioBuilder`'s distribution includes matched-only references, so
`count < internedCount` on essentially every realistic run — the
`Array.Resize` branch fires just as often as the old spread did. The
second allocation was never actually eliminated on the realistic dataset.

Mean time showed a small, inconsistent improvement (−17.6% at N=100K,
only −2.8% at N=1M) likely attributable to removing `List<T>` wrapper
overhead rather than allocation pattern change.

**Change kept** (not worse, simpler code) but hypothesis disproven.
Allocation-volume was never the actual bottleneck.

---

## Item 3 — ReferenceInterner Optional Reverse List

### Hypothesis

`ReferenceInterner` unconditionally built a `List<string> _values`
for reverse lookup (`GetValue`). Since `GetValue` is diagnostics-only
and never called during `Classify()`, making it optional (constructor
flag, default off) should reduce `IndexBuild` allocation with no time cost.

### Result ❌ NEGATIVE — REVERTED

First benchmark run (3 iterations, noisy): allocation down −10–16%,
time apparently flat. Rerun with higher iteration count (15 iterations,
tight error bars): **time consistently +5–34% worse** across all N.
No confirmed mechanistic explanation — candidates include the null-conditional
`_values?.Add(reference)` branch per `GetOrAdd` call, or the switch from
`_values.Count` to `_ids.Count` as the `Count` source. Neither was isolated
before the revert decision.

**REVERTED.** Time regression (5–34%) outweighs allocation win (10–16%)
for a feature path (`GetValue`) that was never a bottleneck.

**Methodology note**: this item revealed that `warmupCount: 1,
iterationCount: 3` is insufficient for effect sizes in the 5–35% range.
The first noisy run showed an apparent win; the higher-iteration rerun
showed a real regression. All subsequent items used `warmupCount: 3,
iterationCount: 15` as the standing convention.

---

## Item 4 — Struct-Sort vs Index-Sort

### Hypothesis

`Array.Sort` on `IndexedTransaction[]` moves whole ~20-24 byte structs on
every swap. Sorting a parallel `int[] rowOrder` by `ReferenceId` and reading
through it would move only 4-byte ints, potentially winning on sort cost.

### Implementation note

`ISortedRowView` abstraction was introduced to support both strategies
without duplicating classifier logic, keeping `Classify()` a pure function
regardless of which sort strategy was used.

### Result ❌ NEGATIVE

`IndexBuild` phase (µs/1K, struct-sort vs index-sort):

| N | Struct | Index | Δ |
|---|---|---|---|
| 10K | 157.70 | 151.70 | −3.8% (noise) |
| 100K | 451.69 | 396.10 | **−12.3%** |
| 1M | 572.30 | 715.43 | **+25.0%** (NOISY, StdDev 20%) |

Scan phases (index-sort always pays one extra dereference `rowOrder[i]`):

| Phase | N=1M struct | N=1M index | Δ |
|---|---|---|---|
| SplitConsolidatedScan | 2.81 µs/1K | 4.53 | **+61%** |
| DuplicateScan | 5.55 | 7.34 | **+32%** |
| MismatchScan | 12.95 | 10.23 | −21% (noisy) |

Index-sort wins sort cost at N=100K but loses on every scan phase at scale.
The indirection cost (`rowOrder[i]` before every row read) exceeds the
struct-copy savings from sorting smaller elements. Consistent with the
FrozenDictionary result: indirection costs more than it saves at these scales.

**Struct-sort kept. `ISortedRowView` and index-sort variants deleted.**

The `ISortedRowView` abstraction itself was subsequently retired when the
columnar path (item 6) made row-at-a-time access semantics obsolete.
Keeping it as a `ColumnarRowView : ISortedRowView` would have been
architecture theater — columnar storage with a row-at-a-time API defeats
the entire purpose of the layout change.

---

## Item 6 — Array-of-Structs → Struct-of-Arrays (Columnar)

### Hypothesis

`IndexedTransaction[]` (array-of-structs) loads all fields into cache on
every row access regardless of which phase is running. Struct-of-arrays
(`ColumnarTransactionBatch`) would let each phase touch only the column
arrays it actually reads.

Initial implementation used **sort-then-scatter**:
1. Encode into temporary columns (input order)
2. Build `sortOrder[]` and sort by `MatchKeyId` via `Array.Sort`
3. Scatter: `final[i] = temp[sortOrder[i]]` — random reads at N=1M

### Phase benchmark results — scan phases ✅

| Phase | Struct-sort µs/1K | Columnar µs/1K | Δ at N=1M |
|---|---|---|---|
| MismatchScan | 12.95 | 4.54 | **−65%** |
| MissingSweep | 2.96 | 1.45 | **−51%** |
| SplitConsolidatedScan | 2.81 | 3.45 | +23% (regression) |
| DuplicateScan | 5.55 | 5.12 | −8% |

### Phase benchmark results — IndexBuild ⚠️

| N | Struct-sort µs/1K | Scatter µs/1K | Δ |
|---|---|---|---|
| 10K | 241 | 212 | −12% |
| 100K | 448 | 322 | −28% |
| 1M | 395 | 464 | **+18%** |

The scatter pass's `temp[sortOrder[i]]` random reads hurt at N=1M where
temp arrays exceed L2/L3 cache. Scatter sort has the same fundamental
problem as index-sort (item 4): index-based indirection into large arrays
at scale causes cache misses that outweigh the algorithmic savings.

### Whole-method result ✅

Despite the `IndexBuild` regression, whole-method columnar+scatter was
**0.50x vs dictionary** at N=1M (2x faster) — scan-phase wins more than
compensated. Item 6 was closed on this basis, with the `IndexBuild`
regression documented as a known issue and scoped as item 7.

---

## Item 7 — Histogram Sort (Counting Sort on Dense MatchKeyId)

### Hypothesis

`MatchKeyId` is a dense integer (0..interner.Count-1) — the ideal key for
counting/histogram sort. Replace scatter sort with:
1. Count rows per key (histogram) — O(n)
2. Prefix-sum → per-key start offsets — O(k)
3. Place rows using `cursor[key]++` — O(n), sequential writes per bucket
4. Build `RefGroup[]` directly from histogram — O(k), no merge-join needed

Total: O(n + k) vs O(n log n). Eliminates both the comparison sort AND
the random-read scatter.

### Result ✅

`IndexBuild` phase comparison (µs/1K):

| N | Struct-sort | Scatter (v1) | Histogram (v2) | vs struct | vs scatter |
|---|---|---|---|---|---|
| 10K | 241 | 212 | **139** | −42% | −34% |
| 100K | 448 | 322 | **206** | −54% | −36% |
| 1M | 395 | 464 | **296** | **−25%** | **−36%** |

The 1M regression from item 6 (+18% vs struct-sort) is fully reversed —
histogram is now **−25% faster than struct-sort** at N=1M. No reversals,
no tradeoffs, clean win at every N.

### Whole-method final result

| Engine | ms/1K at N=1M | vs Dictionary |
|---|---|---|
| Dictionary (baseline) | 1.368 | 1.00x |
| Columnar + scatter (item 6) | 0.845 | 0.62x |
| Columnar + histogram (item 7) | **0.646** | **0.47x** |

**The columnar engine with histogram sort is 2.1x faster than the
dictionary-based classifier at N=1M.**

---

## Summary of Rejected Approaches

| Item | Hypothesis | Result | Reason |
|---|---|---|---|
| FrozenDictionary | Faster lookup for Split/Consolidated phases | ❌ Slower at N=100K+1M | Lookup speed irrelevant; scan pattern was the bottleneck |
| Item 2 | Eliminate double-allocation in BuildGroups | ❌ No allocation change | Realistic data always has matched-only refs → resize fires anyway |
| Item 3 | Make ReferenceInterner reverse list optional | ❌ Time regression +5–34% | Cost of null-check or Count source change exceeded allocation win |
| Item 4 | Index-sort (sort int[] instead of structs) | ❌ Scan phases +32–61% | Indirection cost on every read exceeds sort-swap savings |
| Item 6 scatter | Scatter sort for columnar layout | ⚠️ IndexBuild +18% at 1M | Random reads from temp[sortOrder[i]] at scale → cache misses |

All negative results are preserved as documented findings, not hidden.
