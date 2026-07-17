# ADR-09: Project decomposition — Core, Indexing, Execution, Classification, Infrastructure

**Status:** Accepted
**Date:** 2026-07-15
**Supersedes:** the single Domain/Application/Infrastructure layout
**Related:** ADR-01 (Pragmatic Clean Architecture), ADR-05 (Columnar
classification engine). Job Definition / ReconQL separation is not yet
an ADR — no decision has been made and it is not implemented.

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

CedarRecon.Preparation is intentionally not created during the v0.9.0
mechanical restructure, and its eventual promotion to a project is not
decided by this ADR. The preparation-stage RFC defines Preparation as a
logical pipeline stage: domain-aware code that projects Transaction and,
where applicable, MatchedPair into Indexing-owned primitive contracts.
A named stage does not by itself justify a separate assembly.

A dedicated project is warranted only when Preparation requires one or
more of the following: a compiler-enforced dependency boundary;
independent tests or benchmarks; distinct buffer ownership or
lifecycle; multiple real consumers; independent ownership; future
packaging or reuse.

Until then, Preparation may live as a folder inside the orchestration
or consumer project that currently coordinates the stage, without
implying permanent architectural ownership.

See docs/rfcs/preparation-stage-extraction.md for the target
decomposition and implementation criteria.

If Preparation is later promoted to a project, its allowed references
will be Core + Indexing, and Indexing will not reference it:

    Core             → nothing
    Indexing         → nothing
    Preparation      → Core + Indexing   (if promoted to a project)
    Execution        → Core + Indexing
    Classification   → Core + Indexing
    Infrastructure   → Core

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

Indexing contains the domain-neutral physical data structures and
algorithms used to prepare records for matching and classification:
ColumnarTransactionBatch, RefGroup, ReferenceInterner,
MoneyMinorUnitsConverter, and ClassificationStateBytes (the
ProcessingState byte[] column). Its public surface is composed only of
platform and primitive types such as int, long, byte, decimal, strings,
spans, memories, and arrays. It exposes zero CedarRecon domain types by
rule.

ColumnarIndexBuilder was originally planned as an Indexing component
but was found during the Phase 3 restructure audit to combine three
separate responsibilities: domain-to-primitive encoding,
histogram/prefix-sum index construction, and composition of a
domain-facing result containing original Transaction collections and
MatchedPair-derived state. Its public API accepts
IReadOnlyList<Transaction> and IReadOnlyList<MatchedPair>, and its
hottest per-row loop reads domain value objects directly. It therefore
cannot enter the zero-domain-type Indexing boundary unchanged.

ReferenceInterner, however, is domain-neutral and remains an Indexing
responsibility despite its temporary location during the mechanical
restructure: it maps string keys to stable dense integer IDs and has no
dependency on Transaction, NormalizedReference, MatchedPair, or any
other CedarRecon domain type. Dense IDs in the range
[0, DistinctKeyCount) are an Indexing invariant because they enable
histogram counting, prefix sums, direct addressing, and compact group
representations. Its temporary location under Classification/Indexed
during the mechanical restructure reflects the current call graph only;
its target ownership is Indexing.

See docs/rfcs/preparation-stage-extraction.md for the target
decomposition. A future Preparation stage will read domain entities and
project them into Indexing-owned primitive contracts. The physical
index planner will then select an eligible construction strategy —
initially histogram, and later radix, comparison sort, hash,
partitioned, or external merge — based on key shape, statistics,
matching requirements, memory budget, hardware capabilities, and spill
availability.

Until that extraction lands, the unsplit ColumnarIndexBuilder remains
in Classification as a temporary accommodation. This placement is not
a statement that Classification owns domain encoding or physical index
construction.

The rule exists because Indexing is the performance-critical substrate
of the engine. ADR-05 measured a 2.1x whole-method speedup at N=1M from
changes in this layer. Performance work here must be reasoned about in
terms of memory layout, cache locality, allocation behavior, physical
algorithms, and hardware characteristics. Every domain type that leaks
into Indexing couples physical representation to domain evolution: when
v1.1.0 replaces the transaction model with generic ReconRecord, an
Indexing layer that knows Transaction would require redesign, while one
that accepts encoded columns remains unchanged. A separate
dependency-free project makes such coupling a compile-time error.

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

Execution's future MatchingPlanner is distinct from Indexing's future
PhysicalIndexPlanner. The matching planner selects reconciliation
execution semantics and produces physical requirements such as
ordering, required keys, streaming support, or partitioning
constraints. The physical index planner selects an eligible physical
representation satisfying those requirements within the available
memory, CPU, SIMD, topology, and spill constraints. Keeping both
planners in their respective projects prevents execution semantics from
being embedded in low-level indexing algorithms while still allowing
coordinated planning through a future higher-level reconciliation
planner.

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

ReferenceIndexBuilder (with its supporting ReferenceIndex<,>,
ISortStrategyFactory, StructSortStrategyFactory, and
IndexSortStrategyFactory) was found during the Phase 3 audit to have
zero references from any src/ project. It is the struct-sort/index-sort
investigation described in this ADR's item 4 background, retained as a
testing oracle rather than deleted outright. It relocates alongside the
Dictionary/GroupBy equivalence oracle in the test tree, not into
Classification or Indexing. ColumnarIndexBuilder is the only unsplit
builder that remains a live production path under Classification
pending the Preparation-stage extraction; ReferenceInterner's
Indexing-target placement (see below) is unaffected by this.

During the v0.9.0 mechanical restructure, ColumnarIndexBuilder and
ReferenceInterner remain co-located under Classification/Indexed
because that matches ColumnarIndexBuilder's current call graph and
avoids behavior changes during the project split. Their target
destinations are not identical: ReferenceInterner is a domain-neutral
Indexing component (see "Why Indexing is its own project" above), while
ColumnarIndexBuilder must be split between a future Preparation stage
and one or more physical Indexing strategies. Temporary co-location
must not be interpreted as shared permanent ownership.

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