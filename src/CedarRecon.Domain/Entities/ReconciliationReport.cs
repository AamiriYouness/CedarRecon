using CedarRecon.Domain.Enums;

namespace CedarRecon.Domain.Entities;

/// <summary>
/// Full reconciliation report returned to the caller.
/// </summary>
public sealed record ReconciliationReport
{
    public required Guid JobId { get; init; }
    public required ReconciliationStatus Status { get; init; }
    public required ReconciliationSummary Summary { get; init; }
    public required IReadOnlyList<MatchedPair> MatchedPairs { get; init; }
    public required IReadOnlyList<UnmatchedTransaction> UnmatchedTransactions { get; init; }
    public required IReadOnlyList<ReconciliationError> Errors { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}
