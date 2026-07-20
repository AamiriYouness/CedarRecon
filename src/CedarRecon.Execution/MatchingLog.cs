using Microsoft.Extensions.Logging;

namespace CedarRecon.Execution;

/// <summary>
/// Compiled log delegates for the matching subsystem.
/// EventId range: 1200–1299.
/// </summary>
internal static partial class MatchingLog
{
    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Information,
        Message = "Target index built: {UniqueKeys} unique keys from " +
                  "{Total} transactions using {StrategyCount} strategies")]
    internal static partial void IndexBuilt(
        ILogger logger,
        int uniqueKeys,
        int total,
        int strategyCount);
}
