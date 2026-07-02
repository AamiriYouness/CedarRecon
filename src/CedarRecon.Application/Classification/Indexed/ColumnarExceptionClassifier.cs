using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Pipelines;
using Microsoft.Extensions.Logging;

namespace CedarRecon.Application.Classification.Indexed;

/// <summary>
/// Columnar-native implementation of the 8-phase classification cascade.
/// Each scan phase touches ONLY the column arrays it actually reads —
/// the core benefit of the struct-of-arrays layout over array-of-structs.
///
/// Implements IExceptionClassifier (eager, backward-compatible) via
/// ClassifyColumnar().ToList() delegation — same pattern as the previous
/// IndexedExceptionClassifier's Classify() -> ClassifyStreaming() delegation.
/// No logic duplication between the two paths.
///
/// Hot-path discipline:
/// - ProcessingState is read/written as raw bytes via ClassificationStateBytes
///   constants — no enum boxing, no switch overhead per write
/// - All phase loops access only the specific column arrays they need —
///   MatchKeyId for groups (already in RefGroup), AmountMinor+DayNumber for
///   mismatch, ProcessingState for state checks — no other columns touched
/// - ref readonly on group iteration (same as previous classifier)
///
/// Single-threaded by permanent design — see architecture brief.
/// </summary>
public sealed class ColumnarExceptionClassifier : IExceptionClassifier
{
    private readonly ILogger<ColumnarExceptionClassifier> _logger;

    public ColumnarExceptionClassifier(ILogger<ColumnarExceptionClassifier> logger)
    {
        _logger = logger;
    }

    // ── IExceptionClassifier — eager, backward-compatible ─────────────────────

    public IReadOnlyList<UnmatchedTransaction> Classify(
        IReadOnlyList<Transaction> unmatchedSource,
        IReadOnlyList<Transaction> unmatchedTarget,
        IReadOnlyList<MatchedPair> matchedPairs) =>
        ClassifyColumnar(unmatchedSource, unmatchedTarget, matchedPairs).ToList();

    // ── Primary columnar implementation ───────────────────────────────────────

