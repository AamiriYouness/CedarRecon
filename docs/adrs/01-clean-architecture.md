# ADR-01 — Pragmatic Clean Architecture: Hybrid Layer Design

**Status**: Accepted  
**Date**: 2026  
**Deciders**: Youness (Senior .NET Engineer, CedarRecon)

---

## Context

CedarRecon is a financial reconciliation platform, not a CRUD application.
Its core is a computation engine — matching, classification, columnar
execution — not a thin wrapper around a database. The architectural style
must serve this reality.

Two competing concerns shaped this decision:

**Correctness and testability** require that domain logic is isolated from
infrastructure. The matching and classification engines must be testable
without a database, file system, or HTTP runtime.

**Pragmatism** requires avoiding over-engineering. Repository interfaces,
unit-of-work abstractions, CQRS, and MediatR solve problems CedarRecon
does not have yet. Every abstraction must earn its place.

---

## Decision

### Layer structure and dependency rules

```
┌──────────────────────────────────────────────────────────┐
│                        Domain                             │
│                                                           │
│  Entities:      Transaction, MatchedPair,                 │
│                 UnmatchedTransaction                       │
│  Value objects: Money, TransactionReference,              │
│                 ConfidenceScore, TransactionId            │
│  Enums:         DiscrepancyType, MatchStrategy            │
│  Interfaces:    IExceptionClassifier, IMatchStrategy,     │
│                 IDatasetReader, IReconciliationJobQueue,  │
│                 IExceptionNotifier, IAuditLogWriter,      │
│                 ITransactionRepository (future)           │
│  Options:       ReconciliationOptions, ToleranceRule      │
│                                                           │
│  References: nothing                                      │
└──────────────────────────────────────────────────────────┘
              ↑                          ↑
              │                          │
┌─────────────────────┐   ┌─────────────────────────────┐
│    Application      │   │       Infrastructure         │
│                     │   │                              │
│  Matching engine    │   │  Mt940DatasetReader          │
│  Classification     │   │  CsvDatasetReader            │
│  Columnar engine    │   │  CamtDatasetReader           │
│  Normalization      │   │  EF Core DbContext           │
│  Use cases          │   │  EmailExceptionNotifier      │
│                     │   │  AuditLogWriter              │
│  References:        │   │                              │
│  Domain only        │   │  References:                 │
│                     │   │  Domain only                 │
└─────────────────────┘   └─────────────────────────────┘
              ↑
┌──────────────────────────────────────────────────────────┐
│                     Presentation                          │
│                                                           │
│  CedarRecon.Api — ASP.NET Core minimal API               │
│  Scalar OpenAPI documentation                             │
│  HTTP endpoints                                           │
│                                                           │
│  References: Application + Domain                         │
└──────────────────────────────────────────────────────────┘
              ↑
┌──────────────────────────────────────────────────────────┐
│                  Composition Root                         │
│                                                           │
│  Lives in CedarRecon.Api (Program.cs)                    │
│  Only place that knows all layers                         │
│  Wires domain interfaces to implementations              │
│                                                           │
│  References: everything                                   │
└──────────────────────────────────────────────────────────┘
```

**The dependency rule is absolute and has no exceptions:**

```
Domain         → nothing
Application    → Domain only
Infrastructure → Domain only
Presentation   → Application + Domain
Composition    → everything
```

Infrastructure does NOT reference Application. Ever.

### Project structure

```
src/
├── CedarRecon.Domain/            → no references
├── CedarRecon.Application/       → references Domain only
├── CedarRecon.Infrastructure/    → references Domain only
└── CedarRecon.Api/               → references Application + Domain
                                     composition root in Program.cs
tests/
├── CedarRecon.Tests.Unit/        → references Application + Domain
└── CedarRecon.Tests.Integration/ → references Api (full pipeline)
```

---

## The Core Principle: All Interfaces in Domain

This is the decision that makes the dependency rule work with no exceptions.

Every interface that Infrastructure implements lives in Domain — not in
Application. This is the key insight that keeps the architecture clean:

**Domain defines what it needs. Infrastructure provides it.
Application orchestrates between them. Neither Application nor
Infrastructure ever needs to know about the other.**

### Interface ownership decision framework

The question for every interface: *does the domain need this contract,
or does only the use case need it?*

```
Interface                        Owner        Reason
──────────────────────────────────────────────────────────────────
IExceptionClassifier             Domain       Classification is domain behaviour
IMatchStrategy                   Domain       Matching is domain behaviour
ITransactionRepository           Domain       Domain aggregate persistence
IReconRecordRepository           Domain       Domain aggregate (v1.1+)
IReconciliationRunRepository     Domain       A run is a domain entity
IAuditLogWriter                  Domain       Audit is a domain invariant —
                                              a reconciliation without an
                                              audit trail is not valid in
                                              any regulated financial context
IFXRateProvider                  Domain       FX rate is domain reference data
ICurrencyRegistry                Domain       ISO 4217 is domain vocabulary
IDatasetReader                   Domain       The domain needs to receive records
                                              from somewhere — what format they
                                              come in is Infrastructure's concern
IReconciliationJobQueue          Domain       Job scheduling is a domain concept —
                                              renamed from IJobScheduler which
                                              sounds like infrastructure
IExceptionNotifier               Domain       Notification is a domain event
                                              consequence — renamed from
                                              IEmailSender which encodes the
                                              transport mechanism
```

### Naming is not cosmetic — it is architectural

