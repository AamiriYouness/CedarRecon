# CedarRecon Roadmap

CedarRecon is developed across seven phases, each with a single
architectural objective. Every phase builds directly on the previous one.
No phase is started before the prior phase is stable and tested.

---

## Phase 1 — High-Performance Core

### v0.9.x — Execution Engine (Current)

The performance-validated reconciliation core. All components benchmarked
with BenchmarkDotNet. All correctness gates passing.

**Matching**
- ✅ Clean Architecture (Domain / Application / Infrastructure)
- ✅ Generic matching engine
- ✅ Strategy pipeline (composable, ordered)
- ✅ Exact match strategy
- ✅ Fuzzy match strategy
- ✅ Partial heuristic match strategy
- ✅ Parallel execution with MatchContext and target claiming
- ✅ Hash-based indexing

**Classification**
- ✅ 8-phase cascade classifier
- ✅ Columnar execution engine (struct-of-arrays)
- ✅ Histogram sort index builder (O(n+k))
- ✅ Generic ProcessingState execution buffer
- ✅ Lazy ClassificationResult with streaming GetResults()
- ✅ ReferenceInterner (string → dense int, O(1) hot path)
- ✅ MoneyMinorUnitsConverter (decimal → long, scale 10^4)

**Tests and benchmarks**
- ✅ Unit tests
- ✅ Equivalence tests (28 tests, Dictionary vs Columnar)
- ✅ Phase isolation benchmarks (ColumnarClassifierPhaseBenchmark)
- ✅ Whole-method benchmarks (ExceptionClassifierBenchmark)
- ✅ Performance documentation with all negative results

**Documentation**
- ✅ Architecture decision records (ADR)
- ✅ Exception classification engine (doc 05)
- ✅ Columnar execution engine (doc 06)
- ✅ Index build algorithms investigation (doc 07)
- ✅ Histogram columnar builder (doc 08)

---

### v1.0.0 — CedarRecon Core (Stable)

**Goal**: stable public API, semantic versioning contract begins here.

- ☐  API surface review and stabilization
- ☐  Binary release package (CedarRecon-v1.0.0.zip via GitHub Release)
- ☐  Integration test suite (end-to-end matching + classification)
- ☐  Regression benchmark suite (performance gates on CI)
- ✅ `docs/ARCHITECTURE.md` complete
- ☐  `docs/matching-engine.md`
- ☐  `docs/matching-strategies.md`
- ☐  GitHub Release with changelog

---

## Phase 2 — Generic Financial Engine

### v1.1.0 — Generic Financial Model

**Goal**: remove banking-specific assumptions. Everything that is currently
`Transaction` becomes `ReconRecord` — usable by any financial domain.

```
ReconRecord         replaces Transaction
Money               amount + currency, ISO 4217 aware
ReferenceData       counterparty, instrument, account references
Dataset             named, typed collection of ReconRecord
DatasetDefinition   schema descriptor for a Dataset
```

Target domains unlocked: banking, insurance, treasury, payments, cards,
ERP, investments, reinsurance.

- ☐ `ReconRecord` domain model
- ☐ `Money` value object (currency-aware, minor-unit scaling per ISO 4217)
- ☐ `Dataset` / `DatasetDefinition`
- ☐ Migration of matching and classification engines to `ReconRecord`
- ☐ Backward compatibility layer for existing `Transaction`-based code

---

### v1.2.0 — Reference Data Framework

**Goal**: built-in financial reference data, extensible by domain.

**Built-in**
- ☐ ISO 4217 currency registry (3-decimal support: KWD, BHD, OMR)
- ☐ ISO 3166 country codes
- ☐ Business calendar (working days, settlement lag)
- ☐ Holiday calendar (configurable per country/exchange)

**Providers (plug-in)**
- ☐ `ICurrencyProvider`
- ☐ `IFXProvider` (exchange rates)
- ☐ `ICounterpartyProvider`
- ☐ `IInsuranceReferenceProvider`
- ☐ `ICardReferenceProvider`
- ☐ `IERPReferenceProvider`
- ☐ `IReinsuranceReferenceProvider`

---

### v1.3.0 — Quality Gates

**Goal**: validate data before it reaches the reconciliation engine.
Fail fast on bad data rather than producing incorrect results silently.

**Pipeline position**
```
File → Parser → Mapping → Normalization → Quality Gates → ReconRecord
```

