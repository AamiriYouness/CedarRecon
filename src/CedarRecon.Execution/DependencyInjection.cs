using CedarRecon.Execution.Strategies;
using Microsoft.Extensions.DependencyInjection;

namespace CedarRecon.Execution;

/// <summary>
/// Registers CedarRecon.Execution services: the matching engine and its
/// strategy pipeline. Split out of the former Application.DependencyInjection
/// as part of the v0.9.0 monorepo restructure (ADR-09).
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCedarReconExecution(
        this IServiceCollection services,
        ExecutionOptions options)
    {
        services.AddSingleton(_ =>
            BuildPipeline(options));

        services.AddSingleton<IMatchingEngine, HashMatchingEngine>();

        return services;
    }

    private static IReadOnlyList<IMatchStrategy> BuildPipeline(ExecutionOptions options)
    {
        var resolver = options.ModuloResolver switch
        {
            "ScaledLong" => ModuloResolver.ScaledLong,
            "UnsafeMantissa" => ModuloResolver.UnsafeMantissa,
            _ => ModuloResolver.Standard
        };

        var strategies = new List<IMatchStrategy>();

        foreach (var name in options.Strategies)
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

/// <summary>Config binding for Execution's registration — moved out of the
/// former internal CedarReconOptions, which mixed Execution and
/// Application-wide config in one type.</summary>
public sealed class ExecutionOptions
{
    public string ModuloResolver { get; set; } = "Decimal";
    public int ExpectedTargetCount { get; set; } = 65_536;
    public string[] Strategies { get; set; } = ["Exact", "Fuzzy", "Partial"];
}