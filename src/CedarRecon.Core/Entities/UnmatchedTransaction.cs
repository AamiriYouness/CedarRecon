using CedarRecon.Core.Enums;

namespace CedarRecon.Core.Entities;

/// <summary>
/// A transaction that could not be matched.
/// </summary>
public sealed record UnmatchedTransaction(
    Transaction Transaction,
    DiscrepancyType Reason);
