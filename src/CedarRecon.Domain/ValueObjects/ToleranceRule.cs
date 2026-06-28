namespace CedarRecon.Domain.ValueObjects;

/// <summary>
/// Configurable tolerance rules for fuzzy matching.
/// All values come from ReconciliationOptions — nothing is hardcoded.
/// Immutable value object — safe to share across threads.
/// </summary>
public sealed record ToleranceRule
{
    /// <summary>Maximum allowed absolute amount difference (e.g. 0.01 for 1 cent).</summary>
    public decimal AbsoluteTolerance { get; init; }

    /// <summary>Maximum allowed percentage amount difference (e.g. 0.5 for 0.5%).</summary>
    public decimal PercentageTolerance { get; init; }

    /// <summary>Allowed date window in days for date fuzzy matching (e.g. 2 for ±2 days).</summary>
    public int DateWindowDays { get; init; }

    public ToleranceRule(decimal absoluteTolerance, decimal percentageTolerance, int dateWindowDays)
    {
        if (absoluteTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(absoluteTolerance), "Must be ≥ 0.");
        if (percentageTolerance < 0 || percentageTolerance > 100)
            throw new ArgumentOutOfRangeException(nameof(percentageTolerance), "Must be in [0, 100].");
        if (dateWindowDays < 0)
            throw new ArgumentOutOfRangeException(nameof(dateWindowDays), "Must be ≥ 0.");

        AbsoluteTolerance = absoluteTolerance;
        PercentageTolerance = percentageTolerance;
        DateWindowDays = dateWindowDays;
    }

    /// <summary>Default strict rule: exact amounts, exact dates.</summary>
    public static readonly ToleranceRule Strict = new(0m, 0m, 0);

    /// <summary>Standard banking rule: ±1 cent, ±0.5%, ±2 days.</summary>
    public static readonly ToleranceRule Standard = new(0.01m, 0.5m, 2);

    /// <summary>Lenient rule for cross-border payments with FX rounding.</summary>
    public static readonly ToleranceRule Lenient = new(0.05m, 1.0m, 5);

    public bool IsAmountWithinTolerance(decimal sourceAmount, decimal targetAmount)
    {
        var absoluteDiff = Math.Abs(sourceAmount - targetAmount);
        if (absoluteDiff <= AbsoluteTolerance)
            return true;

        if (sourceAmount == 0m) return absoluteDiff == 0m;

        var percentageDiff = absoluteDiff / Math.Abs(sourceAmount) * 100m;
        return percentageDiff <= PercentageTolerance;
    }

    public bool IsDateWithinTolerance(DateTimeOffset sourceDate, DateTimeOffset targetDate)
    {
        var daysDiff = Math.Abs((sourceDate.UtcDateTime.Date - targetDate.UtcDateTime.Date).TotalDays);
        return daysDiff <= DateWindowDays;
    }
}