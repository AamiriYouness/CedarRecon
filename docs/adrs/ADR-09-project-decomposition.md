# ADR-06: Project decomposition — Core, Indexing, Execution, Classification, Infrastructure

**Status:** Accepted
**Date:** 2026-07-15
**Supersedes:** the single Domain/Application/Infrastructure layout
**Related:** ADR-01 (Pragmatic Clean Architecture), ADR-05 (Columnar
classification engine), ADR-07 (Job Definition / ReconQL separation)

## Context

Through v0.9.x the codebase lived in three projects: CedarRecon.Domain,
CedarRecon.Application, CedarRecon.Infrastructure. The columnar engine
(Application/Classification/Indexed), the matching engine
(Application/Matching), and the exception cascade all shared one
assembly. Nothing prevented any class from referencing any other; the
architectural boundaries described in our documentation existed only as
discipline.

The roadmap adds compilers (ReconQL), a planner, ingestion, and an
enterprise surface. Each new subsystem multiplies the ways discipline
can silently fail. We need boundaries the compiler enforces, decided
before the codebase grows around their absence.

## Decision

Split into five projects with a fixed reference graph:

    CedarRecon.Core            references: nothing
    CedarRecon.Indexing        references: nothing
    CedarRecon.Execution       references: Core + Indexing
    CedarRecon.Classification  references: Core + Indexing
    CedarRecon.Infrastructure  references: Core only

All projects live in one repository (monorepo), not git submodules.
The reference graph is enforced by architecture tests that fail the
build on violation.

### Why Core references nothing

Core holds the domain model and every interface contract
(IDatasetReader, IExceptionNotifier, IReconciliationJobQueue,
IAuditLogWriter, ITransactionRepository). A dependency-free Core means
every other project can reference it without inheriting anything, and
contracts can never accidentally depend on their implementations.
Interface names use domain vocabulary, not technical vocabulary,
because the contract describes what the domain needs, not how a
particular technology provides it.

### Why Indexing is its own project — and references nothing

Indexing contains the columnar primitives: ColumnarTransactionBatch,
ColumnarIndexBuilder, ReferenceInterner, RefGroup,
MoneyMinorUnitsConverter, ClassificationStateBytes. Its public surface
is int[]/long[]/byte[]/decimal — zero domain types, by rule.

The rule exists because the columnar layer is the performance-critical
substrate of the entire engine (ADR-05: 2.1x whole-method speedup at
N=1M came from this layer). Performance work here must be reasoned
about in terms of memory layout and cache behavior alone. Every domain
type that leaks in couples layout decisions to domain evolution: when
v1.1.0 replaces the transaction model with generic ReconRecord, an
Indexing layer that knows Transaction would need rewriting; one that
speaks primitives needs nothing. Keeping it in-assembly with domain
code made that leak a single careless using-directive away. A separate
project with no references makes it a compile error.

### Why Execution and Classification are separate from each other

They are different pipeline stages with different contracts and
different futures:

- Execution answers "which records match": strategies, hash engine,
  CAS-based claiming (MatchContext). Its consumers are the pipeline
  and, from v1.0.0, the cost-based planner choosing among its
  strategies.
- Classification answers "what is wrong with everything that did not
  match": the 8-phase cascade producing ClassificationResult. Its
  output is the product surface — exception counts, match rates, the
  facts that run summaries, the v3.0.0 dashboard, and client reporting
  are built from.

Neither calls the other; both are called by the pipeline and both
consume Indexing. Merged, nothing stops the classifier from reaching
into claiming internals — same-assembly access is unrestricted. Split,
the independence is compiler-enforced.

The growth pressure also diverges: v1.0.0 adds three strategies to
Execution; v3.0.0 and v5.0.0 add consumers of Classification's output.
When the API and dashboard arrive, they reference Classification (and
Core) without dragging in strategy code and claiming machinery they
have no business seeing.

Boundary rule that keeps this split honest: Classification produces
classified facts; it never computes KPIs. Aggregation (counts by type,
rates, trends) belongs to consumers — Runtime for run summaries,
Enterprise/Api for dashboards. The moment reporting logic creeps into
Classification, it becomes a reporting library and the split stops
paying for itself.

### Why Infrastructure references Core only

Infrastructure implements Core's contracts (I/O, persistence). If it
could reference Execution or Classification, implementation detail
could flow into engine behavior and the engine could not be tested
without infrastructure. The one-way rule keeps every engine component
constructible in tests from Core contracts alone. Per ADR-01, EF Core
is used directly inside Infrastructure — the coupling is accepted and
honest, and it stays contained behind Core interfaces.

### Why a monorepo, not submodules or packages

Considered: extracting Indexing as a git submodule or a NuGet package.
Rejected for now. The engine's phases co-evolve — a change to RefGroup
layout lands with the Execution and Classification changes that exploit
it, in one commit, covered by one benchmark run against one baseline.
Submodules split that atomicity; packages add versioning ceremony with
exactly one consumer. Project boundaries give us the isolation we need
(reference rules, independent tests and benchmarks) without the
release overhead we do not. If Indexing ever gains an external
consumer, packaging it is straightforward precisely because it
references nothing.

### Relocation of the reference classifier

The dictionary/GroupBy ExceptionClassifier moves to
tests/CedarRecon.Tests.Unit/Reference/. It is the equivalence-test
oracle and the source of the 1,368 ms baseline, not a product code
path; no src/ project may reference it. It is scheduled for deletion
at v1.1.0, when regenerated golden files take over the oracle role.

## Consequences

Positive:
- Dependency rules are compile errors, not review comments
- Indexing is benchmarkable and testable in isolation; its baselines
  (IndexBuild 296 µs/1K at N=1M) attach to a project, not a folder
- v1.1.0's generic model touches Core and consumers, not the columnar
  layer
- Future ReconQL, JobDefinition, Runtime, Api projects slot into an
  established graph instead of negotiating one retroactively

Negative / accepted:
- Five csproj files and an architecture-test project to maintain for
  what is today a small codebase
- Classification is currently two classes; the project looks thin.
  Tripwire: if by v1.0.0 it has not grown and no roadmap consumer has
  materialized, fold it into Execution — merging small into small is
  cheap; the expensive direction is only the late split
- Cross-cutting changes now touch multiple projects and their tests

## Alternatives considered

1. Keep Domain/Application/Infrastructure — rejected: boundaries
   remain discipline-only, and the codebase is about to grow past the
   point where discipline scales.
2. Single CedarRecon.Engine project with folders — rejected: folder
   conventions are not enforced by the compiler, and architecture
   tests on namespaces are weaker and easier to game than project
   references.
3. Indexing as submodule/package — rejected above; revisit only on a
   real external consumer.
