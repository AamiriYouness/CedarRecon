namespace CedarRecon.Domain.Errors;

/// <summary>
/// Domain errors are value objects, not exceptions.
/// They flow through the pipeline as data, not control flow.
/// </summary>
public abstract record DomainError(string Code, string Message);

public sealed record InvalidTransactionError(string Message, int? RowNumber = null, string? FieldName = null)
    : DomainError("INVALID_TRANSACTION", Message);

public sealed record IngestionFailedError(string Message, string FileName, Exception? Cause = null)
    : DomainError("INGESTION_FAILED", Message);

public sealed record PipelineExecutionError(string Message, string Stage, Exception? Cause = null)
    : DomainError("PIPELINE_EXECUTION_FAILED", Message);

public sealed record UnsupportedFormatError(string Message, string? DetectedFormat = null)
    : DomainError("UNSUPPORTED_FORMAT", Message);

public sealed record ToleranceRuleViolationError(string Message, decimal Expected, decimal Actual)
    : DomainError("TOLERANCE_RULE_VIOLATION", Message);