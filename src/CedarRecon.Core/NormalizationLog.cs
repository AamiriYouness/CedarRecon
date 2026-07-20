using Microsoft.Extensions.Logging;

namespace CedarRecon.Core;

/// <summary>
/// Compiled log delegates for the normalization subsystem.
/// EventId range: 1100–1199.
/// </summary>
internal static partial class NormalizationLog
{
    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Error,
        Message = "Normalization failed for row {RowNumber} in {FileName}")]
        internal static partial void Failed(
        ILogger logger,
        Exception exception,
        int rowNumber,
        string fileName);
}
