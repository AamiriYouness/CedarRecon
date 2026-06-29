using CedarRecon.Application.Matching.Strategies;

namespace CedarRecon.Application.Matching;

/// <summary>
/// Factory that constructs the ordered strategy pipeline.
///
/// This is the only place that knows which strategies exist and in what order they run.
/// To add a new strategy (e.g. SemanticMatchStrategy): register it here, nowhere else.
///
/// Order matters: first strategy that returns non-null wins.
/// </summary>
public static class MatchStrategyFactory
{
    /// <summary>
    /// Default pipeline: Exact → Fuzzy → Partial.
    /// </summary>
    public static IReadOnlyList<IMatchStrategy> CreateDefault() =>
    [
        new ExactMatchStrategy(),
        new FuzzyMatchStrategy(),
        new PartialMatchStrategy(),                               // Decimal by default
    ];

    /// <summary>
    /// Override the modulo resolver — useful for benchmarking all three implementations
    /// against real transaction data without changing production code.
    /// </summary>
    public static IReadOnlyList<IMatchStrategy> CreateWithModuloResolver(
        ModuloResolver.IsEvenlyDivisible resolver) =>
    [
        new ExactMatchStrategy(),
        new FuzzyMatchStrategy(),
        new PartialMatchStrategy(resolver),
    ];

    /// <summary>
    /// Custom pipeline — caller controls order and which strategies are active.
    /// Useful for testing a single strategy in isolation or A/B config.
    /// </summary>
    public static IReadOnlyList<IMatchStrategy> Create(params IMatchStrategy[] strategies) =>
        strategies.ToList().AsReadOnly();
}
