using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Enums;
using CedarRecon.Domain.Pipelines;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace CedarRecon.Application.Classification;

/// <summary>
/// Classifies unmatched transactions into all 8 DiscrepancyTypes.
///
/// Classification cascade (order matters — each phase must fully complete before
/// the next starts, since later phases read classifiedSourceIds/classifiedTargetIds
/// populated by earlier ones):
///   1. DuplicateInSource     — same reference appears 2+ times in source
///   2. DuplicateInTarget     — same reference appears 2+ times in target
///   3. SplitPayment          — for a reference: total source legs == 1 AND total target legs > 1
///   4. ConsolidatedPayment   — for a reference: total source legs > 1 AND total target legs == 1
///   5. AmountMismatch        — reference found on both sides, amounts differ
///   6. DateMismatch          — reference found on both sides, dates differ
///   7. MissingInTarget       — source reference not found in target at all
///   8. MissingInSource       — target reference not found in source at all
///
/// ── Performance history (see docs/exception-classifier-scaling.md) ───────────
///
/// Original implementation used GroupBy().ToDictionary() for the four lookup
/// dictionaries. Benchmarked at N=1,000,000 unmatched transactions, that
/// construction step was ~56% of total Classify() time and ~65% of total
/// allocation — GroupBy hashes every key once internally, then ToDictionary
/// hashes every key AGAIN to insert into the result.
///
/// Fix #1 — DictionaryBuilder: replaced GroupBy().ToDictionary() with
/// CollectionsMarshal.GetValueRefOrAddDefault single-pass construction.
/// Benchmarked: 2.4–3.7x faster, ~50% less allocation, and changed the curve
/// shape from super-linear (0.877 → 1.044 → 1.348 ms/1K rows) to roughly flat
/// (0.259 → 0.434 → 0.389 ms/1K rows) — confirming the double-hash was the
/// actual source of the non-linear scaling, not just a constant-factor cost.
///
/// Fix #2 — Batch parallelism within phases 3, 5&6, 7, 8: once the four lookup
/// dictionaries exist, those phases only READ from them — no mutation — so each
/// transaction's classification decision is a pure function of (transaction,
/// read-only global state frozen by EARLIER phases). That makes per-transaction
/// work within a phase safely parallelizable via Parallel.ForEach with
/// thread-local result batches merged sequentially at the end of each phase.
/// The CASCADE ORDER across phases stays strictly sequential — phase N+1 only
/// starts once phase N has fully merged its results — so only work WITHIN each
/// phase parallelizes, never phases relative to each other.
///
/// Phases 1, 2, 4 stay sequential: 1 and 2 iterate dictionary groups (not the
/// flat transaction list) and are already cheap (DuplicatePhases: 76ms at N=1M,
/// ~3% of total). Phase 4 mirrors phase 3 but targets unmatchedTarget; left
/// sequential pending its own investigation (SplitConsolidatedPhases combined
/// was 358ms at N=1M, suspected cache-locality cost, not allocation — see
/// docs/exception-classifier-scaling.md).
///
/// Split/Consolidated detection — the key domain rule:
///   totalSourceLegs(ref) = matched source legs with ref + unmatched source legs with ref
///   totalTargetLegs(ref) = matched target legs with ref + unmatched target legs with ref
///   totalSourceLegs == 1 AND totalTargetLegs > 1 → every unmatched SOURCE leg is Split
///   totalSourceLegs > 1 AND totalTargetLegs == 1 → every unmatched TARGET leg is Consolidated
///
/// AmountMismatch compares source against the closest unclassified target by amount
/// (MinBy), not target list[0], to avoid arbitrary ordering effects.
///
/// Each transaction is classified exactly once — classifiedIds sets prevent
/// double-reporting. The guard checks the current loop variable, never group[0].
/// </summary>
public sealed class ExceptionClassifier : IExceptionClassifier
{
    private readonly ILogger<ExceptionClassifier> _logger;
    private readonly ClassifierOptions _options;

    public ExceptionClassifier(
        ILogger<ExceptionClassifier> logger,
        ClassifierOptions? options = null)
    {
        _logger = logger;
        _options = options ?? ClassifierOptions.Default;
    }

