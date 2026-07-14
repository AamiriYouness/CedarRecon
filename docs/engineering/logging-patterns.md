# Logging Patterns

## Why LoggerMessage delegates instead of LoggerExtensions

CedarRecon uses `[LoggerMessage]` source-generated delegates everywhere.
Calling `_logger.LogInformation(...)` directly is prohibited and enforced
as an error via CA1848 in `.editorconfig`.

### The problem with LoggerExtensions

```csharp
// WRONG — always evaluates arguments regardless of log level
_logger.LogInformation(
    "Classified {Total} exceptions",
    results.Count);
```

Even when `Information` logging is disabled, .NET evaluates `results.Count`,
boxes it as `object`, and allocates a `params object[]` array. On a
classification run processing N=1M transactions, this happens on every run
regardless of log configuration.

CA1873 catches the expensive argument evaluation.
CA1848 catches the extension method call itself.
Both enforced as errors.

### The solution — LoggerMessage source generators

```csharp
// CORRECT — zero cost when log level disabled
internal static partial class ClassificationLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Classification complete: {Total} exceptions")]
    internal static partial void Completed(ILogger logger, int total);
}

// Containing class must be partial
public sealed partial class ColumnarExceptionClassifier : IExceptionClassifier

// Call site — no boxing, no array, no string if disabled
ClassificationLog.Completed(_logger, total);
```

The `[LoggerMessage]` attribute generates a compiled delegate at build time.
When `Information` is disabled, the call is a single `IsEnabled` check
and returns — no allocation, no boxing, no string formatting.

### Exception logging

When logging an exception, place it as the second parameter after `ILogger`.
The framework captures the full stack trace automatically:

```csharp
[LoggerMessage(
    EventId = 1100,
    Level = LogLevel.Error,
    Message = "Normalization failed for row {RowNumber} in {FileName}")]
internal static partial void Failed(
    ILogger logger,
    Exception exception,    // ← full stack trace captured, nothing lost
    int rowNumber,
    string fileName);
```

### Log class organisation

Delegates are grouped by subsystem in `Application/Logging/`.
Each file is `internal static partial class` — no instances, no DI.

```
Application/Logging/
├── ClassificationLog.cs    EventId 1000–1099
├── NormalizationLog.cs     EventId 1100–1199
├── MatchingLog.cs          EventId 1200–1299
├── IndexingLog.cs          EventId 1300–1399 (future)
├── ExecutionLog.cs         EventId 1400–1499 (future)
├── PlannerLog.cs           EventId 1500–1599 (v1.0.0)
├── RuntimeLog.cs           EventId 1600–1699 (v1.2.0)
├── JobDefinitionLog.cs     EventId 1700–1799 (v1.0.0)
└── ReconQLLog.cs           EventId 1800–1899 (v1.0.0)
```

Reserved ranges exist for future subsystems. Adding a new subsystem
means creating a new file in the correct range — no changes to existing
files and no EventId collisions.

### When to log

```
LogLevel.Debug       → per-row details (disabled in production)
LogLevel.Information → per-run summary (counts, outcomes)
LogLevel.Warning     → unexpected but recoverable (skipped rows, lost claims)
LogLevel.Error       → failure with context (parse error, normalization failure)
LogLevel.Critical    → engine invariant violated (should never occur)
```

Never log inside scanning phases (DuplicateScan, MismatchScan, etc.).
Logging belongs in summary calls after phases complete.
A log call inside a phase loop runs N times per classification run.

### Future — Serilog enrichers (v1.2.0+)

When `CedarRecon.Runtime` exists, every log will automatically carry:

```
RunId, CorrelationId, RuleName, TenantId,
SourceSystem, TargetSystem, Version
```

Added via `ILoggerFactory` configuration in the Runtime layer.
The `[LoggerMessage]` delegates require no changes when enrichers are added —
Serilog plugs into `Microsoft.Extensions.Logging` transparently.
