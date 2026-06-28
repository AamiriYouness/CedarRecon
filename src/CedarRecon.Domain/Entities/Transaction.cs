using CedarRecon.Domain.ValueObjects;

namespace CedarRecon.Domain.Entities;

/// <summary>
/// Validated and normalized transaction. All value objects have been constructed,
/// so any instance of this type is guaranteed valid by construction.
/// </summary>
public sealed record Transaction
{
    public required TransactionId Id { get; init; }
    public required TransactionReference NormalizedReference { get; init; }
    public required Money Amount { get; init; }
    public required DateTimeOffset ValueDate { get; init; }
    public required string Description { get; init; }
    public string? Iban { get; init; }
    public string? CounterpartyName { get; init; }
    public DateTimeOffset? BookingDate { get; init; }
    public string SourceFileName { get; init; } = string.Empty;
    public int SourceRowNumber { get; init; }

    /// <summary>
    /// Composite match key used as the hash index key in HashMatchingEngine.
    /// Combines reference + currency. Amount/date are checked separately for tolerance.
    /// </summary>
    public string IndexKey => $"{NormalizedReference.Value}|{Amount.Currency}";
}
