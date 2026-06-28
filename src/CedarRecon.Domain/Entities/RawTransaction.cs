using CedarRecon.Domain.Enums;

namespace CedarRecon.Domain.Entities;

/// <summary>
/// Raw transaction as ingested — unvalidated, unprocessed.
/// All fields are nullable strings at this stage.
/// </summary>
public sealed record RawTransaction
{
    public required string? Reference { get; init; }
    public required string? Amount { get; init; }
    public required string? Currency { get; init; }
    public required string? ValueDate { get; init; }
    public required string? Description { get; init; }
    public string? Iban { get; init; }
    public string? CounterpartyName { get; init; }
    public string? BookingDate { get; init; }

    /// <summary>Source row number for error reporting.</summary>
    public int RowNumber { get; init; }

    /// <summary>Name of the source file this row came from.</summary>
    public string SourceFileName { get; init; } = string.Empty;
}
