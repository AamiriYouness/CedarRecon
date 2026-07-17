using Microsoft.Extensions.Logging;

namespace CedarRecon.Reference;

internal static partial class ReferenceLog
{
    [LoggerMessage(
        EventId = 1100, // pick a range that doesn't collide with ClassificationLog's 1000-1099
        Level = LogLevel.Information,
        Message = "Dictionary classification completed. "+
                  "Rows: {RowCount}, " +
                  "DupSrc: {DupSrc}, DupTgt: {DupTgt}, " +
                  "Split: {Split}, Consol: {Consol}, " +
                  "AmtMismatch: {AmtMismatch}, DateMismatch: {DateMismatch}, " +
                  "MissingInTgt: {MissingInTgt}, MissingInSrc: {MissingInSrc}")]
    internal static partial void DictionaryCompleted(
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