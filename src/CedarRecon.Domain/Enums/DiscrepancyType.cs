namespace CedarRecon.Domain.Enums;

/// <summary>
/// All 8 discrepancy types that the exception classifier can identify.
/// </summary>
public enum DiscrepancyType
{
    MissingInSource,
    MissingInTarget,
    AmountMismatch,
    DateMismatch,
    DuplicateInSource,
    DuplicateInTarget,
    SplitPayment,       // 1 source → many targets
    ConsolidatedPayment // many sources → 1 target
}
