using CedarRecon.Domain;
using CedarRecon.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;

namespace CedarRecon.Application.Options;

/// <summary>
/// Fluent builder for <see cref="ReconciliationOptions"/>.
///
/// Three construction paths:
///
///   1. From config (DI):
///      ReconciliationOptionsBuilder.FromConfig(section).Build()
///
///   2. Fully fluent (code / tests):
///      ReconciliationOptionsBuilder.Create()
///          .WithExactMatchConfidence(1.0m)
///          .WithFuzzyConfidence(min: 0.70m, max: 0.95m)
///          .WithTolerance(t => t.Absolute(5m).Percentage(0.5m).DateWindow(3))
///          .Build()
///
///   3. From an existing instance (copy + override):
///      ReconciliationOptionsBuilder.From(existing)
///          .WithTolerance(t => t.DateWindow(7))
///          .Build()
///
/// Validation is applied in Build() — throws ArgumentException for invalid ranges.
/// </summary>
public sealed class ReconciliationOptionsBuilder
{
    // Confidence scores
    private decimal _exactMatchConfidence = 1.00m;
    private decimal _fuzzyMatchMaxConfidence = 0.95m;
    private decimal _fuzzyMatchMinConfidence = 0.70m;
    private decimal _partialMatchMinConfidence = 0.60m;

    // Tolerance
    private decimal _absoluteTolerance = 5.00m;
    private decimal _percentageTolerance = 0.50m;
    private int _dateWindowDays = 3;

    // ── Factory methods ───────────────────────────────────────────────────────

    /// <summary>Start with production defaults.</summary>
    public static ReconciliationOptionsBuilder Create() => new();

    /// <summary>
    /// Copy all values from an existing options instance.
    /// Override only what needs to change.
    /// </summary>
    public static ReconciliationOptionsBuilder From(ReconciliationOptions existing) =>
        new ReconciliationOptionsBuilder()
            .WithExactMatchConfidence(existing.ExactMatchConfidence)
            .WithFuzzyConfidence(existing.FuzzyMatchMinConfidence, existing.FuzzyMatchMaxConfidence)
            .WithPartialMatchMinConfidence(existing.PartialMatchMinConfidence)
            .WithTolerance(t => t
                .Absolute(existing.DefaultToleranceRule.AbsoluteTolerance)
                .Percentage(existing.DefaultToleranceRule.PercentageTolerance)
                .DateWindow(existing.DefaultToleranceRule.DateWindowDays));

    /// <summary>
    /// Bind from an IConfiguration section.
    /// Missing keys fall back to production defaults — no config required.
    /// </summary>
    public static ReconciliationOptionsBuilder FromConfig(
        IConfigurationSection section)
    {
        var tol = section.GetSection("Tolerance");

        return new ReconciliationOptionsBuilder()
            .WithExactMatchConfidence(
                section.GetValue("ExactMatchConfidence", 1.00m))
            .WithFuzzyConfidence(
                min: section.GetValue("FuzzyMatchMinConfidence", 0.70m),
                max: section.GetValue("FuzzyMatchMaxConfidence", 0.95m))
            .WithPartialMatchMinConfidence(
                section.GetValue("PartialMatchMinConfidence", 0.60m))
            .WithTolerance(t => t
                .Absolute(tol.GetValue("AbsoluteTolerance", 5.00m))
                .Percentage(tol.GetValue("PercentageTolerance", 0.50m))
                .DateWindow(tol.GetValue("DateWindowDays", 3)));
    }

    // ── Fluent setters ────────────────────────────────────────────────────────

    public ReconciliationOptionsBuilder WithExactMatchConfidence(decimal value)
    {
        _exactMatchConfidence = value;
        return this;
    }

    public ReconciliationOptionsBuilder WithFuzzyConfidence(decimal min, decimal max)
    {
        _fuzzyMatchMinConfidence = min;
        _fuzzyMatchMaxConfidence = max;
        return this;
    }

