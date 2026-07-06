# ADR-04 — ReconciliationOptions as Single Configuration Contract

**Status**: Accepted  
**Date**: 2026  
**Deciders**: Youness (Senior .NET Engineer, CedarRecon)

---

## Context

A reconciliation engine has many tunables: confidence thresholds, tolerance
windows, parallelism degrees, batch sizes, error handling modes. These values
vary by client, by domain, by dataset, and by regulatory requirement.

A French bank reconciling nostro accounts has different tolerance requirements
than a reinsurer reconciling bordereaux. A treasury team may need a 5-day
date window; a payment processor may need zero tolerance on amounts.

If any of these values are hardcoded anywhere in the engine, a client with
different requirements needs a code change and a redeployment. If each
component defines its own configuration independently, there is no single
place to understand or audit the engine's behaviour for a given run.

The question is: where do tunables live, and how do they flow through the
engine?

---

## Decision

**All tunables live in `ReconciliationOptions`. No threshold, tolerance,
or configuration value is hardcoded anywhere in the engine. Every component
that needs a tunable receives it through `ReconciliationOptions` passed at
call time, not through constructor injection or static fields.**

```csharp
public class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";

    // Pipeline parallelism
    public int NormalizeParallelism    { get; init; } = Environment.ProcessorCount;
    public int EnrichParallelism       { get; init; } = Environment.ProcessorCount;
    public int ClassifyParallelism     { get; init; } = Environment.ProcessorCount;
    public int BlockBoundedCapacity    { get; init; } = 10_000;

    // Matching
    public ToleranceRule DefaultToleranceRule      { get; init; } = ToleranceRule.Standard;
    public decimal MinimumConfidenceThreshold      { get; init; } = 0.50m;

    // Confidence bands
    public decimal ExactMatchConfidence            { get; init; } = 1.0m;
    public decimal FuzzyMatchMaxConfidence         { get; init; } = 0.99m;
    public decimal FuzzyMatchMinConfidence         { get; init; } = 0.70m;
    public decimal PartialMatchMaxConfidence       { get; init; } = 0.69m;
    public decimal PartialMatchMinConfidence       { get; init; } = 0.50m;

    // Ingestion
    public long MaxFileSizeBytes                   { get; init; } = 500L * 1024 * 1024;
    public int  IngestionBatchSize                 { get; init; } = 1_000;

    // Error handling
    public int  MaxConsecutiveErrors               { get; init; } = 100;
    public bool AbortOnFatalError                  { get; init; } = false;

    // Date handling
    public string DefaultTimezone                  { get; init; } = "UTC";
}
```

### How options flow through the engine

`ReconciliationOptions` is passed at call time to every component that needs
it. It is not stored as a field on strategies or the engine:

```csharp
// Engine passes options to each strategy call
MatchedPair? TryMatch(
    Transaction source,
    IReadOnlyList<Transaction> candidates,
    ReconciliationOptions options,    // ← passed per call, not stored
    MatchContext context);
```

This means the same engine instance can process different datasets with
different options in sequence — a banking dataset with tight tolerances
followed by an insurance dataset with wider tolerances — without
reconstructing any objects.

### Binding from configuration

```csharp
// In DI registration
services.Configure<ReconciliationOptions>(
    configuration.GetSection(ReconciliationOptions.SectionName));
```

```json
// appsettings.json
{
  "Reconciliation": {
    "FuzzyMatchMinConfidence": 0.70,
    "DefaultToleranceRule": {
      "AbsoluteTolerance": 0.01,
      "DateWindowDays": 2
    }
  }
}
```

Defaults are defined on the class. An `appsettings.json` entry overrides
the default. An environment variable overrides `appsettings.json`. The
standard .NET configuration hierarchy applies — no custom override mechanism
is needed.

### ToleranceRule as a value object

```csharp
public readonly record struct ToleranceRule
{
    public decimal AbsoluteTolerance  { get; init; }
    public decimal PercentageTolerance{ get; init; }
    public int     DateWindowDays     { get; init; }

    public static readonly ToleranceRule Standard = new()
    {
        AbsoluteTolerance   = 0.01m,
        PercentageTolerance = 0.001m,
        DateWindowDays      = 2
    };

    public static readonly ToleranceRule Zero = new();
}
```

`ToleranceRule` is a value object on `ReconciliationOptions`. It is not a
separate configuration class with its own DI registration. The entire
configuration for a reconciliation run is one object.

### ConfidenceScore bands are non-overlapping by convention

```
ExactMatch:   1.00          (1.00)
FuzzyMatch:   0.70 – 0.99  (FuzzyMatchMinConfidence – FuzzyMatchMaxConfidence)
PartialMatch: 0.50 – 0.69  (PartialMatchMinConfidence – PartialMatchMaxConfidence)
```

These bands do not overlap. A fuzzy match score can never exceed an exact
match score. The `ConfidenceScore` value object enforces [0.0, 1.0] at
construction — it cannot carry an invalid score.

---

## Consequences

### No hardcoded values anywhere in the engine

A global search for magic numbers (0.70, 0.99, 0.50, 2, 10_000) in strategy
or engine code is a bug. All of those values belong in `ReconciliationOptions`
with a named property. Code review enforces this.

### Options are auditable per run

For compliance and audit, the `ReconciliationOptions` used for a given run
can be serialised and stored alongside the results. A regulator asking
"what tolerance was applied to this dataset on this date?" has a precise,
reproducible answer.

### Defaults are production-safe

Every property has a sensible default. An engine with no `appsettings.json`
entry and no environment variables runs with production-safe defaults —
not with zeroes or nulls that would produce incorrect results silently.

### Same instance, different options per run

Because options are passed at call time rather than stored on the engine,
the same `HashMatchingEngine` instance can be used for multiple sequential
reconciliation runs with different options. This is relevant for the future
scheduling layer (v5.2.0) where a single worker process runs many
reconciliation jobs without restarting between them.

---

## Rejected Alternatives

### Per-component configuration

Each strategy or engine component has its own configuration class:

```csharp
// REJECTED
public class FuzzyMatchOptions { public decimal MinConfidence { get; init; } }
public class ExactMatchOptions { public decimal Confidence { get; init; } }
```

Produces multiple configuration classes with no guaranteed consistency.
`FuzzyMatchOptions.MinConfidence` and `ExactMatchOptions.Confidence` can
be set to overlapping values with no validation. A single
`ReconciliationOptions` with named bands is clearer and enforces structure.

### Constructor-injected configuration

Options injected into strategy constructors via `IOptions<T>`:

```csharp
// REJECTED
public FuzzyMatchStrategy(IOptions<FuzzyMatchOptions> options) { ... }
```

Strategies constructed once cannot adapt to per-run options without
reconstruction. This breaks the pattern of using a single engine instance
for multiple runs with different options. Passing options at call time
is more flexible at no additional cost.

### Static configuration class

```csharp
// REJECTED
public static class MatchingConfig
{
    public static decimal FuzzyMinConfidence = 0.70m;
}
```

Untestable, not thread-safe for concurrent runs with different options,
not bindable from `appsettings.json`. Rejected unconditionally.

---

## References

- `src/CedarRecon.Domain/ReconciliationOptions.cs`
- `src/CedarRecon.Domain/ValueObjects/ToleranceRule.cs`
- `src/CedarRecon.Domain/ValueObjects/ConfidenceScore.cs`
- `src/CedarRecon.Application/Matching/Strategies/FuzzyMatchStrategy.cs`
