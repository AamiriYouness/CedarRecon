# RFC — Preparation Stage Extraction: Splitting Domain Encoding from Physical Index Construction

Status: **Proposed**
Review outcome: **Architecturally accepted, pending implementation-time
resolution of §3 (MatchedPair timing) and §8 (buffer ownership/lifetime).**
Not scheduled, not started. Out of scope for the v0.9.0 monorepo
restructure. Filed as a companion to ADR-09 (Indexing extraction), which
documents where the *unsplit* code lands during that restructure and why.

Accepted as the target architecture for the post-v0.9.0 Preparation/Indexing
split. Physical planner contracts are intentionally introduced
incrementally (§7), with comparative cost modeling deferred until at least
two benchmarked strategies exist.

Revision note: this is the third draft. Draft 1 had a dependency-direction
bug (§1) and no planner concept. Draft 2 fixed the dependency direction and
added the physical planner but left a stale open-questions reference, an
unqualified statistics-collection claim, no shared-interning invariant, and
an implicitly single-shaped `PhysicalIndex`. All are corrected below.

## Problem

`ColumnarIndexBuilder` (`Application/Classification/Indexed/`) currently does
three jobs in one class:

1. **Domain decoding** — `Transaction.NormalizedReference.Value`,
   `Transaction.Amount.Amount`, `Transaction.ValueDate.UtcDateTime` read
   directly in the per-row encode loop (`BuildBatch`)
2. **Index construction** — histogram count → prefix-sum → cursor-based
   placement → `RefGroup[]` build (the O(n+k) algorithm documented in
   ADR-07/08)
3. **Result composition for a specific consumer** — the returned
   `ColumnarIndex` carries `IReadOnlyList<Transaction> SourceOriginals` /
   `TargetOriginals` directly, plus `MatchedPair`-derived leg counts

Per the v0.9.0 target architecture (Core / Indexing / Execution /
Classification), **Indexing must contain zero domain types** — enforced by
NetArchTest. `ColumnarIndexBuilder` cannot enter that boundary unchanged.

Call-site evidence (checked directly against the repo):

```
ColumnarIndexBuilder.Build(...)  — single caller: ColumnarExceptionClassifier.cs:52
ReferenceInterner                — instantiated independently at:
                                    ColumnarIndexBuilder.cs:43
                                    ReferenceIndexBuilder.cs:134
```

Both current callers of both types live in `Classification/Indexed/` today —
an accident of build order, not a statement of ownership.

## §1 — Dependency direction

The primitive contract types (`PrimitiveTransactionBatch`, etc.) are owned
by **Indexing**, not Preparation. Preparation constructs values of a type
Indexing defines; it does not lend Indexing a type of its own.

```
Preparation → Core (domain)
Preparation → Indexing            (constructs Indexing's primitive contracts)
Execution   → Indexing
Classification → Indexing

Indexing -X-> Preparation
Indexing -X-> Core
Indexing -X-> Execution
Indexing -X-> Classification
```

Execution and Classification are both *consumers* of Indexing, drawn as
siblings, not a chain.

## Target pipeline shape

```
Domain transactions (source + target)
      ↓
Preparation
  - domain → primitive encoding, shared interner (see §2a)
  - basic input statistics (see §2b)
      ↓
Indexing profiling
  - key-domain statistics derived from the encoded/interned representation
      ↓
PhysicalIndexPlanner              (see §7)
  - IndexRequirements from the matching side (see §6)
  - key density/cardinality, sortedness, memory budget, SIMD, spill capability
      ↓
Selected physical index strategy  (histogram today; radix/hash/merge later)
      ↓
PhysicalIndex (polymorphic — see §5)
      ↓
      ├──→ Matching / Execution
      └──→ Classification
```

## Proposed decomposition

