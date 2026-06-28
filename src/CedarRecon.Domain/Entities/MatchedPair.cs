using CedarRecon.Domain.Enums;
using CedarRecon.Domain.ValueObjects;

namespace CedarRecon.Domain.Entities;

/// <summary>
/// A confirmed matched pair between source and target transactions.
/// </summary>
public sealed record MatchedPair(
    Transaction Source,
    Transaction Target,
    ConfidenceScore Score,
    MatchStrategy Strategy);
