using CedarRecon.Application.Matching;
using CedarRecon.Application.Matching.Strategies;
using CedarRecon.Application.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CedarRecon.Application;

/// <summary>
/// Registers all CedarRecon Application-layer services.
///
/// Usage in Program.cs:
///   builder.Services.AddCedarReconApplication(builder.Configuration);
///
/// appsettings.json (all optional — defaults shown):
///   {
///     "CedarRecon": {
///       "ModuloResolver":            "Decimal",
///       "ExpectedTargetCount":       65536,
///       "Strategies":                ["Exact", "Fuzzy", "Partial"],
///       "ExactMatchConfidence":      1.00,
///       "FuzzyMatchMaxConfidence":   0.95,
///       "FuzzyMatchMinConfidence":   0.70,
///       "AggregateMatchMinConfidence": 0.60,
///       "Tolerance": {
///         "AbsoluteTolerance":   5.00,
///         "PercentageTolerance": 0.50,
///         "DateWindowDays":      3
///       }
///     }
///   }
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCedarReconApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("CedarRecon");
        var config = section.Get<CedarReconOptions>() ?? new CedarReconOptions();

        // ── ReconciliationOptions — built via fluent builder ──────────────────
        // Builder validates all values at startup — misconfigured deployments
        // fail fast with a clear ArgumentException rather than silently misbehaving.
        var reconciliationOptions = ReconciliationOptionsBuilder
            .FromConfig(section)
            .Build();

        services.AddSingleton(reconciliationOptions);

        // ── Strategy pipeline ─────────────────────────────────────────────────
        services.AddSingleton<IReadOnlyList<IMatchStrategy>>(_ =>
            BuildPipeline(config));

        // ── Matching engine ───────────────────────────────────────────────────
        services.AddSingleton<IMatchingEngine, HashMatchingEngine>();

        return services;
    }

    // ── Pipeline builder ──────────────────────────────────────────────────────

    private static IReadOnlyList<IMatchStrategy> BuildPipeline(CedarReconOptions config)
    {
        var resolver = config.ModuloResolver switch
        {
            "ScaledLong" => ModuloResolver.ScaledLong,
            "UnsafeMantissa" => ModuloResolver.UnsafeMantissa,
            _ => ModuloResolver.Decimal
        };

        var strategies = new List<IMatchStrategy>();

        foreach (var name in config.Strategies)
        {
            IMatchStrategy? strategy = name switch
            {
                "Exact" => new ExactMatchStrategy(),
                "Fuzzy" => new FuzzyMatchStrategy(),
                "Partial" => new PartialMatchStrategy(resolver),
                _ => null
            };

            if (strategy is not null)
                strategies.Add(strategy);
        }

        return strategies.Count > 0
            ? strategies.AsReadOnly()
            : MatchStrategyFactory.CreateWithModuloResolver(resolver);
    }
}

// ── Config binding ────────────────────────────────────────────────────────────

internal sealed class CedarReconOptions
{
    public string ModuloResolver { get; set; } = "Decimal";
    public int ExpectedTargetCount { get; set; } = 65_536;
    public string[] Strategies { get; set; } = ["Exact", "Fuzzy", "Partial"];
}