    public IReadOnlyList<UnmatchedTransaction> Classify(
        IReadOnlyList<Transaction> unmatchedSource,
        IReadOnlyList<Transaction> unmatchedTarget,
        IReadOnlyList<MatchedPair> matchedPairs)
    {
        // ── Build lookup structures — single-pass, see DictionaryBuilder ──────
        var sourceByRef = DictionaryBuilder.BuildGroupedByReference(unmatchedSource);
        var targetByRef = DictionaryBuilder.BuildGroupedByReference(unmatchedTarget);
        var matchedSourceLegCounts = DictionaryBuilder.BuildSourceLegCounts(matchedPairs);
        var matchedTargetLegCounts = DictionaryBuilder.BuildTargetLegCounts(matchedPairs);

        var results = new List<UnmatchedTransaction>(
            unmatchedSource.Count + unmatchedTarget.Count);

        // Each classified exactly once. Writes only ever happen either inside
        // the sequential phases (1/2/4) or in the single-threaded merge step
        // that follows each parallel phase's Parallel.ForEach — never during
        // the parallel work itself. Plain HashSet is safe under that discipline.
        var classifiedSourceIds = new HashSet<Guid>();
        var classifiedTargetIds = new HashSet<Guid>();

        // ── 1. DuplicateInSource (sequential — iterates dictionary groups) ────
        foreach (var (_, group) in sourceByRef)
        {
            if (group.Count <= 1) continue;
            foreach (var tx in group)
            {
                results.Add(new UnmatchedTransaction(tx, DiscrepancyType.DuplicateInSource));
                classifiedSourceIds.Add(tx.Id.Value);
            }
        }

        // ── 2. DuplicateInTarget (sequential — iterates dictionary groups) ────
        foreach (var (_, group) in targetByRef)
        {
            if (group.Count <= 1) continue;
            foreach (var tx in group)
            {
                results.Add(new UnmatchedTransaction(tx, DiscrepancyType.DuplicateInTarget));
                classifiedTargetIds.Add(tx.Id.Value);
            }
        }

        // ── 3. SplitPayment — PARALLEL ────────────────────────────────────────
        RunPhase(
            unmatchedSource,
            _options.DegreeOfParallelism,
            sourceTx =>
            {
                if (classifiedSourceIds.Contains(sourceTx.Id.Value)) return null;

                var refKey = sourceTx.NormalizedReference.Value;
                var totalSourceLegs = TotalLegs(refKey, matchedSourceLegCounts, sourceByRef);
                var totalTargetLegs = TotalLegs(refKey, matchedTargetLegCounts, targetByRef);

                return totalSourceLegs == 1 && totalTargetLegs > 1
                    ? new UnmatchedTransaction(sourceTx, DiscrepancyType.SplitPayment)
                    : null;
            },
            results, classifiedSourceIds);

        // ── 4. ConsolidatedPayment (sequential — see class doc for rationale) ─
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

        // ── 5 & 6. AmountMismatch / DateMismatch — PARALLEL ───────────────────
        // classifiedTargetIds is fully frozen here — step 4 completed above and
        // nothing in this phase writes to classifiedTargetIds, only reads it.
        RunPhase(
            unmatchedSource,
            _options.DegreeOfParallelism,
            sourceTx =>
            {
                if (classifiedSourceIds.Contains(sourceTx.Id.Value)) return null;
                if (!targetByRef.TryGetValue(sourceTx.NormalizedReference.Value, out var candidates))
                    return null;

                var bestTarget = candidates
                    .Where(t => !classifiedTargetIds.Contains(t.Id.Value))
                    .MinBy(t => Math.Abs(t.Amount.Amount - sourceTx.Amount.Amount));

                if (bestTarget is null) return null;

                var amountDiffers = sourceTx.Amount.Amount != bestTarget.Amount.Amount;
                var dateDiffers = sourceTx.ValueDate.UtcDateTime.Date
                                 != bestTarget.ValueDate.UtcDateTime.Date;

                var discrepancy = amountDiffers
                    ? DiscrepancyType.AmountMismatch
                    : dateDiffers
                        ? DiscrepancyType.DateMismatch
                        : DiscrepancyType.MissingInTarget;

                return new UnmatchedTransaction(sourceTx, discrepancy);
            },
            results, classifiedSourceIds);

        // ── 7. MissingInTarget — PARALLEL, terminal (no further phase reads IDs) ─
        RunPhase(
            unmatchedSource,
            _options.DegreeOfParallelism,
            sourceTx => classifiedSourceIds.Contains(sourceTx.Id.Value)
                ? null
                : new UnmatchedTransaction(sourceTx, DiscrepancyType.MissingInTarget),
            results, classifiedIdsToUpdate: null);

        // ── 8. MissingInSource — PARALLEL, terminal ───────────────────────────
        RunPhase(
            unmatchedTarget,
            _options.DegreeOfParallelism,
            targetTx => classifiedTargetIds.Contains(targetTx.Id.Value)
                ? null
                : new UnmatchedTransaction(targetTx, DiscrepancyType.MissingInSource),
            results, classifiedIdsToUpdate: null);

        LogSummary(results);

        return results.AsReadOnly();
    }

