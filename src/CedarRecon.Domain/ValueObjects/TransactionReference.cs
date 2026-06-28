namespace CedarRecon.Domain.ValueObjects;

/// <summary>
/// Normalized transaction reference. Always uppercase, no spaces/dashes/slashes.
/// This is the primary matching key — normalization is enforced at construction.
/// </summary>
public sealed class TransactionReference : IEquatable<TransactionReference>
{
    public string Value { get; }

    private TransactionReference(string value) => Value = value;

    /// <summary>
    /// Creates from already-normalized value. Use NormalizationEngine to produce input.
    /// </summary>
    public static TransactionReference FromNormalized(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("TransactionReference cannot be empty.", nameof(normalized));

        // Enforce contract: must be pre-normalized
        if (normalized != normalized.Trim().ToUpperInvariant())
            throw new ArgumentException(
                $"TransactionReference must be uppercase with no leading/trailing whitespace. Got: '{normalized}'",
                nameof(normalized));

        return new(normalized);
    }

    /// <summary>
    /// Creates from raw input — strips spaces/dashes/slashes and uppercases.
    /// Equivalent to NormalizationEngine.NormalizeReference but available on the value object.
    /// </summary>
    public static TransactionReference FromRaw(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Raw reference cannot be empty.", nameof(raw));

        // Inline normalization — mirrors NormalizationEngine.NormalizeReference exactly
        var span = raw.AsSpan().Trim();
        var buffer = new char[span.Length];
        var writeIndex = 0;

        foreach (var ch in span)
        {
            if (ch is ' ' or '-' or '/')
                continue;
            buffer[writeIndex++] = char.ToUpperInvariant(ch);
        }

        var normalized = new string(buffer, 0, writeIndex);

        if (normalized.Length == 0)
            throw new ArgumentException($"Reference '{raw}' normalizes to empty string.", nameof(raw));

        return new(normalized);
    }

    public bool Equals(TransactionReference? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is TransactionReference other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
    public override string ToString() => Value;

    public static bool operator ==(TransactionReference? left, TransactionReference? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(TransactionReference? left, TransactionReference? right) =>
        !(left == right);
}
