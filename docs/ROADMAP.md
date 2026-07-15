# CedarRecon Roadmap

Public roadmap for the CedarRecon reconciliation engine. Each version is
a coherent capability milestone; versions ship when their acceptance
criteria and benchmark gates pass, not on a calendar.

For architecture details behind these milestones, see
[ARCHITECTURE.md](ARCHITECTURE.md).

---

## v0.9.0 — Engine Core *(current)*

**Goal:** A proven columnar reconciliation core with measured, reproducible
performance.

**Features**
- Columnar classification engine: struct-of-arrays batches, histogram
  sort O(n+k), reference interning, 8-phase exception cascade.
- Composable matching pipeline: Exact, Fuzzy, and Partial strategies over
  an O(1) hash matching engine with lock-free (CAS) claim resolution.
- Exact money handling via minor units (scale 10^4).
- Equivalence test suite guaranteeing the columnar engine produces
  identical results to the reference implementation.
- Benchmark suite with fixed workload distribution and CI regression
  gates (≥15% regression fails the build).

**Architecture additions**
- Monorepo restructure into `CedarRecon.Core`, `CedarRecon.Indexing`,
  `CedarRecon.Execution`, `CedarRecon.Classification`, and
  `CedarRecon.Infrastructure`.
- `CedarRecon.Indexing` isolated as a primitives-only project with zero
  domain type references.
- Docker image published for reproducible runs.

**Measured baseline:** 2.1x faster than the dictionary-based reference
classifier at N=1M (646 ms vs 1,368 ms).

---

## v1.0.0 — Full Engine

**Goal:** Complete the query-engine pipeline: declarative match logic in,
cost-based physical plan out.

**Features**
- **ReconQL v1** — a declarative DSL for match logic: lexer, parser, AST,
  semantic analysis, logical plan generation.
- **Job Definitions (YAML)** — deployment configuration: data sources,
  field mapping, tolerance parameters, scheduling. Parameters are
  injected into ReconQL at plan time (`$tolerance.amount`), keeping match
  logic environment-agnostic.
- **Cost-based planner** — chooses the physical execution strategy from
  dataset statistics rather than fixed heuristics.
- **Statistics subsystem** — cardinality, distribution, and key-skew
  statistics feeding the cost model.
- New execution strategies: **SortMerge**, **Aggregate**, and
  **Tolerance** matching.

**Architecture additions**
- `CedarRecon.ReconQL` (compiler front-end and logical planning).
- `CedarRecon.JobDefinition` (YAML schema and parameter injection).
- Planner layer separating logical plans from physical plans.

---

## v1.1.0 — Generic Model

**Goal:** Reconcile any record shape, not just bank transactions.

**Features**
- Generic `ReconRecord` model replacing the fixed transaction schema.
- Configurable `MatchKeyDefinition`: which fields form match keys,
  their types, and their comparison semantics are declared per job.

**Architecture additions**
- Schema abstraction between ingestion and the columnar engine; the
  engine remains schema-agnostic and operates on typed columns.

---

## v1.2.0 — Ingestion

**Goal:** First-class connectivity to the formats reconciliation teams
actually receive.

**Features**
- **MT940** (SWIFT customer statement) reader.
- **CAMT.053** (ISO 20022 bank-to-customer statement) reader.
- **CSV** reader with configurable mapping.
- **Excel** reader.

**Architecture additions**
- `CedarRecon.Runtime` project: ingestion orchestration built on the
  `IDatasetReader` contract, keeping format-specific code out of the
  engine.

---

## v1.3.0 — Quality Gates

**Goal:** Trustworthy pipelines — bad data is caught, quarantined, and
reported, never silently matched.

**Features**
- Input validation with per-job rules (required fields, formats, ranges).
- Dead letter queue for rejected records with full rejection reasons.
- Fail-fast mode for structural errors; tolerance thresholds for
  record-level errors.

**Architecture additions**
- Validation stage between ingestion and indexing in the runtime
  pipeline; quality metrics surfaced per run.

---

## v2.0.0 — Metadata-Driven Platform + ReconQL v2

**Goal:** Reconciliations defined entirely from metadata; ReconQL grows
into a full language.

**Features**
- **ReconQL v2** — full grammar: multi-source joins, computed match
  keys, conditional rules, custom classification outcomes.
- AST-to-engine compiler replacing the v1 logical-plan translation.
- Metadata-driven job catalog: new reconciliations deployed without
  code changes.

**Architecture additions**
- Compiler backend targeting the physical operator set directly.
- Job catalog and metadata store.

---

## v3.0.0 — Enterprise Platform

**Goal:** From engine to platform: the operational layer that
reconciliation teams work in every day.

**Features**
- Exception workflow: assignment, investigation states, resolution,
  and escalation.
- Full audit trail across job definition changes, runs, and manual
  actions.
- REST API for job management, run execution, and results retrieval.
- Operations dashboard: run health, match rates, exception aging.
- Role-based access control.

**Architecture additions**
- `CedarRecon.Api` (HTTP surface) and `CedarRecon.Enterprise`
  (workflow, audit, RBAC) projects.

---

## v4.0.0 — Distributed Execution

**Goal:** Scale beyond a single machine while keeping single-node
performance discipline.

**Features**
- Coordinator/worker execution model with partitioned reconciliation
  runs.
- Kafka-based ingestion and event streams.
- Kubernetes deployment: operators, autoscaling, rolling upgrades.

**Architecture additions**
- Distributed planner extensions: partitioning strategy becomes part of
  the physical plan.
- Cluster coordination and shuffle layer.

---

## v5.0.0 — ML-Assisted Matching

**Goal:** Machine learning where it measurably helps — after candidate
generation, never instead of it.

**Features**
- Post-candidate ML scoring: deterministic strategies generate
  candidates; a trained model ranks ambiguous ones.
- Active learning from analyst decisions on exceptions.

**Architecture additions**
- Scoring extension point in the matching pipeline; model lifecycle
  management (training data capture, versioning, evaluation).

---

## Principles that hold across every version

- Deterministic core: ML assists ranking, it never decides matches alone.
- Every performance claim is a published BenchmarkDotNet result.
- Data residency: the engine runs inside your infrastructure; financial
  data never leaves it.
- Backwards compatibility for ReconQL and Job Definitions within a major
  version.