    public ClassificationResult ClassifyColumnar(
        IReadOnlyList<Transaction> unmatchedSource,
        IReadOnlyList<Transaction> unmatchedTarget,
        IReadOnlyList<MatchedPair> matchedPairs)
    {
        var index = ColumnarIndexBuilder.Build(unmatchedSource, unmatchedTarget, matchedPairs);

        // ProcessingState arrays are already zero-initialized (= None) by
        // ColumnarIndexBuilder — no explicit initialization needed here.
        var src = index.Source;
        var tgt = index.Target;

        // ── Phases 1 & 2: Duplicates ──────────────────────────────────────────
        // Only touches: Groups[] (already loaded for the loop),
        // ProcessingState (write). MatchKeyId, AmountMinor, DayNumber:
        // NOT touched — the whole point of columnar layout.
        for (var g = 0; g < index.Groups.Length; g++)
        {
            ref readonly var group = ref index.Groups[g];

            if (group.SourceCount > 1)
                for (var i = group.SourceStart; i < group.SourceStart + group.SourceCount; i++)
                    src.ProcessingState[i] = ClassificationStateBytes.DuplicateInSource;

            if (group.TargetCount > 1)
                for (var i = group.TargetStart; i < group.TargetStart + group.TargetCount; i++)
                    tgt.ProcessingState[i] = ClassificationStateBytes.DuplicateInTarget;
        }

        // ── Phase 3: SplitPayment ─────────────────────────────────────────────
        for (var g = 0; g < index.Groups.Length; g++)
        {
            ref readonly var group = ref index.Groups[g];
            if (group.TotalSourceLegs != 1 || group.TotalTargetLegs <= 1) continue;

            for (var i = group.SourceStart; i < group.SourceStart + group.SourceCount; i++)
                if (src.ProcessingState[i] == ClassificationStateBytes.None)
                    src.ProcessingState[i] = ClassificationStateBytes.SplitPayment;
        }

        // ── Phase 4: ConsolidatedPayment ──────────────────────────────────────
        for (var g = 0; g < index.Groups.Length; g++)
        {
            ref readonly var group = ref index.Groups[g];
            if (group.TotalSourceLegs <= 1 || group.TotalTargetLegs != 1) continue;

            for (var i = group.TargetStart; i < group.TargetStart + group.TargetCount; i++)
                if (tgt.ProcessingState[i] == ClassificationStateBytes.None)
                    tgt.ProcessingState[i] = ClassificationStateBytes.ConsolidatedPayment;
        }

        // ── Phases 5 & 6: AmountMismatch / DateMismatch ───────────────────────
        // Only touches: AmountMinor[] and DayNumber[] columns (read),
        // ProcessingState (read/write). MatchKeyId, OriginalIndex: NOT touched.
        var srcAmount = src.AmountMinor;
        var tgtAmount = tgt.AmountMinor;
        var srcDay = src.DayNumber;
        var tgtDay = tgt.DayNumber;
        var srcState = src.ProcessingState;
        var tgtState = tgt.ProcessingState;

        for (var g = 0; g < index.Groups.Length; g++)
        {
            ref readonly var group = ref index.Groups[g];
            if (group.TargetCount == 0) continue;

            for (var si = group.SourceStart; si < group.SourceStart + group.SourceCount; si++)
            {
                if (srcState[si] != ClassificationStateBytes.None) continue;

                var sourceAmount = srcAmount[si];
                var bestTargetIndex = -1;
                var bestDiff = long.MaxValue;

                for (var ti = group.TargetStart; ti < group.TargetStart + group.TargetCount; ti++)
                {
                    if (tgtState[ti] != ClassificationStateBytes.None) continue;

                    var diff = Math.Abs(tgtAmount[ti] - sourceAmount);
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        bestTargetIndex = ti;
                    }
                }

                if (bestTargetIndex < 0) continue;

                srcState[si] = srcAmount[si] != tgtAmount[bestTargetIndex]
                    ? ClassificationStateBytes.AmountMismatch
                    : srcDay[si] != tgtDay[bestTargetIndex]
                        ? ClassificationStateBytes.DateMismatch
                        : ClassificationStateBytes.MissingInTarget;
            }
        }

        // ── Phases 7 & 8: MissingInTarget / MissingInSource ──────────────────
        // Flat O(n) sweep — touches ONLY ProcessingState.
        for (var i = 0; i < src.Count; i++)
            if (srcState[i] == ClassificationStateBytes.None)
                srcState[i] = ClassificationStateBytes.MissingInTarget;

        for (var i = 0; i < tgt.Count; i++)
            if (tgtState[i] == ClassificationStateBytes.None)
                tgtState[i] = ClassificationStateBytes.MissingInSource;

        var result = new ClassificationResult
        {
            Source = src,
            Target = tgt,
            SourceOriginals = index.SourceOriginals,
            TargetOriginals = index.TargetOriginals,
        };

        LogSummary(srcState, tgtState);
        return result;
    }

    private void LogSummary(byte[] srcState, byte[] tgtState)
    {
        var counts = new int[9];
        for (var i = 0; i < srcState.Length; i++) counts[srcState[i]]++;
        for (var i = 0; i < tgtState.Length; i++) counts[tgtState[i]]++;

        _logger.LogInformation(
            "Classification complete (columnar engine): {Total} exceptions — " +
            "DupSrc={DupSrc} DupTgt={DupTgt} Split={Split} Consol={Consol} " +
            "AmtMismatch={AmtMismatch} DateMismatch={DateMismatch} " +
            "MissingInTgt={MissingInTgt} MissingInSrc={MissingInSrc}",
            srcState.Length + tgtState.Length,
            counts[ClassificationStateBytes.DuplicateInSource],
            counts[ClassificationStateBytes.DuplicateInTarget],
            counts[ClassificationStateBytes.SplitPayment],
            counts[ClassificationStateBytes.ConsolidatedPayment],
            counts[ClassificationStateBytes.AmountMismatch],
            counts[ClassificationStateBytes.DateMismatch],
            counts[ClassificationStateBytes.MissingInTarget],
            counts[ClassificationStateBytes.MissingInSource]);
    }
}