`IEmailSender` sounds like Infrastructure. `IExceptionNotifier` sounds
like Domain. They can describe the same contract. The domain-vocabulary
name belongs in Domain; the infrastructure-vocabulary name does not.

This is not renaming for aesthetic reasons. It is recognising that the
domain cares about *what happens* (an exception is notified) not *how it
happens* (via SMTP, via webhook, via push notification). The interface
in Domain expresses the *what*. The implementation in Infrastructure
expresses the *how*.

```csharp
// Domain — the what
public interface IExceptionNotifier
{
    Task NotifyAsync(ExceptionCase exceptionCase, CancellationToken ct);
}

// Infrastructure — the how (one of many possible implementations)
public sealed class EmailExceptionNotifier : IExceptionNotifier
{
    // knows about SMTP, templates, retry — Domain never sees this
}

public sealed class WebhookExceptionNotifier : IExceptionNotifier
{
    // knows about HTTP, signatures, retry — Domain never sees this
}
```

### IDatasetReader — the file parsing example

File parsing is the clearest test of this principle. Does the domain
know what a file is? No. But the domain does need to receive records
from an external source. That need is expressed as a domain interface:

```csharp
// Domain
public interface IDatasetReader
{
    IAsyncEnumerable<ReconRecord> ReadAsync(
        Stream source,
        DatasetDefinition definition,
        CancellationToken ct);
}

// Infrastructure — three implementations, all invisible to Domain
public sealed class Mt940DatasetReader : IDatasetReader { ... }
public sealed class CsvDatasetReader   : IDatasetReader { ... }
public sealed class CamtDatasetReader  : IDatasetReader { ... }
```

Application uses `IDatasetReader` without knowing whether the source
is an MT940 file, a CSV, a database query result streamed as a `Stream`,
or a test fixture. Infrastructure provides the implementation. The domain
interface is the contract between them.

---

## Pragmatic Decisions

### No Repository Pattern (EF Core direct)

EF Core's `DbContext` with `DbSet<T>` is itself a unit of work and a
repository. Adding a custom repository interface on top of EF Core:
- Does not improve testability (EF Core has in-memory providers)
- Hides EF Core's powerful query capabilities behind a lowest-common-
  denominator interface
- Creates a leaky abstraction — LINQ queries inevitably encode SQL semantics

**Decision**: EF Core's `DbContext` is injected directly in Infrastructure.
Domain defines `ITransactionRepository` as a domain interface. Infrastructure
implements it using EF Core. Application calls the domain interface. The
repository pattern exists at the domain interface level — we do not add
another abstraction layer on top of EF Core's own abstractions.

This is honest: we choose EF Core and accept that coupling in Infrastructure.
The domain interface protects Application from knowing EF Core exists.

### No MediatR / CQRS (yet)

Use cases are called directly from Presentation. MediatR will be introduced
when the platform requires multiple consumers of the same use case, or
cross-cutting concerns (validation, retry, logging) without polluting use
case logic. Not before that complexity exists.

---

## The Columnar Engine — A Separate Future Project

The columnar execution engine (`ColumnarTransactionBatch`, `ColumnarIndexBuilder`,
histogram sort, execution planner) currently lives in `CedarRecon.Application`.
Long term it becomes `CedarRecon.ColumnarEngine` — a separate project.

This is a product decision, not a refactoring decision. The columnar engine
is general-purpose infrastructure for processing tabular financial data. It
will be used by the matching engine, classification engine, quality gates,
and future operators. It is to CedarRecon what a storage engine is to a
database — a foundational primitive that every component uses.

```
src/
├── CedarRecon.Domain/
├── CedarRecon.Application/
├── CedarRecon.Infrastructure/
├── CedarRecon.Api/
└── CedarRecon.ColumnarEngine/     ← future
      ColumnarTransactionBatch
      ColumnarIndexBuilder
      ExecutionPlanner
      No dependency on Domain — operates on int[], long[], byte[]
      Referenced by Application as a computation primitive
```

The columnar engine has **no dependency on the domain model**. It operates
on typed arrays, not on `Transaction` or `ReconRecord`. Application is the
translation boundary — encoding domain objects into columnar batches before
processing, decoding results back into domain objects after.

---

## Consequences

### What this enables

- Full unit test coverage without infrastructure — all engine tests run
  in milliseconds with no external dependencies
- Infrastructure implementations are swappable — switching from email to
  webhook notification requires a new `IExceptionNotifier` implementation
  only, no Application changes
- Domain vocabulary is consistent — interfaces use domain language,
  not infrastructure language
- The dependency rule has no exceptions — no special cases to remember,
  no "Infrastructure references Application for these three things"

### What this constrains

- Every infrastructure concern must be expressed as a domain interface
  before it can be used in Application — no shortcuts
- EF Core is a committed Infrastructure dependency — switching requires
  Infrastructure changes only (Application and Domain are unaffected)
- No MediatR means no built-in pipeline behaviours — cross-cutting
  concerns are handled explicitly until CQRS is introduced

---

## References

- `src/CedarRecon.Domain/` — domain layer, no external references
- `src/CedarRecon.Application/` — use cases, engine, references Domain only
- `src/CedarRecon.Infrastructure/` — references Domain only
- `src/CedarRecon.Api/` — presentation + composition root
- ADR-02: Hash-based matching engine
- ADR-03: Strategy pipeline and MatchContext
- ADR-04: ReconciliationOptions
- ADR-05: Columnar classification engine
