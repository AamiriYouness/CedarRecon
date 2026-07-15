# CedarRecon Architecture

CedarRecon is a high-performance financial reconciliation **execution engine**.
It is not a library and not a CRUD application. It is a platform for banks,
insurers, payment processors, and treasury operations, engineered with the
same discipline as analytical query engines such as DuckDB, Apache Arrow,
and Velox: columnar execution, cost-based planning, and a strict separation
between *what to match* and *how to execute the match*.

---

## 1. Execution Pipeline

Every reconciliation run flows through the same pipeline, end to end:

```
+------------------------------------------------------------------+
|  Job Definition (YAML)                                           |
|  sources | field mapping | tolerance parameters | schedule       |
+-------------------------------+----------------------------------+
                                |  parameter injection ($tolerance.amount, ...)
                                v
+------------------------------------------------------------------+
|  ReconQL (.reconql)                                              |
|  match logic only -- parameterized, deployment-agnostic          |
+-------------------------------+----------------------------------+
                                |  lexer -> parser -> AST -> semantic analysis
                                v
+------------------------------------------------------------------+
|  Logical Plan                                                    |
+-------------------------------+----------------------------------+
                                |  statistics + cost model
                                v
+------------------------------------------------------------------+
|  Physical Plan                                                   |
+-------------------------------+----------------------------------+
                                v
+------------------------------------------------------------------+
|  CedarRecon.Indexing                                             |
|  columnar batches (struct-of-arrays) | histogram sort O(n+k)     |
|  reference interning (string -> dense int)                       |
+-------------------------------+----------------------------------+
                                v
+------------------------------------------------------------------+
|  CedarRecon.Execution                                            |
|  matching strategies: Exact -> Fuzzy -> Partial (composable)     |
|  hash engine O(1) lookup | lock-free claims (CAS)                |
+-------------------------------+----------------------------------+
                                v
+------------------------------------------------------------------+
|  CedarRecon.Classification                                       |
|  8-phase exception cascade | byte[] ProcessingState              |
|  no LINQ on the hot path                                         |
+-------------------------------+----------------------------------+
                                v
+------------------------------------------------------------------+
|  Materialization -> Output                                       |
|  matched sets | exceptions | audit trail                         |
+------------------------------------------------------------------+
```

---

## 2. Separation of Concerns

The separation below is absolute. Each layer is ignorant of the layers
above it by design.

| Concern            | Artifact                | Owns                                                         | Knows nothing about      |
|--------------------|-------------------------|--------------------------------------------------------------|--------------------------|
| **Deployment**     | Job Definition (YAML)   | sources, field mapping, tolerance parameters, schedule       | match logic, execution   |
| **Business logic** | ReconQL (`.reconql`)    | match rules only, parameterized via `$tolerance.amount` etc. | data sources, scheduling |
| **Strategy**       | Planner                 | reads both, produces the Physical Plan via cost model        | I/O, persistence         |
| **Execution**      | Engine                  | runs the Physical Plan against columnar batches              | YAML, ReconQL — entirely |

Consequences:

- The same `.reconql` file runs unchanged across environments; only the
  Job Definition changes between dev, UAT, and production.
- Tolerances are *parameters injected at plan time*, never constants
  baked into match logic.
- The engine can be tested and benchmarked without a single line of YAML
  or ReconQL in scope.

---

## 3. Project Structure (Monorepo)

```
src/
├── CedarRecon.Core/
├── CedarRecon.Indexing/
├── CedarRecon.Execution/
├── CedarRecon.Classification/
├── CedarRecon.ReconQL/
├── CedarRecon.JobDefinition/
├── CedarRecon.Infrastructure/
├── CedarRecon.Runtime/
├── CedarRecon.Api/
└── CedarRecon.Enterprise/
tests/
├── CedarRecon.Tests.Unit/
├── CedarRecon.Tests.Integration/
└── CedarRecon.Indexing.Tests/
```

