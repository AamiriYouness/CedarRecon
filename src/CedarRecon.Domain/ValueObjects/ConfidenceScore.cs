namespace CedarRecon.Domain.ValueObjects;

/// <summary>
/// Confidence score for a match result. Always in range [0.0, 1.0].
/// Enforced at construction — no invalid scores can propagate through the pipeline.
/// </summary>
public readonly record struct ConfidenceScore : IComparable<ConfidenceScore>
{
    public decimal Value { get; }

    private ConfidenceScore(decimal value) => Value = value;

    public static readonly ConfidenceScore Zero = new(0.0m);
    public static readonly ConfidenceScore Perfect = new(1.0m);

    /// <summary>Exact match — all fields align.</summary>
    public static readonly ConfidenceScore Exact = new(1.0m);

    /// <summary>Lower bound for fuzzy matches.</summary>
    public static readonly ConfidenceScore FuzzyMin = new(0.70m);

    /// <summary>Lower bound for aggregate matches (split/consolidated).</summary>
    public static readonly ConfidenceScore AggregateMin = new(0.50m);

    public static ConfidenceScore Of(decimal value)
    {
        if (value < 0.0m || value > 1.0m)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"ConfidenceScore must be in [0.0, 1.0]. Got: {value}");

        return new(Math.Round(value, 4, MidpointRounding.AwayFromZero));
    }

    public bool IsHighConfidence => Value >= 0.90m;
    public bool IsMediumConfidence => Value >= 0.70m && Value < 0.90m;
    public bool IsLowConfidence => Value < 0.70m;

    public int CompareTo(ConfidenceScore other) => Value.CompareTo(other.Value);

    public static bool operator >(ConfidenceScore left, ConfidenceScore right) => left.Value > right.Value;
    public static bool operator <(ConfidenceScore left, ConfidenceScore right) => left.Value < right.Value;
    public static bool operator >=(ConfidenceScore left, ConfidenceScore right) => left.Value >= right.Value;
    public static bool operator <=(ConfidenceScore left, ConfidenceScore right) => left.Value <= right.Value;

    public override string ToString() => $"{Value:P0}";
}
