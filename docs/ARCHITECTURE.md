# CedarRecon — Architecture

## Platform Overview

CedarRecon is a high-performance, metadata-driven financial reconciliation
platform. Its architecture is inspired by database execution engines: data
is processed in columnar batches by sequential operators, rules are expressed
as metadata rather than code, and every design decision is validated by
benchmarks before adoption.

---

## Full Pipeline

```
┌─────────────────────────────────────────────────────────────┐
│                  Financial Source Systems                    │
│  Core banking · ERP · Payment rails · Insurance · Treasury  │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                    Ingestion Framework                       │
│  CSV · Excel · MT940 · CAMT.053 · SQL · REST · Parquet      │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                      Quality Gates                          │
│  Field validation · Record validation · Duplicate detection  │
│  Dead letter queue · Fail-fast / warning mode               │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│               Reference Data Resolution                     │
│  ISO 4217 currency · ISO 3166 country · FX rates            │
│  Business calendar · Counterparty · Instrument              │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                  ReconRecord Normalization                   │
│  Canonical financial record · Money (amount + currency)     │
│  NormalizedReference · ValueDate · Counterparty             │
└──────────────────┬────────────────────┬─────────────────────┘
                   │                    │
              Source Dataset       Target Dataset
                   │                    │
                   ▼                    ▼
┌─────────────────────────────────────────────────────────────┐
│             In-Memory Columnar Execution Engine             │
│                                                             │
│  ColumnarTransactionBatch (struct-of-arrays)                │
│    int[]  MatchKeyId    — interned reference ID             │
│    long[] AmountMinor   — scaled integer amount             │
│    int[]  DayNumber     — value date as integer             │
│    int[]  OriginalIndex — bridge to source record           │
│    byte[] ProcessingState — generic execution buffer        │
│                                                             │
│  ColumnarIndexBuilder                                       │
│    Histogram sort O(n+k) · Prefix-sum grouping              │
│    RefGroup[] from histogram (no merge-join)                │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                    Execution Planner                        │
│  Strategy selector: Histogram · Radix · StructSort          │
│  Cost-based plan: Hash · SortMerge · Range · Aggregate      │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                    Matching Engine                          │
│                                                             │
│  Strategy pipeline (ordered, composable)                    │
│    ExactMatchStrategy    — reference + amount + currency    │
│    FuzzyMatchStrategy    — normalized reference similarity  │
│    PartialMatchStrategy  — split / consolidated heuristics  │
│                                                             │
│  Hash-based index · MatchContext · CAS target claiming      │
│  Parallel execution · ConfidenceScore                       │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                   Match Evidence                            │
│  ClosestCandidate · AmountDifference · DateDifference       │
│  ConfidenceScore · CandidateHints                           │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│               Exception Classification                      │
│                                                             │
│  8-phase cascade (columnar, cache-local scans)              │
│    1. DuplicateInSource    5. AmountMismatch                │
│    2. DuplicateInTarget    6. DateMismatch                  │
│    3. SplitPayment         7. MissingInTarget               │
│    4. ConsolidatedPayment  8. MissingInSource               │
│                                                             │
│  ClassificationResult · Lazy GetResults()                   │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                   Exception Cases                           │
│  ExceptionCase · ExceptionCandidate · SuggestedAction       │
│  Severity · ResolutionState · Assignment                    │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│              Workflow / Manual Resolution                   │
│  Exception queue · Assignment · Comments · Approvals        │
│  Manual match · SLA · Escalation                            │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                   Audit and Reporting                       │
│  Immutable audit log · Full replay · Evidence versioning    │
│  Dashboard · KPIs · Break analysis · Regulatory export      │
└─────────────────────────────────────────────────────────────┘
```

---

## Rule Authoring Pipeline

Rules can be authored at three levels, all compiling to the same
execution plan:

```
CedarReconQL (domain expert, human-readable DSL)
    ↓
AST
    ↓
ReconciliationDefinition (developer, JSON metadata)
    ↓
Metadata Compiler
    ↓
Execution Plan
    ↓
Columnar Engine
```

**CedarReconQL** is for reconciliation officers, treasury analysts, and
compliance teams who understand the financial domain but should not need
to write code or JSON. It compiles directly to `ReconciliationDefinition`.

**ReconciliationDefinition** is for developers and system integrators who
prefer programmatic control. It is also the interchange format — imported,
exported, versioned, and validated.

Both paths produce identical execution plans. The columnar engine has no
knowledge of how the plan was authored.

---

## Data Residency

CedarRecon processes data **in-memory, in-process**. This is a deliberate
architectural choice with the following implications:

**What this means:**
- Reconciliation data is loaded into memory for the duration of a run
- No data is written to external storage by the engine itself
- Data does not survive process restart (no built-in persistence layer)
- A single reconciliation run is bounded by available memory

**Why this choice:**
- Zero network latency — all operations are CPU and memory bound
- No external dependencies during execution — no database, no message bus
- Predictable, measurable performance — cache locality is a first-class
  design concern, not an afterthought
- Testable in isolation — a unit test can run a complete reconciliation
  with no infrastructure

**What this means for your deployment:**
- Ingestion (reading source data) happens before the engine starts
- Results (matched pairs, exception cases) are returned to the caller
  to persist however the consuming system requires