| Project                     | References                        | Introduced | Role                                                  |
|-----------------------------|-----------------------------------|------------|-------------------------------------------------------|
| `CedarRecon.Core`           | nothing                           | v0.9.0     | Domain model, all interface contracts                 |
| `CedarRecon.Indexing`       | nothing                           | v0.9.0     | Columnar primitives — `int[]`/`long[]`/`byte[]` only  |
| `CedarRecon.Execution`      | Core + Indexing                   | v0.9.0     | Matching engine and strategies                        |
| `CedarRecon.Classification` | Core + Indexing                   | v0.9.0     | Exception classification cascade                      |
| `CedarRecon.ReconQL`        | Core                              | v1.0.0     | Lexer, parser, AST, logical plan                      |
| `CedarRecon.JobDefinition`  | Core                              | v1.0.0     | YAML schema, parameter injection                      |
| `CedarRecon.Infrastructure` | Core only                         | v0.9.0     | Implementations of Core interfaces (I/O, persistence) |
| `CedarRecon.Runtime`        | Core + Execution + Classification | v1.2.0+    | Ingestion, orchestration, quality gates               |
| `CedarRecon.Api`            | Core + Execution + Classification | v3.0.0     | HTTP surface                                          |
| `CedarRecon.Enterprise`     | Core + Indexing + Execution       | v3.0.0+    | Workflow, audit, RBAC, dashboard                      |

### Dependency rules (absolute, no exceptions)

1. `Infrastructure` does **not** reference `Execution` or `Classification`.
2. `Indexing` has **zero** domain type references — no `Transaction`,
   no `ReconRecord`. It operates on primitive arrays only.
3. Every interface `Infrastructure` implements lives in `Core`.
4. Interface names use **domain vocabulary**, not technical vocabulary:

   | Correct                   | Rejected       |
   |---------------------------|----------------|
   | `IDatasetReader`          | `IFileParser`  |
   | `IExceptionNotifier`      | `IEmailSender` |
   | `IReconciliationJobQueue` | `IJobScheduler`|
   | `IAuditLogWriter`         | —              |
   | `ITransactionRepository`  | —              |

### Pragmatic decisions

Deliberate, documented trade-offs (see ADR-01), not omissions:

- **No repository pattern.** EF Core `DbContext` is used directly.
  The abstraction cost is not justified; the coupling is accepted and honest.
- **No MediatR.** Direct method calls until indirection earns its place.
- **No FluentValidation framework.** Validation is explicit code.

---

## 4. Design Principles

1. **Measure before adopting.** No optimization ships without a
   BenchmarkDotNet result at N=1M. Negative results are documented as
   first-class findings and never retried (ADR-05).
2. **Columnar first.** Hot-path data lives in struct-of-arrays batches
   (`ColumnarTransactionBatch`), not object graphs.
3. **Primitives at the bottom.** `CedarRecon.Indexing` speaks
   `int[]`, `long[]`, `byte[]`. Domain meaning is applied above it.
4. **Exact money.** Amounts are minor units at scale 10^4
   (`MoneyMinorUnitsConverter`); inputs beyond 4 decimal places throw.
5. **Lock-free claiming.** Match ownership is resolved with CAS
   (`MatchContext`, single `TryAdd`), never locks on the hot path.
6. **Composable strategies.** Matching is a pipeline of independent
   strategies (`Exact -> Fuzzy -> Partial` by default), each claiming
   candidates in descending score order.
7. **Determinism.** Same inputs, same plan, same output — a hard
   requirement for financial audit.

---

## 5. Data Residency

CedarRecon executes where the data lives. The engine is deployed inside
the customer's infrastructure (on-premises or private cloud); transaction
data is never transmitted to any external service, and the engine emits
no telemetry containing financial data. Ingested datasets, intermediate
columnar batches, and reconciliation outputs remain within the customer's
storage boundary for the full lifecycle of a run.

---

## 6. Performance Engineering Priorities

In strict order. When two optimizations conflict, the higher item wins.

1. **Algorithmic complexity** — O(n+k) histogram sort for index builds,
   O(1) hash lookup for matching. No O(n log n) where counting works.
2. **Memory layout** — struct-of-arrays columnar batches; scan-friendly,
   cache-line-dense representations on every hot path.
3. **Allocation discipline** — no LINQ on hot paths, `byte[]` state
   machines (`ClassificationStateBytes`), lazy materialization
   (`ClassificationResult.GetResults()`).
4. **Cache locality** — sequential array traversal over pointer chasing;
   `RefGroup` start/count offsets into pre-sorted arrays.
5. **String elimination** — `ReferenceInterner` maps references to dense
   sequential `int` IDs once at ingestion; downstream comparisons are
   integer comparisons.
6. **Contention avoidance** — CAS-based claims, atomic
   `_targetIndex + _context` swaps, no shared locks in matching.
7. **JIT mechanics** — `AggressiveInlining` where measured to matter,
   branch-predictable phase loops.
8. **Regression protection** — CI benchmark gates; a >=15% regression
   fails the build.

---

## 7. Benchmark Discipline

All results below use the fixed configuration:

