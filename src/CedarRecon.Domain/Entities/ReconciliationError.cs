using CedarRecon.Domain.Enums;

namespace CedarRecon.Domain.Entities;

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