**Features**
- ☐ Configurable validation rules (field, record, file level)
- ☐ Missing required fields
- ☐ Invalid currency / country codes
- ☐ Amount format violations
- ☐ Date range violations
- ☐ Duplicate detection (pre-reconciliation)
- ☐ Dead letter queue (quarantine invalid records)
- ☐ Warning mode (log and continue)
- ☐ Fail-fast mode (halt on first violation)

---

### v1.4.0 — Ingestion Framework

**Goal**: read financial data from enterprise sources without custom connectors.

**Flat files**
- ☐ CSV, TXT, Excel (xlsx)
- ☐ JSON, XML
- ☐ Parquet

**Financial message formats**
- ☐ MT940 (bank statement)
- ☐ MT942 (intraday statement)
- ☐ CAMT.052 (intraday notification)
- ☐ CAMT.053 (end-of-day statement)
- ☐ CAMT.054 (credit/debit notification)

**Database connectors**
- ☐ SQL Server
- ☐ Oracle
- ☐ PostgreSQL

**API connectors**
- ☐ REST (configurable, auth-aware)

---

## Phase 3 — Enterprise Matching

### v2.0.0 — Metadata-Driven Reconciliation

**Goal**: zero hardcoded reconciliation rules. All rules expressed as
data, not code. A reconciliation definition is a JSON document that
fully describes a reconciliation run.

```
ReconciliationDefinition
    DatasetDefinition[]
    MatchRuleDefinition[]
    ToleranceRule[]
    StrategyDefinition[]
    ExecutionProfile
```

- ☐ `ReconciliationDefinition` schema
- ☐ JSON serialization / deserialization
- ☐ Definition validator
- ☐ Definition-driven matching engine
- ☐ Definition-driven classification

---

### v2.1.0 — Match Evidence

**Goal**: replace bare `DiscrepancyType` with structured evidence showing
*why* a transaction was classified and *what* the closest candidate was.

```
ExceptionEvidence
    DiscrepancyType
    ClosestCandidate (ReconRecord?)
    AmountDifference (Money?)
    DateDifference   (int days?)
    ConfidenceScore
    CandidateHints[]
```

- ☐ `ExceptionEvidence` model
- ☐ Evidence population in classification cascade
- ☐ Evidence in `ClassificationResult`

---

### v2.2.0 — Exception Case Engine

**Goal**: structured exception lifecycle management.

```
ExceptionCase
    ExceptionCandidate[]
    SuggestedAction
    Severity
    Assignment
    ResolutionState
```

- ☐ `ExceptionCase` domain model
- ☐ `ExceptionCandidate` with ranked suggestions
- ☐ `SuggestedAction` (auto-match, manual review, escalate, write-off)
- ☐ `Severity` (blocking, warning, informational)
- ☐ `ResolutionState` (open, in-review, resolved, rejected)

---

### v2.3.0 — Aggregate Matching

**Goal**: match groups of transactions, not just one-to-one pairs.

- ☐ Group aggregate matching (many→one, one→many)
- ☐ Bounded subset sum (configurable search limits)
- ☐ Ambiguity detection (multiple valid aggregate matches)
- ☐ Timeout and search depth limits

---

## Phase 4 — Rule Platform

### v3.0.0 — Metadata Compiler

**Goal**: compile `ReconciliationDefinition` metadata into an optimized
execution plan. Validation at compile time, not at runtime.

```
Metadata
    ↓
Validation
    ↓
Compiled Rule
    ↓
Execution Plan
```

- ☐ Definition compiler
- ☐ Compile-time validation (referential integrity, type checking)
- ☐ Compiled rule representation
- ☐ Execution plan model

---

### v3.1.0 — CedarReconQL

**Goal**: a human-readable DSL for domain experts who understand
financial reconciliation but should not need to write JSON or code.
CedarReconQL compiles to `ReconciliationDefinition` — it is an authoring
surface, not an execution surface. The columnar engine executes; the DSL
describes intent.

**Example**

```
RULE "Nostro Exact Match"
MATCH ONE_TO_ONE
CANDIDATE BY
    Reference
    Currency
WHEN
    Amount == Amount
AND
    ValueDate WITHIN 2 DAYS
```

```
RULE "Split Payment"
MATCH ONE_TO_MANY
CANDIDATE BY
    Reference
WHEN
    SUM(Amount) == Amount
TOLERANCE
    Amount 0.01
```

