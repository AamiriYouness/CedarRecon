namespace CedarRecon.Classification;

/// <summary>
/// Byte constants for ProcessingState during the classification execution
/// phase. These are the classification operator's private interpretation of
/// the shared execution buffer — not an enum, not a public type.
///
/// Values match the ClassificationState enum's byte values (which is
/// : byte) so that existing ClassificationState-typed code can be migrated
/// incrementally: cast (ClassificationState)batch.ProcessingState[i] still
/// works during transition, but the hot path uses these constants directly.
/// </summary>
internal static class ClassificationStateBytes
{
    public const byte None = 0;
    public const byte DuplicateInSource = 1;
    public const byte DuplicateInTarget = 2;
    public const byte SplitPayment = 3;
    public const byte ConsolidatedPayment = 4;
    public const byte AmountMismatch = 5;
    public const byte DateMismatch = 6;
    public const byte MissingInTarget = 7;
    public const byte MissingInSource = 8;
}