- For datasets that exceed available memory, chunking and partitioning
  are provided (v6.0.0 distributed execution)
- The engine is stateless between runs by design

---

## Design Principles

### 1. Measure before adopting

Every architectural decision in the execution engine is backed by
BenchmarkDotNet evidence. Intuition about what should be faster is
treated as a hypothesis to test, not a conclusion. Negative results
(approaches that were benchmarked and rejected) are documented
alongside positive results.

### 2. Correctness before performance

The equivalence test suite (`ExceptionClassifierEquivalenceTests`, 28 tests)
is the non-negotiable correctness gate. No performance optimization is
accepted until all equivalence tests pass. A faster wrong answer is
worse than a slower right one.

### 3. Column isolation on the hot path

Scan phases access only the columns they need. `DuplicateScan` touches
only `ProcessingState`. `MismatchScan` touches only `AmountMinor`,
`DayNumber`, and `ProcessingState`. This is enforced by the columnar
layout — no phase can accidentally load irrelevant data into cache.

### 4. ProcessingState is an execution buffer, not a domain field

`byte[] ProcessingState` has operator-local semantics. During classification
it holds classification state. A future matching operator will give it
different semantics. The byte values are never exposed outside the engine —
they are translated to stable domain types (`DiscrepancyType`, `MatchedPair`)
before results are returned.

### 5. Pure functions for reconciliation logic

`ClassifyColumnar()` and `Match()` are pure functions: same inputs,
same outputs, always. No side effects, no external lookups, no shared
mutable state during execution. This makes the engine testable,
reproducible, and safe to call from any context.

### 6. Metadata-driven, not code-driven

Starting from v2.0.0, reconciliation rules are expressed as
`ReconciliationDefinition` metadata, not as compiled code. This means:
- Rules can be changed without redeployment
- Rules can be authored by domain experts (via CedarReconQL)
- Rules can be validated at compile time before execution
- Rules can be versioned, audited, and replayed

### 7. Domain vocabulary

CedarRecon uses the vocabulary of financial reconciliation practitioners,
not the vocabulary of software engineering. `ReconRecord` not `Entity`.
`MatchKeyId` not `ForeignKey`. `AmountMismatch` not `ValueDelta`. This
makes the system readable to domain experts and reduces the translation
layer between business intent and technical implementation.

---

## Current Implementation Status

| Component | Status | Version |
|---|---|---|
| Columnar execution engine | ✅ Complete | v0.9.x |
| Histogram sort index builder | ✅ Complete | v0.9.x |
| Exception classifier (8 phases) | ✅ Complete | v0.9.x |
| Matching engine (3 strategies) | ✅ Complete | v0.9.x |
| Generic financial model (ReconRecord) | ☐ Planned | v1.1.0 |
| Reference data framework | ☐ Planned | v1.2.0 |
| Quality gates | ☐ Planned | v1.3.0 |
| Ingestion framework | ☐ Planned | v1.4.0 |
| Metadata-driven reconciliation | ☐ Planned | v2.0.0 |
| CedarReconQL DSL | ☐ Planned | v3.1.0 |
| Execution planner | ☐ Planned | v4.0.0 |
| Enterprise platform | ☐ Planned | v5.x |
| Distributed execution | ☐ Planned | v6.0.0 |

---

## Repository Structure

```
CedarRecon/
├── src/
│   ├── CedarRecon.Domain/
│   │   ├── Entities/          — Transaction, ReconRecord (v1.1+)
│   │   ├── ValueObjects/      — Money, TransactionReference
│   │   ├── Enums/             — DiscrepancyType, MatchStrategy
│   │   └── Pipelines/         — IExceptionClassifier, IMatchStrategy
│   ├── CedarRecon.Application/
│   │   ├── Classification/
│   │   │   └── Indexed/       — Columnar engine, histogram builder
│   │   └── Matching/          — Hash engine, strategy pipeline
│   └── CedarRecon.Infrastructure/
│       └── (future: persistence, connectors)
├── tests/
│   ├── CedarRecon.Tests.Unit/
│   └── CedarRecon.Tests.Performance/
│   └── CedarRecon.Tests.Integration/
└── docs/
    ├── ARCHITECTURE.md        — this document
    ├── 05-exception-classification-engine.md
    ├── 06-columnar-execution-engine.md
    ├── 07-index-build-algorithms.md
    ├── 08-histogram-columnar-builder.md
    └── ADR-columnar-classification-engine.md
```

---

## Further Reading

| Document | Description |
|---|---|
| [ROADMAP.md](../ROADMAP.md) | Version-by-version release plan |
| [ADR-columnar-classification-engine.md](./adrs/ADR-columnar-classification-engine.md) | Why we adopted the columnar model |
| [05-exception-classification-engine.md](./adrs/05-exception-classification-engine.md) | Classification domain and cascade |
| [06-columnar-execution-engine.md](./adrs/06-columnar-execution-engine.md) | Execution model and ProcessingState |
| [07-index-build-algorithms.md](./adrs/07-index-build-algorithms.md) | Full investigation with all negative results |
| [08-histogram-columnar-builder.md](./adrs/08-histogram-columnar-builder.md) | Histogram sort algorithm |