**Pipeline**
```
CedarReconQL source
    ↓
Lexer / Parser
    ↓
AST
    ↓
Semantic validation
    ↓
ReconciliationDefinition
    ↓
Metadata Compiler (v3.0.0)
    ↓
Execution Plan
    ↓
Columnar Engine
```

- ☐ Language specification
- ☐ Lexer
- ☐ Parser → AST
- ☐ Semantic validator
- ☐ AST → ReconciliationDefinition compiler
- ☐ Error messages readable by domain experts (not developers)
- ☐ VS Code extension (syntax highlighting, basic IntelliSense)

---

## Phase 5 — Execution Planner

### v4.0.0 — Cost-Based Planner

**Goal**: the engine selects its own execution strategy based on data
characteristics, rather than using a fixed strategy.

```
Input statistics
    ↓
Planner
    ↓
Execution Plan
    ↓
Columnar Engine
```

**Plans**
- ☐ Hash plan (low cardinality, dense keys)
- ☐ Sort-merge plan (pre-sorted or nearly-sorted data)
- ☐ Range plan (date-range or amount-range matching)
- ☐ Aggregate plan (many-to-many, subset sum)

**Strategy selector**
- ☐ Histogram sort (dense integer keys, O(n+k))
- ☐ Radix sort (sparse/composite integer keys, O(n·d))
- ☐ Struct-sort fallback (arbitrary comparison keys, O(n log n))

---

### v4.1.0 — Adaptive Planner

**Goal**: learn from execution statistics to improve future plans.

- ☐ Execution statistics collection
- ☐ Cardinality estimation
- ☐ Plan cache
- ☐ Adaptive plan switching (replan mid-execution if statistics diverge)

---

## Phase 6 — Enterprise Platform

### v5.0.0 — Workflow Engine

- ☐ Exception queue
- ☐ Assignment (user, team, rule-based)
- ☐ Manual match
- ☐ Comments and attachments
- ☐ Approval workflows
- ☐ SLA tracking
- ☐ Escalation rules

### v5.1.0 — Audit Engine

- ☐ Immutable audit log
- ☐ Full replay capability
- ☐ Match evidence versioning
- ☐ Decision history
- ☐ Regulatory export

### v5.2.0 — Scheduling

- ☐ Job scheduling (cron, event-triggered)
- ☐ Worker pool
- ☐ Incremental reconciliation (only new/changed records)
- ☐ Retry with backoff
- ☐ Failure notifications

### v5.3.0 — Security

- ☐ RBAC (role-based access control)
- ☐ Granular permissions (view, match, approve, admin)
- ☐ Multi-tenancy with data isolation
- ☐ Audit of access and changes

### v5.4.0 — REST API

- ☐ OpenAPI specification
- ☐ Swagger UI
- ☐ .NET SDK (auto-generated from spec)
- ☐ Authentication (OAuth2 / API keys)

### v5.5.0 — Dashboard

- ☐ Exception queue view
- ☐ KPIs (match rate, exception rate, aging)
- ☐ Break analysis
- ☐ Operations dashboard (throughput, latency)
- ☐ Reconciliation history

---

## Phase 7 — Scale

### v6.0.0 — Distributed Execution

**Goal**: reconcile datasets that do not fit in a single process.

- ☐ Dataset chunking and partitioning
- ☐ Streaming ingestion
- ☐ Message bus integration (Kafka, Azure Service Bus, RabbitMQ)
- ☐ Cloud execution (Azure, AWS)
- ☐ Horizontal scaling with result merge

---

## Version Summary

| Version | Phase | Focus |
|---|---|---|
| v0.9.x | 1 | Execution engine (current) |
| v1.0.0 | 1 | Stable core, NuGet release |
| v1.1.0 | 2 | Generic financial model |
| v1.2.0 | 2 | Reference data |
| v1.3.0 | 2 | Quality gates |
| v1.4.0 | 2 | Ingestion |
| v2.0.0 | 3 | Metadata-driven reconciliation |
| v2.1.0 | 3 | Match evidence |
| v2.2.0 | 3 | Exception cases |
| v2.3.0 | 3 | Aggregate matching |
| v3.0.0 | 4 | Metadata compiler |
| v3.1.0 | 4 | CedarReconQL DSL |
| v4.0.0 | 5 | Cost-based planner |
| v4.1.0 | 5 | Adaptive planner |
| v5.0–5.5 | 6 | Enterprise platform |
| v6.0.0 | 7 | Distributed execution |
