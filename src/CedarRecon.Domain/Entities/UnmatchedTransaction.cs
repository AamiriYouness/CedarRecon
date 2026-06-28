using CedarRecon.Domain.Enums;

namespace CedarRecon.Domain.Entities;

/// <summary>
/// A transaction that could not be matched.
/// </summary>
public sealed record UnmatchedTransaction(
    Transaction Transaction,
    DiscrepancyType Reason);
