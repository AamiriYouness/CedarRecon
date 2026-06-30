using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Enums;
using CedarRecon.Domain.Pipelines;
using Microsoft.Extensions.Logging;

namespace CedarRecon.Application.Classification;

/// <summary>
/// Classifies unmatched transactions into all 8 DiscrepancyTypes.
///
/// Classification cascade (order matters — first match wins per transaction):
///   1. DuplicateInSource     — same reference appears 2+ times in source
///   2. DuplicateInTarget     — same reference appears 2+ times in target
///   3. SplitPayment          — for a reference: total source legs == 1 AND total target legs > 1
///   4. ConsolidatedPayment   — for a reference: total source legs > 1 AND total target legs == 1
///   5. AmountMismatch        — reference found on both sides, amounts differ
///   6. DateMismatch          — reference found on both sides, dates differ
///   7. MissingInTarget       — source reference not found in target at all
///   8. MissingInSource       — target reference not found in source at all
///
/// All lookups are O(n) via dictionaries. No nested iteration over the full set.
///
/// Split/Consolidated detection — the key domain rule:
///   A split is "one thing on one side, many things on the other side" for a
///   given reference. That count must include BOTH matched and unmatched legs,
///   because a split with 2 of 3 legs already matched is still a split — the
///   transaction's role doesn't change just because some siblings landed first.
///
///   totalSourceLegs(ref) = matched source legs with ref + unmatched source legs with ref
///   totalTargetLegs(ref) = matched target legs with ref + unmatched target legs with ref
///
///   totalSourceLegs == 1 AND totalTargetLegs > 1 → every unmatched SOURCE leg is Split
///   totalSourceLegs > 1 AND totalTargetLegs == 1 → every unmatched TARGET leg is Consolidated
///
///   Comparing only "how many target legs does this source's reference have" (without
///   also checking the source side's own total) misclassifies many-to-one scenarios —
///   a reference with 2 source legs and 1 target leg is a consolidation, not nothing,
///   even though "target leg count for this ref" alone is only 1.
///
/// AmountMismatch compares source against the closest unclassified target by amount
/// (MinBy), not target list[0], to avoid arbitrary ordering effects when a reference
/// has multiple distinct target candidates remaining after split/consolidated removal.
///
/// Each transaction is classified exactly once — classifiedIds sets prevent
/// double-reporting. The guard checks the current loop variable, never group[0].
/// </summary>
public sealed class ExceptionClassifier : IExceptionClassifier
{
    private readonly ILogger<ExceptionClassifier> _logger;

