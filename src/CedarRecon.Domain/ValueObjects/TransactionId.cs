namespace CedarRecon.Domain.ValueObjects;

/// <summary>
/// Strongly-typed transaction identifier. Prevents stringly-typed Id bugs
/// across domain boundaries. Implicitly converts to/from Guid for convenience
/// at domain edges.
/// </summary>
public readonly record struct TransactionId
{
    public Guid Value { get; }

    private TransactionId(Guid value) => Value = value;

    public static TransactionId New() => new(Guid.NewGuid());

    public static TransactionId From(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("TransactionId cannot be an empty Guid.", nameof(value));
        return new(value);
    }

    public static TransactionId From(string value)
    {
        if (!Guid.TryParse(value, out var guid))
            throw new ArgumentException($"'{value}' is not a valid TransactionId.", nameof(value));
        return From(guid);
    }

    public static implicit operator Guid(TransactionId id) => id.Value;

    public override string ToString() => Value.ToString();
}