```
Preparation/                         (logical stage/folder — see §7b;
                                     project status intentionally deferred)
├── TransactionBatchEncoder.cs       Transaction -> Indexing.PrimitiveTransactionBatch
├── PreparationStatistics.cs         input-level stats only — see §2b
├── MatchStateProjector.cs           only if MatchedPair is a Preparation input — see §3
└── PreparationResult.cs

Indexing/
├── Contracts/
│   ├── PrimitiveTransactionBatch.cs   ← owned here, not Preparation (§1)
│   ├── IndexStatistics.cs             key-domain stats — see §2b
│   ├── IndexBuildRequest.cs
│   ├── IndexBuildPlan.cs              includes selection rationale — see §4
│   ├── IPhysicalIndex.cs              polymorphic result contract — see §5
│   └── IPhysicalIndexStrategy.cs
├── Profiling/
│   └── IndexStatisticsCollector.cs    completes stats from encoded batch + interner
├── Planning/
│   ├── PhysicalIndexPlanner.cs
│   ├── IndexCostModel.cs
│   └── RuntimeEnvironmentProbe.cs
├── Strategies/
│   ├── HistogramIndexStrategy.cs      the only implementation for the initial extraction
│   ├── RadixIndexStrategy.cs          future
│   └── ComparisonSortIndexStrategy.cs future
├── ReferenceInterner.cs               ← moved here, not Preparation — see §2a
├── ColumnarTransactionBatch.cs
└── RefGroup.cs

Execution/
├── MatchingPlanner.cs                 supplies IndexRequirements — see §6
├── HashMatchingEngine.cs
└── ...

Classification/
├── ColumnarExceptionClassifier.cs
└── ClassificationResult.cs
```

## §2a — `ReferenceInterner`: resolved to Indexing, shared key space required

An interner mapping `ReadOnlySpan<char>`/`string` → dense `int` has no
dependency on `Transaction`/`NormalizedReference`/`MatchedPair` — the
domain-awareness lives in the caller extracting the string, not the interner
itself. ID density (`0..K-1`) is also an indexing invariant, not an encoding
one. **`ReferenceInterner` moves to `Indexing`.**

**Correctness invariant, not optional:** source and target must intern into
the *same* `ReferenceInterner` instance. If encoded independently
(`"ABC"→5` in source, `"ABC"→19` in target), dense-ID equality across the
two sides is meaningless and the whole histogram/matching pipeline breaks
silently — no exception, just wrong groupings. The API must make sharing the
default, not an easy-to-miss requirement:

```csharp
var interner = new ReferenceInterner();
var sourceBatch = encoder.Encode(sourceTransactions, interner);
var targetBatch = encoder.Encode(targetTransactions, interner);
```

or, preferably, a joint operation that removes the possibility of misuse
entirely:

```csharp
public PreparedReconciliationInput Encode(
    IReadOnlyList<Transaction> source,
    IReadOnlyList<Transaction> target);
```

This invariant is listed again in §9 — it is the single easiest way to
silently corrupt a reconciliation run, so it should be enforced by API shape
(one entry point, interner not separately constructible by the caller) and
not merely documented.

## §2b — Statistics: split between Preparation and Indexing

Not all planner-relevant statistics are knowable during domain encoding.
Splitting by what each stage can actually compute cheaply and correctly:

**Preparation can compute directly** (single pass over domain objects, no
interner/key-space knowledge needed): row count, amount min/max, date
min/max, null/zero counts.

**Requires the interner / encoded representation**, so belongs to Indexing
profiling instead: distinct key count, minimum/maximum encoded key, density
ratio, reference-group histogram, maximum group skew, and **key
sortedness** — specifically, whether adjacent rows are already
non-decreasing by *encoded match key*. This cannot be answered at
Preparation time: "pre-ordered" is meaningless without naming the ordering
key, and encoded match-key IDs don't exist until interning has happened.
Transactions may well arrive ordered by date while being unordered by
reference — Preparation should not guess which ordering a later physical
strategy will care about.

