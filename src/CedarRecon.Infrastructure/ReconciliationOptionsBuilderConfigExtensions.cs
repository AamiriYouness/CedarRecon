using CedarRecon.Core.Options;
using Microsoft.Extensions.Configuration;

namespace CedarRecon.Infrastructure;

/// <summary>
/// Config-binding entry point for <see cref="ReconciliationOptionsBuilder"/>.
///
/// Split out of the builder itself during the v0.9.0 restructure (ADR-09):
/// Microsoft.Extensions.Configuration is a composition-root concern, and
/// Core does not take on that dependency just to support this one binding
/// path. The rest of the builder (Create/From/fluent setters/Build/
/// Validate/ToleranceBuilder) lives in CedarRecon.Core with no package
/// dependency beyond Logging.Abstractions (via NormalizationEngine).
///
/// Usage in DI (Application/DependencyInjection.cs):
///   ReconciliationOptionsBuilderConfigExtensions.FromConfig(section).Build()
/// </summary>
public static class ReconciliationOptionsBuilderConfigExtensions
{
    /// <summary>
    /// Bind from an IConfiguration section.
    /// Missing keys fall back to production defaults — no config required.
    /// </summary>
    public static ReconciliationOptionsBuilder FromConfig(
        IConfigurationSection section)
    {
        var tol = section.GetSection("Tolerance");

        return ReconciliationOptionsBuilder.Create()
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
}