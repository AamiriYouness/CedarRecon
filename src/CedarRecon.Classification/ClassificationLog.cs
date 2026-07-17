using Microsoft.Extensions.Logging;

namespace CedarRecon.Classification;

/// <summary>
/// Compiled log delegates for the classification subsystem.
/// EventId range: 1000–1099.
///
/// All delegates are source-generated at build time via [LoggerMessage].
/// Zero allocation when the log level is disabled — no string interpolation,
/// no boxing, no params object[] array. Containing classes that use these
/// delegates must be marked partial.
///
/// Do not add business logic here. Log classes contain only delegates.
/// </summary>
internal static partial class ClassificationLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Columnar classification completed. " +
                  "Rows: {RowCount}, " +
                  "DupSrc: {DupSrc}, DupTgt: {DupTgt}, " +
                  "Split: {Split}, Consol: {Consol}, " +
                  "AmtMismatch: {AmtMismatch}, DateMismatch: {DateMismatch}, " +
                  "MissingInTgt: {MissingInTgt}, MissingInSrc: {MissingInSrc}")]
    internal static partial void ColumnarCompleted(
        ILogger logger,
        int rowCount,
        int dupSrc,
        int dupTgt,
        int split,
        int consol,
        int amtMismatch,
        int dateMismatch,
        int missingInTgt,
        int missingInSrc);
}
