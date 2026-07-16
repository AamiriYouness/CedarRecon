using CedarRecon.Core.Enums;

namespace CedarRecon.Core.Entities;

/// <summary>
/// An error encountered during processing. IsFatal = true means
/// the pipeline should stop cleanly. IsFatal = false = skip and continue.
/// </summary>
public sealed record ReconciliationError(
    ReconciliationErrorType Type,
    string Message,
    int? RowNumber = null,
    bool IsFatal = false,
    string? FileName = null,
    Exception? Exception = null);