```
Preparation/
├── TransactionBatchEncoder.cs
└── PreparationStatistics.cs      row count, amount/date range, null counts

Indexing/Profiling/
├── IndexStatisticsCollector.cs   completes IndexStatistics from the encoded
│                                 batch + interner (distinct keys, density,
│                                 skew, etc.)
└── IndexStatistics.cs
```

```csharp
public sealed record PreparationResult(
    PrimitiveTransactionBatch Batch,
    PreparationStatistics Statistics);

var indexStatistics = indexStatisticsCollector.Complete(
    preparationResult.Batch, preparationResult.Statistics, interner);
```

Without this split, `EncodingStatisticsCollector` (as drafted previously)
would quietly become responsible for indexing analysis it can't actually
perform correctly at the Preparation stage.

## §3 — `MatchedPair` projection: timing is unresolved, and it matters

Two different domain-aware activities may not belong to the same stage:

- **Transaction domain projection**: `Transaction → referenceId, amountMinor, dayNumber`
- **Existing match-state projection**: `MatchedPair → source/target matched-leg counts`

- **Case A — `MatchedPair` values are pre-existing/external inputs**
  (previously reconciled pairs fed back in): `MatchStateProjector` is a
  legitimate Preparation component.
- **Case B — `MatchedPair` values are produced by the current matching
  run**: they cannot be part of a *pre-matching* preparation stage. The
  pipeline becomes `Preparation → Indexing → Matching → match-state
  projection → Classification` — a post-match / Classification-input
  adapter, not Preparation.

**Not resolved here** — requires checking how `matchedPairs` actually flows
into `ColumnarIndexBuilder.Build` today before implementation starts. This
is a load-bearing open question and must be resolved before implementation,
alongside buffer ownership and lifetime in §8.

## §4 — Planner output must carry selection rationale

`IndexBuildPlan` should return more than the chosen enum — for benchmarking,
production diagnostics, and customer-facing support, it needs to explain
*why*:

```csharp
public sealed record IndexBuildPlan(
    PhysicalIndexStrategyKind Strategy,
    ExecutionKernelKind Kernel,          // see §7c — separate from Strategy
    IndexCost EstimatedCost,
    IReadOnlyList<StrategyEvaluation> Candidates,
    string SelectionReason);
```

