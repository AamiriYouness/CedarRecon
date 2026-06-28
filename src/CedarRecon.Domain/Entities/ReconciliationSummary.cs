namespace CedarRecon.Domain.Entities;

/// <summary>
/// Summary statistics for a completed reconciliation run.
/// </summary>
public sealed record ReconciliationSummary
{
    public required int TotalSourceTransactions { get; init; }
    public required int TotalTargetTransactions { get; init; }
    public required int MatchedCount { get; init; }
    public required int UnmatchedSourceCount { get; init; }
    public required int UnmatchedTargetCount { get; init; }
    public required int SkippedRows { get; init; }
    public required int ErrorCount { get; init; }
    public required int DeadLetteredFiles { get; init; }

    public required TimeSpan TotalDuration { get; init; }
    public required TimeSpan MatchingDuration { get; init; }
    public required TimeSpan NormalizationDuration { get; init; }

    public decimal MatchRate =>
        TotalSourceTransactions == 0
            ? 0m
            : Math.Round((decimal)MatchedCount / TotalSourceTransactions * 100, 2, MidpointRounding.AwayFromZero);

    public int ExactMatchCount { get; init; }
    public int FuzzyMatchCount { get; init; }
    public int AggregateMatchCount { get; init; }
}
