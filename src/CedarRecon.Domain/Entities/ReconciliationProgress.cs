using CedarRecon.Domain.Enums;

namespace CedarRecon.Domain.Entities;

/// <summary>
/// Progress snapshot emitted by the pipeline at configurable intervals.
/// </summary>
public sealed record ReconciliationProgress(
    int TotalSourceTransactions,
    int ProcessedCount,
    int MatchedCount,
    int UnmatchedCount,
    ReconciliationStatus Status,
    TimeSpan Elapsed);