```
warmupCount: 3, iterationCount: 15, launchCount: 1
ScenarioBuilder fixed distribution:
  60% noise/1:1 | 15% duplicates | 10% splits
  10% consolidations | 5% mismatches + matched-pair sampling
N in { 10_000, 100_000, 1_000_000 }
```

Pure-random data (e.g. naive Bogus generation) is prohibited — it does
not reproduce the reference-group skew that dominates real workloads.

### Proven results (N = 1M)

| Measurement                 | Baseline    | Columnar   | Delta        |
|-----------------------------|-------------|------------|--------------|
| Whole-method classification | 1,368 ms    | 646 ms     | 2.1x faster  |
| MismatchScan                | 12.57 µs/1K | 4.54 µs/1K | -64%         |
| MissingSweep                | 2.96 µs/1K  | 1.45 µs/1K | -51%         |
| IndexBuild (vs struct-sort) | 395 µs/1K   | 296 µs/1K  | -25%         |

### Confirmed negative results (never retry)

| Hypothesis                              | Outcome at N=1M                |
|-----------------------------------------|--------------------------------|
| `FrozenDictionary`                      | -41% (slower)                  |
| Index-sort (`int[] rowOrder`)           | SplitConsolidatedScan +61%     |
| `ReferenceInterner` without reverse map | +5–34% time, reverted          |
| Scatter-sort                            | IndexBuild +18%                |
| ScaledLong `ModuloResolver`             | 3.36x slower than `decimal`    |

---

## 8. Current Implementation Status (v0.9.0)

| Component                       | Current location                      | Target project              | Status                      |
|---------------------------------|---------------------------------------|-----------------------------|-----------------------------|
| `ColumnarTransactionBatch`      | `Application/Classification/Indexed`  | `CedarRecon.Indexing`       | Implemented                 |
| `ColumnarIndexBuilder`          | `Application/Classification/Indexed`  | `CedarRecon.Indexing`       | Histogram sort O(n+k)       |
| `ReferenceInterner`             | `Application/Classification/Indexed`  | `CedarRecon.Indexing`       | Sequential dense IDs        |
| `RefGroup`                      | `Application/Classification/Indexed`  | `CedarRecon.Indexing`       | Implemented                 |
| `MoneyMinorUnitsConverter`      | `Application/Classification/Indexed`  | `CedarRecon.Indexing`       | Scale 10^4, throws on >4dp  |
| `ClassificationStateBytes`      | `Application/Classification/Indexed`  | `CedarRecon.Indexing`       | Internal byte constants     |
| `HashMatchingEngine`            | `Application/Matching`                | `CedarRecon.Execution`      | O(1), atomic swap           |
| `MatchContext`                  | `Application/Matching`                | `CedarRecon.Execution`      | CAS, single `TryAdd`        |
| `ExactMatchStrategy`            | `Application/Matching`                | `CedarRecon.Execution`      | Amount+Currency+Date        |
| `FuzzyMatchStrategy`            | `Application/Matching`                | `CedarRecon.Execution`      | Scored, descending TryClaim |
| `PartialMatchStrategy`          | `Application/Matching`                | `CedarRecon.Execution`      | Divisor relationship        |
| `MatchStrategyFactory`          | `Application/Matching`                | `CedarRecon.Execution`      | Exact -> Fuzzy -> Partial   |
| `ColumnarExceptionClassifier`   | `Application/Classification`          | `CedarRecon.Classification` | 8-phase cascade             |
| `ClassificationResult`          | `Application/Classification`          | `CedarRecon.Classification` | Lazy `GetResults()`         |
| Equivalence tests               | `ExceptionClassifierEquivalenceTests` | `tests/`                    | 28/28 passing               |
| Phase benchmarks                | `ColumnarClassifierPhaseBenchmark`    | `tests/`                    | 6 phases                    |
| Whole-method benchmark          | `ExceptionClassifierBenchmark`        | `tests/`                    | Dictionary vs Columnar      |
| Monorepo extraction             | —                                     | all `src/` projects         | In progress (ADR-06)        |
| Statistics + cost-based planner | —                                     | `CedarRecon.ReconQL`        | Planned v1.0.0              |
| ReconQL v1 / JobDefinition      | —                                     | v1.0.0 projects             | Planned v1.0.0 (ADR-07)     |

---

## Related documents

- `docs/ROADMAP.md` — public version plan v0.9.0 -> v5.0.0
- `docs/adr/` — ADR-01 through ADR-07 (architecture decision records)
