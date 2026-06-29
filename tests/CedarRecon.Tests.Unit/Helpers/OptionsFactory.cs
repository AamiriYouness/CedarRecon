using CedarRecon.Domain;
using CedarRecon.Domain.ValueObjects;

namespace CedarRecon.Tests.Unit.Helpers;

/// <summary>
/// Centralises ReconciliationOptions construction for tests.
/// ToleranceRule now uses a constructor — no object initialiser.
///
/// Never scatter new ToleranceRule(...) across test files.
/// If the constructor signature changes, fix it here once.
/// </summary>
internal static class OptionsFactory
{
    /// <summary>
    /// Realistic production-like defaults.
    /// Override only what each test cares about.
    ///
    /// Tolerances:
    ///   - ±5 absolute (e.g. bank fee rounding)
    ///   - ±0.5% percentage (e.g. FX rounding on large amounts)
    ///   - ±3 calendar days (e.g. T+2 settlement lag)
    /// </summary>
    public static ReconciliationOptions Default() => new()
    {
        ExactMatchConfidence = 1.00m,
        FuzzyMatchMaxConfidence = 0.95m,
        FuzzyMatchMinConfidence = 0.70m,
        PartialMatchMinConfidence = 0.60m,
        DefaultToleranceRule = Tolerances.Default,
    };

    /// <summary>
    /// Zero tolerance — any difference in amount or date → no fuzzy match.
    /// Use for tests that verify strict boundary behaviour.
    /// </summary>
    public static ReconciliationOptions ZeroTolerance() => new()
    {
        ExactMatchConfidence = 1.00m,
        FuzzyMatchMaxConfidence = 0.95m,
        FuzzyMatchMinConfidence = 0.70m,
        PartialMatchMinConfidence = 0.60m,
        DefaultToleranceRule = Tolerances.Zero,
    };

    /// <summary>
    /// Wide tolerance — use for tests that want fuzzy to match almost anything.
    /// Useful when testing claim/context logic without worrying about tolerance gates.
    /// </summary>
    public static ReconciliationOptions WideTolerance() => new()
    {
        ExactMatchConfidence = 1.00m,
        FuzzyMatchMaxConfidence = 0.95m,
        FuzzyMatchMinConfidence = 0.70m,
        PartialMatchMinConfidence = 0.60m,
        DefaultToleranceRule = Tolerances.Wide,
    };

    // ── ToleranceRule presets — reused independently in some tests ────────────

    public static class Tolerances
    {
        /// <summary>Realistic: ±5 absolute, ±0.5%, ±3 days.</summary>
        public static readonly ToleranceRule Default = new(
            absoluteTolerance: 5.00m,
            percentageTolerance: 0.50m,
            dateWindowDays: 3);

        /// <summary>No tolerance whatsoever.</summary>
        public static readonly ToleranceRule Zero = new(
            absoluteTolerance: 0m,
            percentageTolerance: 0m,
            dateWindowDays: 0);

        /// <summary>Very permissive — ±1000, ±10%, ±30 days.</summary>
        public static readonly ToleranceRule Wide = new(
            absoluteTolerance: 1_000m,
            percentageTolerance: 10m,
            dateWindowDays: 30);

        /// <summary>Custom — use when a test needs a specific tolerance shape.</summary>
        public static ToleranceRule Custom(
            decimal absolute = 0m,
            decimal percentage = 0m,
            int days = 0) =>
            new(absolute, percentage, days);
    }
}