    public ExceptionClassifier(ILogger<ExceptionClassifier> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<UnmatchedTransaction> Classify(
        IReadOnlyList<Transaction> unmatchedSource,
        IReadOnlyList<Transaction> unmatchedTarget,
        IReadOnlyList<MatchedPair> matchedPairs)
    {
        var results = new List<UnmatchedTransaction>(
            unmatchedSource.Count + unmatchedTarget.Count);

        // ── Build lookup structures — all O(n) ───────────────────────────────

        var sourceByRef = unmatchedSource
            .GroupBy(t => t.NormalizedReference.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var targetByRef = unmatchedTarget
            .GroupBy(t => t.NormalizedReference.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Matched leg counts per reference — one count per side.
        // A reference's matched source legs come from MatchedPair.Source;
        // its matched target legs come from MatchedPair.Target.
        var matchedSourceLegCounts = matchedPairs
            .GroupBy(p => p.Source.NormalizedReference.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var matchedTargetLegCounts = matchedPairs
            .GroupBy(p => p.Target.NormalizedReference.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // Track classified transactions — each classified exactly once
        var classifiedSourceIds = new HashSet<Guid>();
        var classifiedTargetIds = new HashSet<Guid>();

        // ── 1. DuplicateInSource ─────────────────────────────────────────────
        foreach (var (_, group) in sourceByRef)
        {
            if (group.Count <= 1) continue;

            foreach (var tx in group)
            {
                results.Add(new UnmatchedTransaction(tx, DiscrepancyType.DuplicateInSource));
                classifiedSourceIds.Add(tx.Id.Value);
            }
        }

        // ── 2. DuplicateInTarget ─────────────────────────────────────────────
        foreach (var (_, group) in targetByRef)
        {
            if (group.Count <= 1) continue;

            foreach (var tx in group)
            {
                results.Add(new UnmatchedTransaction(tx, DiscrepancyType.DuplicateInTarget));
                classifiedTargetIds.Add(tx.Id.Value);
            }
        }

        // ── 3. SplitPayment ───────────────────────────────────────────────────
        // totalSourceLegs(ref) == 1 AND totalTargetLegs(ref) > 1
        foreach (var sourceTx in unmatchedSource)
        {
            if (classifiedSourceIds.Contains(sourceTx.Id.Value)) continue;

            var refKey = sourceTx.NormalizedReference.Value;

            var totalSourceLegs = TotalLegs(refKey, matchedSourceLegCounts, sourceByRef);
            var totalTargetLegs = TotalLegs(refKey, matchedTargetLegCounts, targetByRef);

            if (totalSourceLegs == 1 && totalTargetLegs > 1)
            {
                results.Add(new UnmatchedTransaction(sourceTx, DiscrepancyType.SplitPayment));
                classifiedSourceIds.Add(sourceTx.Id.Value);
            }
        }

        // ── 4. ConsolidatedPayment ────────────────────────────────────────────
        // totalSourceLegs(ref) > 1 AND totalTargetLegs(ref) == 1
        foreach (var targetTx in unmatchedTarget)
        {
            if (classifiedTargetIds.Contains(targetTx.Id.Value)) continue;

            var refKey = targetTx.NormalizedReference.Value;

            var totalSourceLegs = TotalLegs(refKey, matchedSourceLegCounts, sourceByRef);
            var totalTargetLegs = TotalLegs(refKey, matchedTargetLegCounts, targetByRef);

            if (totalSourceLegs > 1 && totalTargetLegs == 1)
            {
                results.Add(new UnmatchedTransaction(targetTx, DiscrepancyType.ConsolidatedPayment));
                classifiedTargetIds.Add(targetTx.Id.Value);
            }
        }

        // ── 5 & 6. AmountMismatch / DateMismatch ────────────────────────────
        foreach (var sourceTx in unmatchedSource)
        {
            if (classifiedSourceIds.Contains(sourceTx.Id.Value)) continue;
            if (!targetByRef.TryGetValue(sourceTx.NormalizedReference.Value, out var candidates))
                continue;

            var bestTarget = candidates
                .Where(t => !classifiedTargetIds.Contains(t.Id.Value))
                .MinBy(t => Math.Abs(t.Amount.Amount - sourceTx.Amount.Amount));

            if (bestTarget is null) continue;

            var amountDiffers = sourceTx.Amount.Amount != bestTarget.Amount.Amount;
            var dateDiffers = sourceTx.ValueDate.UtcDateTime.Date
                             != bestTarget.ValueDate.UtcDateTime.Date;

            var discrepancy = amountDiffers
                ? DiscrepancyType.AmountMismatch
                : dateDiffers
                    ? DiscrepancyType.DateMismatch
                    : DiscrepancyType.MissingInTarget; // both match but upstream strategy failed

            results.Add(new UnmatchedTransaction(sourceTx, discrepancy));
            classifiedSourceIds.Add(sourceTx.Id.Value);
        }

        // ── 7. MissingInTarget ───────────────────────────────────────────────
        foreach (var sourceTx in unmatchedSource)
        {
            if (classifiedSourceIds.Contains(sourceTx.Id.Value)) continue;

            results.Add(new UnmatchedTransaction(sourceTx, DiscrepancyType.MissingInTarget));
        }

        // ── 8. MissingInSource ───────────────────────────────────────────────
        foreach (var targetTx in unmatchedTarget)
        {
            if (classifiedTargetIds.Contains(targetTx.Id.Value)) continue;

            results.Add(new UnmatchedTransaction(targetTx, DiscrepancyType.MissingInSource));
        }

        LogSummary(results);

        return results.AsReadOnly();
    }

    /// <summary>
    /// Total legs for a reference on one side = matched legs + unmatched legs.
    /// Used identically for both source and target sides — caller passes the
    /// matching matched-count dictionary and unmatched-by-ref dictionary.
    /// </summary>
    private static int TotalLegs(
        string refKey,
        IReadOnlyDictionary<string, int> matchedLegCounts,
        IReadOnlyDictionary<string, List<Transaction>> unmatchedByRef)
    {
        var matched = matchedLegCounts.GetValueOrDefault(refKey, 0);
        var unmatched = unmatchedByRef.TryGetValue(refKey, out var group) ? group.Count : 0;
        return matched + unmatched;
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    private void LogSummary(List<UnmatchedTransaction> results)
    {
        var byType = results
            .GroupBy(r => r.Reason)
            .ToDictionary(g => g.Key, g => g.Count());

        _logger.LogInformation(
            "Classification complete: {Total} exceptions — " +
            "DupSrc={DupSrc} DupTgt={DupTgt} Split={Split} Consol={Consol} " +
            "AmtMismatch={AmtMismatch} DateMismatch={DateMismatch} " +
            "MissingInTgt={MissingInTgt} MissingInSrc={MissingInSrc}",
            results.Count,
            byType.GetValueOrDefault(DiscrepancyType.DuplicateInSource),
            byType.GetValueOrDefault(DiscrepancyType.DuplicateInTarget),
            byType.GetValueOrDefault(DiscrepancyType.SplitPayment),
            byType.GetValueOrDefault(DiscrepancyType.ConsolidatedPayment),
            byType.GetValueOrDefault(DiscrepancyType.AmountMismatch),
            byType.GetValueOrDefault(DiscrepancyType.DateMismatch),
            byType.GetValueOrDefault(DiscrepancyType.MissingInTarget),
            byType.GetValueOrDefault(DiscrepancyType.MissingInSource));
    }
}