    /// <summary>
    /// Runs <paramref name="classify"/> over <paramref name="items"/>, in parallel
    /// batches once the input is large enough to justify it, merging results into
    /// <paramref name="results"/> sequentially after the parallel work completes.
    ///
    /// Correctness contract: <paramref name="classify"/> must be a PURE function of
    /// its input item plus state that is fully frozen BEFORE this call — built
    /// dictionaries, and classifiedIds populated only by EARLIER phases. It must
    /// never depend on classifiedIds entries written by other items in THIS SAME
    /// call, since batch execution order across partitions is not guaranteed.
    ///
    /// <paramref name="classifiedIdsToUpdate"/>: pass null for terminal phases
    /// (nothing downstream reads classifiedIds again) to skip that bookkeeping;
    /// pass the relevant HashSet when a later phase depends on this phase's output.
    /// </summary>
    private static void RunPhase(
        IReadOnlyList<Transaction> items,
        int degreeOfParallelism,
        Func<Transaction, UnmatchedTransaction?> classify,
        List<UnmatchedTransaction> results,
        HashSet<Guid>? classifiedIdsToUpdate)
    {
        // Below this size, parallelism overhead (partitioning, thread handoff,
        // merge) costs more than it saves. Threshold is conservative — tune via
        // benchmark if a different cutoff proves better for a given workload.
        const int ParallelThreshold = 10_000;

        if (items.Count < ParallelThreshold || degreeOfParallelism <= 1)
        {
            foreach (var item in items)
            {
                var result = classify(item);
                if (result is null) continue;

                results.Add(result);
                classifiedIdsToUpdate?.Add(result.Transaction.Id.Value);
            }
            return;
        }

        // Partition + merge: each thread accumulates into its OWN local list —
        // no shared mutable state during the parallel work itself.
        var partitionResults = new ConcurrentBag<List<UnmatchedTransaction>>();

        Parallel.ForEach(
            Partitioner.Create(0, items.Count),
            new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism },
            range =>
            {
                var local = new List<UnmatchedTransaction>();
                for (var i = range.Item1; i < range.Item2; i++)
                {
                    var result = classify(items[i]);
                    if (result is not null) local.Add(result);
                }
                if (local.Count > 0) partitionResults.Add(local);
            });

        // Single-threaded merge — bounded by total matches found, not by the
        // full input size, so it stays cheap relative to the parallel work above.
        foreach (var local in partitionResults)
        {
            foreach (var result in local)
            {
                results.Add(result);
                classifiedIdsToUpdate?.Add(result.Transaction.Id.Value);
            }
        }
    }

    /// <summary>
    /// Total legs for a reference on one side = matched legs + unmatched legs.
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

/// <summary>
/// Configuration for ExceptionClassifier's parallel phases.
/// Not part of ReconciliationOptions — this is an implementation detail of
/// HOW classification executes, not a business/domain tuning knob.
/// </summary>
public sealed class ClassifierOptions
{
    /// <summary>
    /// Max degree of parallelism for batched classification phases (3, 5, 6, 7, 8).
    /// Default: Environment.ProcessorCount. Set to 1 to force fully sequential
    /// execution — useful for deterministic test runs or debugging.
    /// </summary>
    public int DegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    public static readonly ClassifierOptions Default = new();
}