Example rationale content: *"Selected Histogram: key IDs dense from
0..84,201, K/N = 0.084, estimated peak memory 42MB vs 512MB budget,
estimated cost lower than radix."* Rejected candidates should carry reasons
too (*"Radix rejected: estimated temp buffer exceeds 128MB cap"*, *"Hash
rejected: downstream sort-merge execution requires ordered groups"*) — this
belongs in CedarRecon's structured logs and benchmark reports, not just an
internal decision no one can see.

## §5 — `PhysicalIndex` is polymorphic, not one fixed shape

Different strategies don't naturally produce the same physical
representation: histogram/radix/sort produce sorted rows with contiguous
`RefGroup` offsets; hash produces buckets/chains, not necessarily globally
sorted; external-merge may be spilled/disk-backed segments; partitioned
strategies produce multiple local indexes. Treating `PhysicalIndex` as
always one concrete columnar struct would force unrelated fields into one
type.

```csharp
public interface IPhysicalIndex
{
    PhysicalIndexKind Kind { get; }
    long RowCount { get; }
}

public sealed class SortedColumnarIndex : IPhysicalIndex;   // histogram/radix/sort
public sealed class HashPhysicalIndex : IPhysicalIndex;     // future
public sealed class PartitionedPhysicalIndex : IPhysicalIndex; // future
public sealed class ExternalPhysicalIndex : IPhysicalIndex; // future
```

**Scope constraint for the initial extraction**: the `IPhysicalIndex`
contract need only cover `SortedColumnarIndex` (what histogram produces
today). Additional implementations are introduced only alongside the
strategies that need them (§7d) — declaring the interface now, not
speculatively building unused variants.

## §6 — `IndexRequirements` provenance and planner coordination

`PhysicalIndexPlanner` (Indexing — chooses physical layout: histogram /
radix / sort / hash / partitioned / external-merge) and `MatchingPlanner`
(Execution — chooses reconciliation execution strategy: hash match /
sort-merge / aggregate / fuzzy candidate generation) are correctly separate,
but need a defined direction of influence. **Matching constraints flow into
physical planning, not the reverse** — the matching strategy usually has
real requirements (e.g. sort-merge needs ordered groups) that a physical
strategy must satisfy, rather than matching passively adapting to whatever
layout indexing happened to produce.

```
ReconQL compilation
      ↓
Logical matching requirements
      ↓
MatchingPlanner / execution planning
      ↓
IndexRequirements   (e.g. MustBeOrdered=true, RequiredKeys=[...], SupportsStreaming=true)
      ↓
PhysicalIndexPlanner  — rejects incompatible strategies (e.g. unordered hash-only)
      ↓
PhysicalIndex + executable matching plan
```

A higher-level `ReconciliationPlanner` coordinating both is plausible future
work but is explicitly **not designed in this RFC** — state only that
`IndexRequirements` are supplied by the consumer or a higher-level
reconciliation planner and constrain physical-strategy eligibility.

## §7 — Physical planner implementation and extraction sequencing (avoid building a fake planner up front)

The target structure includes `IndexCostModel`, `RuntimeEnvironmentProbe`,
`PhysicalIndexPlanner`, `IPhysicalIndexStrategy` — correct as a target, but
building a "cost-based" planner with one strategy and arbitrary scoring on
day one produces an abstraction with nothing to validate it. A planner only
becomes meaningful once at least two benchmarked strategies actually
compete.

Proposed phasing (this RFC does not commit to a timeline, only an order):

- **Phase A** — extract primitive contracts, `TransactionBatchEncoder`,
  `HistogramIndexStrategy`. Preserve behavior exactly. No planner — direct
  call.
- **Phase B** — add `RuntimeCapabilities`/`IndexStatistics`, an eligibility
  check, and a single-strategy "coordinator" implementing the eventual
  planner contract but always selecting histogram deterministically.
- **Phase C** — add a second real strategy (radix or comparison-sort),
  introduce genuine comparative cost modeling, benchmark planner decisions
  against each other.
- **Phase D** — hash, partitioned, external-merge strategies.

Cost comparison becomes authoritative only once multiple benchmarked
strategies exist; before that, the "planner" is a coordinator wearing the
final interface, not a validated cost model.

### §7a — Strategy selection vs. kernel selection (SIMD)

CPU architecture and SIMD width should influence *kernel* selection within a
chosen strategy, not necessarily the strategy choice itself. E.g. SIMD may
select `HistogramIndexStrategy`'s scalar vs. `Vector128` vs. `Vector256`
implementation, or radix's digit width — not decide "ARM64 → radix, x64 →
histogram" as a blanket rule. Keep these as two separate concerns:

```csharp
public sealed record IndexBuildPlan(
    PhysicalIndexStrategyKind Strategy,   // histogram / radix / hash / merge
    ExecutionKernelKind Kernel,           // scalar / Vector128 / AVX2 / AVX-512 / NEON
    IndexCost EstimatedCost,
    IReadOnlyList<StrategyEvaluation> Candidates,
    string SelectionReason);
```

## §7b — Preparation: logical stage, not a committed project

This RFC treats Preparation as a pipeline stage, not as a project
decision. A dedicated `CedarRecon.Preparation` project is justified only
if the stage grows enough to require a compiler-enforced boundary,
independent tests or benchmarks, distinct ownership or buffer
lifecycle, multiple real consumers, or future packaging. Its appearance
as a named box in the pipeline does not by itself justify another
assembly.

For the initial extraction in §7 Phase A, `TransactionBatchEncoder` and
its immediate helpers may live as a folder inside the project that
currently orchestrates the preparation flow. That temporary placement
must not be interpreted as permanent architectural ownership. Promotion
to a dedicated project remains a later decision based on demonstrated
boundary pressure.

## §8 — Buffer ownership and lifetime: open question, not blocking

`ReadOnlyMemory<T>` expresses read-only *access*, not ownership or
immutability. Before implementing `PrimitiveTransactionBatch` as a bare
`record struct`, answer: who allocates the backing arrays; are they pooled
(`ArrayPool<T>`); who returns them; can the resulting index outlive the
encoded batch; does the index retain or copy the input memory; can another
component mutate the source arrays after handoff. If buffers end up pooled,
an `IDisposable` wrapper or separate borrowed/owned forms
(`PrimitiveTransactionBatchView` vs `OwnedPrimitiveTransactionBatch`) may be
needed. Not resolved here — an explicit question for implementation time,
tied to §3 as the two load-bearing open questions.

## §9 — Required invariants

- Source and target use the **same** interning key space (§2a) — this is
  the single most important invariant in this document; a violation
  produces silently wrong reconciliation results, not an exception.
- Reference IDs are stable for the lifetime of one reconciliation run.
- Dense IDs occupy `[0, DistinctKeyCount)`.
- Primitive columns within a batch have identical row counts across all
  arrays.
- Row maps preserve access to original domain transactions without
  embedding domain objects inside Indexing.
- Physical strategy selection cannot exceed the configured engine memory
  budget.
- Planner fallback must always produce a valid strategy or a structured
  planning failure — never silently degrade.
- **Strategy choice must not alter reconciliation semantics** — the
  physical planner may change layout and execution cost, but never matching
  correctness or business-rule outcomes. This is the property that makes
  swapping strategies safe at all; if it doesn't hold, the planner isn't an
  optimization, it's a correctness risk.

## §10 — Benchmark methodology

The existing 646ms N=1M whole-method baseline is necessary but not
sufficient. Measure at three levels: (1) domain encoding alone, (2)
primitive index construction alone (given an already-encoded batch), (3)
end-to-end, compared against the 646ms baseline. Track allocated bytes and
Gen0/1/2 counts, not just wall-clock, per existing BenchmarkDotNet
discipline (warmupCount 3 / iterationCount 15). Consider x64/ARM64
separately once kernel-selection logic (§7a) exists. Acceptance requires
both end-to-end parity and algorithm isolation (the primitive histogram core
alone is not slower than the equivalent section of today's unsplit code). A
regression is a first-class negative result per project convention
(ADR-07/08), not something accepted because the architecture is cleaner on
paper.

## Naming note (non-blocking)

`PrimitiveTransactionBatch` vs `EncodedTransactionBatch` — "Primitive"
signals the zero-domain-type contract explicitly; "Encoded" describes the
pipeline role instead. Either is defensible; left as an implementation-time
naming choice rather than settled here.

## What this RFC does NOT cover

- No timeline — v0.9.x+ follow-up, not gating v0.9.0.
- No benchmark numbers yet — this is a design proposal.
- No decision on PR sequencing beyond the phase order in §7.
- Does not design the higher-level `ReconciliationPlanner` coordinator
  mentioned in §6 — only notes that `IndexRequirements` must come from
  somewhere above `PhysicalIndexPlanner`.
- Does not finalize `IndexBuildRequest`/`RuntimeCapabilities`/`IndexCost`
  field lists — sketches are illustrative of shape, not committed contracts.

## Relationship to the v0.9.0 restructure

During the mechanical restructure (ADR-09), `ColumnarIndexBuilder`,
`ReferenceIndexBuilder`, and `ReferenceInterner` are placed in
`Classification/Indexed/` as a **temporary accommodation** matching their
current call graph — not a statement that Classification owns them
conceptually. This RFC is the record of where they're actually meant to end
up. Treat this RFC, not the Phase 3 placement, as the target architecture.