    public ReconciliationOptionsBuilder WithPartialMatchMinConfidence(decimal value)
    {
        _partialMatchMinConfidence = value;
        return this;
    }

    /// <summary>
    /// Configure tolerance via a nested fluent builder.
    /// Example: .WithTolerance(t => t.Absolute(5m).Percentage(0.5m).DateWindow(3))
    /// </summary>
    public ReconciliationOptionsBuilder WithTolerance(Action<ToleranceBuilder> configure)
    {
        var tb = new ToleranceBuilder();
        configure(tb);
        _absoluteTolerance = tb.AbsoluteValue;
        _percentageTolerance = tb.PercentageValue;
        _dateWindowDays = tb.DateWindowValue;
        return this;
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates all values and constructs a <see cref="ReconciliationOptions"/> instance.
    /// Throws <see cref="ArgumentException"/> if any value is out of range.
    /// </summary>
    public ReconciliationOptions Build()
    {
        Validate();

        return new ReconciliationOptions
        {
            ExactMatchConfidence = _exactMatchConfidence,
            FuzzyMatchMaxConfidence = _fuzzyMatchMaxConfidence,
            FuzzyMatchMinConfidence = _fuzzyMatchMinConfidence,
            PartialMatchMinConfidence = _partialMatchMinConfidence,
            DefaultToleranceRule = new ToleranceRule(
                absoluteTolerance: _absoluteTolerance,
                percentageTolerance: _percentageTolerance,
                dateWindowDays: _dateWindowDays),
        };
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private void Validate()
    {
        if (_exactMatchConfidence is < 0m or > 1m)
            throw new ArgumentException(
                $"ExactMatchConfidence must be in [0, 1]. Got: {_exactMatchConfidence}");

        if (_fuzzyMatchMinConfidence < 0m || _fuzzyMatchMaxConfidence > 1m)
            throw new ArgumentException(
                $"Fuzzy confidence bounds must be in [0, 1]. Got min={_fuzzyMatchMinConfidence}, max={_fuzzyMatchMaxConfidence}");

        if (_fuzzyMatchMinConfidence >= _fuzzyMatchMaxConfidence)
            throw new ArgumentException(
                $"FuzzyMatchMinConfidence ({_fuzzyMatchMinConfidence}) must be < FuzzyMatchMaxConfidence ({_fuzzyMatchMaxConfidence})");

        if (_partialMatchMinConfidence is < 0m or > 1m)
            throw new ArgumentException(
                $"AggregateMatchMinConfidence must be in [0, 1]. Got: {_partialMatchMinConfidence}");

        // ToleranceRule constructor validates its own arguments
        // (negative tolerances, percentage > 100, negative days)
        // so no need to duplicate those checks here
    }

    // ── Nested tolerance builder ───────────────────────────────────────────────

    /// <summary>
    /// Fluent sub-builder for <see cref="ToleranceRule"/>.
    /// Accessed only through <see cref="ReconciliationOptionsBuilder.WithTolerance"/>.
    /// </summary>
    public sealed class ToleranceBuilder
    {
        internal decimal AbsoluteValue { get; private set; } = 5.00m;
        internal decimal PercentageValue { get; private set; } = 0.50m;
        internal int DateWindowValue { get; private set; } = 3;

        /// <summary>Maximum absolute difference in currency units. e.g. 5.00 = ±5.</summary>
        public ToleranceBuilder Absolute(decimal value) { AbsoluteValue = value; return this; }

        /// <summary>Maximum percentage difference. e.g. 0.5 = ±0.5%.</summary>
        public ToleranceBuilder Percentage(decimal value) { PercentageValue = value; return this; }

        /// <summary>Maximum date difference in calendar days. e.g. 3 = ±3 days.</summary>
        public ToleranceBuilder DateWindow(int days) { DateWindowValue = days; return this; }

        /// <summary>Zero tolerance — exact amounts and dates only.</summary>
        public ToleranceBuilder Strict()
        {
            AbsoluteValue = 0m;
            PercentageValue = 0m;
            DateWindowValue = 0;
            return this;
        }
    }
}