# 08 — Histogram Columnar Builder

## Purpose

`ColumnarIndexBuilder` builds the `ColumnarTransactionBatch` arrays and
`RefGroup[]` index used by `ColumnarExceptionClassifier`. This document
describes the algorithm in detail, explains why it was chosen over the
alternatives (see `07-index-build-algorithms.md`), and documents the key
implementation decisions.

---

## Algorithm Overview

Four passes, each O(n) or O(k) where k = distinct `MatchKeyId` values:

```
Pass 1: Encode        O(n)    input order → temporary columns
Pass 2: Histogram     O(n)    count rows per MatchKeyId
Pass 3: Prefix sum    O(k)    counts → start offsets
Pass 4: Place         O(n)    input order → final sorted columns
                              via cursor[key]++
RefGroup build        O(k)    directly from histogram (no merge-join)
```

Total: O(n + k). For typical reconciliation data (high reference reuse,
k ≪ n), this is effectively O(n).

---

## Pass 1 — Encode into Temporary Columns

Iterates the input `IReadOnlyList<Transaction>` once in input order,
writing into four temporary arrays:

```csharp
tmpOriginalIndex[i] = i;
tmpMatchKeyId[i]    = interner.GetOrAdd(tx.NormalizedReference.Value);
tmpAmountMinor[i]   = MoneyMinorUnitsConverter.ToMinorUnits(tx.Amount.Amount);
tmpDayNumber[i]     = DateOnly.FromDateTime(tx.ValueDate.UtcDateTime.Date).DayNumber;
```

This is the only pass that touches the domain `Transaction` objects. All
subsequent passes work on plain integer and long arrays — no reference
types, no string comparisons.

`interner.GetOrAdd` interns each reference string on first encounter,
assigning a dense sequential ID (0, 1, 2, ...). After this pass,
`interner.Count` equals the number of distinct references seen.

---

## Pass 2 — Histogram (Count Rows per Key)

```csharp
var counts = new int[keyCount];
for (var i = 0; i < n; i++)
    counts[tmpMatchKeyId[i]]++;
```

`counts[k]` = number of rows in this batch with `MatchKeyId == k`. This
is a sequential read over `tmpMatchKeyId` (cache-friendly) and a scattered
write into `counts` (k is small relative to cache, so `counts` stays hot).

---

## Pass 3 — Prefix Sum → Start Offsets

```csharp
var starts = new int[keyCount];
var running = 0;
for (var k = 0; k < keyCount; k++)
{
    starts[k] = running;
    running   += counts[k];
}
```

After this pass, `starts[k]` is the first position in the final sorted
arrays where key `k`'s rows will be placed. `starts[k] + counts[k]` equals
`starts[k+1]` for all k — the layout is fully determined before any row
is moved.

---

## Pass 4 — Place Rows into Final Sorted Columns

```csharp
var cursor = new int[keyCount];
Array.Copy(starts, cursor, keyCount);

for (var i = 0; i < n; i++)
{
    var key = tmpMatchKeyId[i];
    var pos = cursor[key]++;

    originalIndex[pos] = tmpOriginalIndex[i];
    matchKeyId[pos]    = key;
    amountMinor[pos]   = tmpAmountMinor[i];
    dayNumber[pos]     = tmpDayNumber[i];
}
```

`cursor[key]` starts at `starts[key]` and advances as rows for that key
are placed. This produces two key properties:

**Reads are sequential**: the outer loop iterates `i = 0..n-1` in input
order, so reads from `tmpMatchKeyId`, `tmpAmountMinor`, etc. are strictly
sequential — exactly the access pattern hardware prefetchers optimize for.

**Writes are contiguous within each key bucket**: `cursor[key]++` means
rows for the same key are written to consecutive positions in the output
arrays. Writes from different keys interleave, but any given write lands
in a position that was prefetched as part of the same key's contiguous run.

This contrasts with the scatter approach replaced in item 7:

```csharp
// Old scatter (item 6) — RANDOM READS at N=1M:
final[i] = temp[sortOrder[i]];  // sortOrder[i] is arbitrary → cache miss
```

The histogram approach has no random reads. Both reads (sequential over
input) and writes (contiguous per key bucket) are cache-friendly at any N.

---

## RefGroup Construction — No Merge-Join Needed

After the histogram passes, `starts[k]` and `counts[k]` for the source
batch are exactly `SourceStart` and `SourceCount` for reference `k`.
Equivalently for the target batch.

The previous implementation needed a merge-join walk over both sorted
arrays to discover group boundaries:

