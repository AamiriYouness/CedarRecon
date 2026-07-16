using CedarRecon.Core.Enums;
using CedarRecon.Core.ValueObjects;

namespace CedarRecon.Core.Entities;

/// <summary>
/// A confirmed matched pair between source and target transactions.
/// </summary>
public sealed record MatchedPair(
    Transaction Source,
    Transaction Target,
    ConfidenceScore Score,
    MatchStrategy Strategy);
