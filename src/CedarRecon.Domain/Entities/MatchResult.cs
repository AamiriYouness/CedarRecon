using CedarRecon.Domain.Enums;

namespace CedarRecon.Domain.Entities;

/// <summary>
/// Discriminated union result from the matching engine per transaction.
/// </summary>
public abstract record MatchResult(MatchResultKind Kind);

public sealed record MatchedResult(MatchedPair Pair) : MatchResult(MatchResultKind.Matched);
public sealed record UnmatchedResult(UnmatchedTransaction Unmatched) : MatchResult(MatchResultKind.Unmatched);
public sealed record DuplicateResult(Transaction Transaction, DiscrepancyType DuplicateKind) : MatchResult(MatchResultKind.Duplicate);