```csharp
// Old merge-join (O(n) walk over both arrays):
while (si < sortedSource.Length || ti < sortedTarget.Length)
{
    // walk both arrays in lockstep, emit one RefGroup per distinct key
}
```

With histogram sort, boundaries are already known. `RefGroup[]` is built
in a single O(k) pass over the key space:

```csharp
for (var k = 0; k < internedCount; k++)
{
    var sourceCount = source.Counts[k];
    var targetCount = target.Counts[k];

    if (sourceCount == 0 && targetCount == 0) continue; // matched-only, skip

    groups[count++] = new RefGroup
    {
        ReferenceId   = k,
        SourceStart   = source.Starts[k],
        SourceCount   = sourceCount,
        TargetStart   = target.Starts[k],
        TargetCount   = targetCount,
        // ...
    };
}
```

This eliminates an entire O(n) pass (the merge-join) and replaces it with
an O(k) pass. For datasets with high reference reuse (k ≪ n), this is
a significant reduction. For datasets where every transaction has a unique
reference (k = n), it's equivalent.

---

## BatchWithHistogram — Intermediate Type

`BuildBatch` returns a `BatchWithHistogram` struct rather than just
`ColumnarTransactionBatch`, carrying the `starts[]` and `counts[]` arrays
needed for `RefGroup` construction:

```csharp
private readonly struct BatchWithHistogram(
    ColumnarTransactionBatch batch,
    int[] starts,
    int[] counts,
    int keyCount)
```

These arrays are not included in `ColumnarTransactionBatch` itself because
they are not needed after index build time — the `RefGroup[]` already
encodes their information in a form the classifier can use directly.
`BatchWithHistogram` is purely an internal build-time artifact.

---

## Allocation Profile

Per `BuildBatch` call (source and target each):

| Array | Size | Notes |
|---|---|---|
| `tmpOriginalIndex` | n × 4 bytes | temporary, discarded after Place pass |
| `tmpMatchKeyId` | n × 4 bytes | temporary |
| `tmpAmountMinor` | n × 8 bytes | temporary |
| `tmpDayNumber` | n × 4 bytes | temporary |
| `counts` | k × 4 bytes | kept in `BatchWithHistogram` for RefGroup build |
| `starts` | k × 4 bytes | kept in `BatchWithHistogram` |
| `cursor` | k × 4 bytes | temporary, discarded after Place pass |
| `originalIndex` | n × 4 bytes | final, owned by batch |
| `matchKeyId` | n × 4 bytes | final |
| `amountMinor` | n × 8 bytes | final |
| `dayNumber` | n × 4 bytes | final |
| `ProcessingState` | n × 1 byte | final, zero-initialized |

Peak allocation during build: approximately `(n × 42 + k × 12)` bytes per
batch. At N=1M with k ≈ 0.25N (typical ScenarioBuilder distribution),
this is roughly 43MB per batch, 86MB total for source+target — consistent
with the observed `Allocated: 431MB` at N=1M (which includes both batches
plus `RefGroup[]` and ancillary structures).

The temporary arrays (20 bytes/row × n) are the dominant allocation cost
and the primary target for any future build-cost reduction. One approach:
encode directly into final arrays in a pre-sorted pass (requires knowing
the key distribution upfront, e.g. from a previous run or a sampling pass).

---

## Benchmark Results

`ColumnarClassifierPhaseBenchmark.IndexBuild` (µs/1K rows):

| N | Histogram | Scatter (prev) | Struct-sort (prev) | vs struct-sort |
|---|---|---|---|---|
| 10K | **139** | 212 | 241 | −42% |
| 100K | **206** | 322 | 448 | −54% |
| 1M | **296** | 464 | 395 | **−25%** |

Growth ratio 10K→1M: 2.13x. Still super-linear due to temp-array
allocation cost exceeding L3 cache at N=1M. The known remaining target
for a hypothetical item 8 would be eliminating the temp arrays via a
two-pass encode (first pass counts, second pass places directly).

---

## Known Limitations

**Temp-array allocation**: the four temporary arrays (20 bytes/row) are
the residual source of super-linear growth at N=1M. They are allocated,
populated, and immediately abandoned after the Place pass — a significant
GC pressure source at scale.

**Key range assumption**: the histogram approach assumes `MatchKeyId` is
dense (0..k-1). This is guaranteed by `ReferenceInterner` for the current
classification path. If a future operator produces a non-dense or composite
key, a different build strategy (radix sort, comparison sort) must be
selected — see the planner sketch in `06-columnar-execution-engine.md`.
