namespace CedarRecon.Domain.ValueObjects;

/// <summary>
/// Immutable monetary amount with ISO 4217 currency code.
/// Amounts are always stored at 2dp (AwayFromZero rounding).
/// Arithmetic preserves currency and re-applies rounding.
/// </summary>
public readonly record struct Money
{
    public decimal Amount { get; }

    /// <summary>ISO 4217 uppercase currency code e.g. "EUR", "USD", "MAD".</summary>
    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Of(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency code cannot be empty.", nameof(currency));

        var normalizedCurrency = currency.Trim().ToUpperInvariant();

        if (normalizedCurrency.Length != 3)
            throw new ArgumentException(
                $"Currency code must be 3 characters (ISO 4217). Got: '{currency}'",
                nameof(currency));

        // Canonical rounding — AwayFromZero at 2dp
        var roundedAmount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);

        return new Money(roundedAmount, normalizedCurrency);
    }

    /// <summary>
    /// Adds two Money values. Currencies must match.
    /// </summary>
    public Money Add(Money other)
    {
        AssertSameCurrency(other);
        return Of(Amount + other.Amount, Currency);
    }

    /// <summary>
    /// Subtracts other from this. Currencies must match.
    /// </summary>
    public Money Subtract(Money other)
    {
        AssertSameCurrency(other);
        return Of(Amount - other.Amount, Currency);
    }

    /// <summary>
    /// Checks if amounts are within an absolute tolerance (same currency required).
    /// </summary>
    public bool IsWithinAbsoluteTolerance(Money other, decimal absoluteTolerance)
    {
        AssertSameCurrency(other);
        return Math.Abs(Amount - other.Amount) <= absoluteTolerance;
    }

    /// <summary>
    /// Checks if amounts are within a percentage tolerance (same currency required).
    /// </summary>
    public bool IsWithinPercentageTolerance(Money other, decimal percentageTolerance)
    {
        AssertSameCurrency(other);
        if (Amount == 0m && other.Amount == 0m) return true;
        if (Amount == 0m) return false;

        var diff = Math.Abs((Amount - other.Amount) / Amount) * 100m;
        return diff <= percentageTolerance;
    }

    private void AssertSameCurrency(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Cannot operate on different currencies: {Currency} vs {other.Currency}");
    }

    public override string ToString() => $"{Amount:F2} {Currency}";
}