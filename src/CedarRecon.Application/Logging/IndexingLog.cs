using Microsoft.Extensions.Logging;

namespace CedarRecon.Application.Logging;

/// <summary>
/// Compiled log delegates for the indexing subsystem.
/// EventId range: 1300–1399.
///
/// Indexing is a hot path — log only at Debug level inside build phases.
/// Information level for per-run summaries only.
/// Never log inside the scatter/placement loops.
/// </summary>
internal static partial class IndexingLog
{
    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Information,
        Message = "Columnar index built. " +
                  "Source: {SourceCount} rows, Target: {TargetCount} rows, " +
                  "Distinct keys: {KeyCount}, Groups: {GroupCount}, " +
                  "Elapsed: {ElapsedMilliseconds}ms")]
    internal static partial void IndexBuilt(
        ILogger logger,
        int sourceCount,
        int targetCount,
        int keyCount,
        int groupCount,
        long elapsedMilliseconds);

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Debug,
        Message = "Histogram built. Keys: {KeyCount}, " +
                  "Max bucket size: {MaxBucketSize}, " +
                  "Avg bucket size: {AvgBucketSize:F1}")]
    internal static partial void HistogramBuilt(
        ILogger logger,
        int keyCount,
        int maxBucketSize,
        double avgBucketSize);

    [LoggerMessage(
        EventId = 1310,
        Level = LogLevel.Warning,
        Message = "Heavy key detected. KeyId: {KeyId}, " +
                  "Count: {Count}, Avg: {AvgCount:F1}. " +
                  "Consider partitioning strategy for this key.")]
    internal static partial void HeavyKeyDetected(
        ILogger logger,
        int keyId,
        int count,
        double avgCount);

    [LoggerMessage(
        EventId = 1320,
        Level = LogLevel.Debug,
        Message = "Reference interning completed. " +
                  "Distinct references: {Count}, Elapsed: {ElapsedMilliseconds}ms")]
    internal static partial void InterningCompleted(
        ILogger logger,
        int count,
        long elapsedMilliseconds);
}
