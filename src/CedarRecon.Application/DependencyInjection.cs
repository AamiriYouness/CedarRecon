using CedarRecon.Execution;
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
        var executionOptions = section.Get<ExecutionOptions>() ?? new ExecutionOptions();

        var reconciliationOptions = ReconciliationOptionsBuilder
            .FromConfig(section)
            .Build();

        services.AddSingleton(reconciliationOptions);
        services.AddCedarReconExecution(executionOptions);

        return services;
    }

 
}
