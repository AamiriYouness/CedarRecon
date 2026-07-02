using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using CedarRecon.Application.Classification;
using CedarRecon.Domain.Entities;
using System.Collections.Frozen;

namespace CedarRecon.Tests.Performance;

/// <summary>
/// Isolates each phase of ExceptionClassifier.Classify() into its own benchmark
/// so we can see WHICH phase is responsible for the super-linear scaling found
/// in ExceptionClassifierBenchmark.
///
/// Each [Benchmark] reproduces exactly one phase of the real Classify() method,
/// using the same dictionaries built the same way, so the numbers are directly
/// comparable to slices of the full run.
///
/// Phases under test:
///   BuildDictionaries           — GroupBy + ToDictionary, original approach (baseline)
///   BuildDictionariesFast       — CollectionsMarshal single-pass build (shipped fix #1)
///   DuplicatePhases             — steps 1+2, iterate dictionary groups
///   SplitConsolidatedPhases     — steps 3+4, TotalLegs() per transaction, Dictionary-backed
///   SplitConsolidatedPhasesFrozen — same logic, FrozenDictionary-backed reads (hypothesis test)
///   MismatchPhase               — steps 5+6, the (cleared) suspect: .Where(...).MinBy(...)
///   FallbackPhases              — steps 7+8, simple HashSet.Contains filter
///
/// Why test FrozenDictionary here specifically:
/// SplitConsolidatedPhases showed the steepest RELATIVE growth of any phase in the
/// full benchmark (3.06x from N=10K to N=1M) despite modest absolute allocation —
/// the working hypothesis is cache-locality / hashing cost on repeated lookups
/// against a large Dictionary, not an allocation problem. FrozenDictionary is built
/// specifically for "construct once, read many times fast" — it precomputes an
/// optimized internal layout for the read pattern, trading slower construction for
/// faster lookups. This phase does ONLY reads (TotalLegs calls TryGetValue twice
/// per transaction, no writes), so if the cache-locality hypothesis is correct,
/// FrozenDictionary-backed reads should show a flatter curve here specifically.
///
/// Run with:
///   dotnet run -c Release --project tests/CedarRecon.Tests.Performance --filter "*ClassifierPhase*"
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 3, launchCount: 1)]
public class ClassifierPhaseBenchmark
{
    private List<Transaction> _unmatchedSource = null!;
    private List<Transaction> _unmatchedTarget = null!;
    private List<MatchedPair> _matchedPairs = null!;

    // Pre-built dictionaries — shared by phases that run AFTER BuildDictionaries
    // in the real cascade, so we're not re-measuring dictionary construction
    // inside every phase benchmark.
    private Dictionary<string, List<Transaction>> _sourceByRef = null!;
    private Dictionary<string, List<Transaction>> _targetByRef = null!;
    private Dictionary<string, int> _matchedSourceLegCounts = null!;
    private Dictionary<string, int> _matchedTargetLegCounts = null!;

    // Frozen counterparts — built once in Setup so SplitConsolidatedPhasesFrozen
    // measures ONLY the read-pattern difference, not construction cost.
    // FrozenDictionary construction is itself slower than Dictionary — that cost
    // is deliberately excluded from this benchmark to isolate the read hypothesis.
    // (If the hypothesis holds, a SEPARATE benchmark would weigh read gain vs
    // build cost for the real end-to-end decision — this one answers "are reads
    // actually faster" in isolation first.)
    private FrozenDictionary<string, List<Transaction>> _sourceByRefFrozen = null!;
    private FrozenDictionary<string, List<Transaction>> _targetByRefFrozen = null!;
    private FrozenDictionary<string, int> _matchedSourceLegCountsFrozen = null!;
    private FrozenDictionary<string, int> _matchedTargetLegCountsFrozen = null!;

    [Params(10_000, 100_000, 1_000_000)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var (source, target, matched) = ScenarioBuilder.Build(N, seed: 42);
        _unmatchedSource = source;
        _unmatchedTarget = target;
        _matchedPairs = matched;

        _sourceByRef = source
            .GroupBy(t => t.NormalizedReference.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        _targetByRef = target
            .GroupBy(t => t.NormalizedReference.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        _matchedSourceLegCounts = matched
            .GroupBy(p => p.Source.NormalizedReference.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        _matchedTargetLegCounts = matched
            .GroupBy(p => p.Target.NormalizedReference.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        _sourceByRefFrozen = _sourceByRef.ToFrozenDictionary(StringComparer.Ordinal);
        _targetByRefFrozen = _targetByRef.ToFrozenDictionary(StringComparer.Ordinal);
        _matchedSourceLegCountsFrozen = _matchedSourceLegCounts.ToFrozenDictionary(StringComparer.Ordinal);
        _matchedTargetLegCountsFrozen = _matchedTargetLegCounts.ToFrozenDictionary(StringComparer.Ordinal);
    }


    // ── Phase: dictionary construction ────────────────────────────────────────

    [Benchmark(Baseline = true)]
    public int BuildDictionaries()
    {
        var sourceByRef = _unmatchedSource
            .GroupBy(t => t.NormalizedReference.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var targetByRef = _unmatchedTarget
            .GroupBy(t => t.NormalizedReference.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var matchedSourceLegCounts = _matchedPairs
            .GroupBy(p => p.Source.NormalizedReference.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var matchedTargetLegCounts = _matchedPairs
            .GroupBy(p => p.Target.NormalizedReference.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return sourceByRef.Count + targetByRef.Count
             + matchedSourceLegCounts.Count + matchedTargetLegCounts.Count;
    }

    // ── Phase: dictionary construction — CollectionsMarshal single-pass fix ──
    //
    // Problem with BuildDictionaries above: GroupBy builds an internal hash-grouping
    // structure (hashing every key once), then ToDictionary hashes every key AGAIN
    // to insert into the result dictionary. That's two full hash passes per element,
    // plus GroupBy's own intermediate allocations (IGrouping wrappers) that get
    // discarded immediately after ToDictionary copies their contents out.
    //
    // Fix: CollectionsMarshal.GetValueRefOrAddDefault hashes the key ONCE, returns
    // a ref to the dictionary slot (creating it with a default value if missing),
    // and we mutate that slot directly. No GroupBy, no IGrouping wrappers, no
    // second hash pass. Dictionaries are also pre-sized using the same capacity
    // formula DictionaryBuilder uses, avoiding incremental resize-and-rehash as
    // they grow past their initial bucket count.
    //
    // This is NOT unsafe code — CollectionsMarshal is a sanctioned BCL API
    // (System.Runtime.InteropServices, .NET 6+) specifically designed for this
    // exact "avoid the double lookup" pattern. No pointers, no manual memory
    // management — just skipping LINQ's allocation and re-hashing overhead.

    [Benchmark]
    public int BuildDictionariesFast()
    {
        var sourceByRef = DictionaryBuilder.BuildGroupedByReference(_unmatchedSource);
        var targetByRef = DictionaryBuilder.BuildGroupedByReference(_unmatchedTarget);
        var matchedSourceLegCounts = DictionaryBuilder.BuildSourceLegCounts(_matchedPairs);
        var matchedTargetLegCounts = DictionaryBuilder.BuildTargetLegCounts(_matchedPairs);

        return sourceByRef.Count + targetByRef.Count
             + matchedSourceLegCounts.Count + matchedTargetLegCounts.Count;
    }

    // ── Phase: Duplicate detection (steps 1+2) ────────────────────────────────

    [Benchmark]
    public int DuplicatePhases()
    {
        var classifiedSourceIds = new HashSet<Guid>();
        var classifiedTargetIds = new HashSet<Guid>();
        var count = 0;

        foreach (var (_, group) in _sourceByRef)
        {
            if (group.Count <= 1) continue;
            foreach (var tx in group)
            {
                classifiedSourceIds.Add(tx.Id.Value);
                count++;
            }
        }

        foreach (var (_, group) in _targetByRef)
        {
            if (group.Count <= 1) continue;
            foreach (var tx in group)
            {
                classifiedTargetIds.Add(tx.Id.Value);
                count++;
            }
        }

        return count;
    }

    // ── Phase: Split + Consolidated (steps 3+4) ───────────────────────────────

    [Benchmark]
    public int SplitConsolidatedPhases()
    {
        var classifiedSourceIds = new HashSet<Guid>();
        var classifiedTargetIds = new HashSet<Guid>();
        var count = 0;

        foreach (var sourceTx in _unmatchedSource)
        {
            var refKey = sourceTx.NormalizedReference.Value;
            var totalSourceLegs = TotalLegs(refKey, _matchedSourceLegCounts, _sourceByRef);
            var totalTargetLegs = TotalLegs(refKey, _matchedTargetLegCounts, _targetByRef);

            if (totalSourceLegs == 1 && totalTargetLegs > 1)
            {
                classifiedSourceIds.Add(sourceTx.Id.Value);
                count++;
            }
        }

        foreach (var targetTx in _unmatchedTarget)
        {
            var refKey = targetTx.NormalizedReference.Value;
            var totalSourceLegs = TotalLegs(refKey, _matchedSourceLegCounts, _sourceByRef);
            var totalTargetLegs = TotalLegs(refKey, _matchedTargetLegCounts, _targetByRef);

            if (totalSourceLegs > 1 && totalTargetLegs == 1)
            {
                classifiedTargetIds.Add(targetTx.Id.Value);
                count++;
            }
        }

        return count;
    }

    // ── Phase: Split + Consolidated — FrozenDictionary read hypothesis ────────
    // Identical logic to SplitConsolidatedPhases above — only the dictionary
    // TYPE backing the reads changes. Construction cost is excluded (built once
    // in Setup). If this shows a flatter curve than the Dictionary version above,
    // the cache-locality hypothesis is supported and FrozenDictionary is worth
    // adopting for sourceByRef/targetByRef/legCounts in the real classifier.

    [Benchmark]
    public int SplitConsolidatedPhasesFrozen()
    {
        var classifiedSourceIds = new HashSet<Guid>();
        var classifiedTargetIds = new HashSet<Guid>();
        var count = 0;

        foreach (var sourceTx in _unmatchedSource)
        {
            var refKey = sourceTx.NormalizedReference.Value;
            var totalSourceLegs = TotalLegsFrozen(refKey, _matchedSourceLegCountsFrozen, _sourceByRefFrozen);
            var totalTargetLegs = TotalLegsFrozen(refKey, _matchedTargetLegCountsFrozen, _targetByRefFrozen);

            if (totalSourceLegs == 1 && totalTargetLegs > 1)
            {
                classifiedSourceIds.Add(sourceTx.Id.Value);
                count++;
            }
        }

        foreach (var targetTx in _unmatchedTarget)
        {
            var refKey = targetTx.NormalizedReference.Value;
            var totalSourceLegs = TotalLegsFrozen(refKey, _matchedSourceLegCountsFrozen, _sourceByRefFrozen);
            var totalTargetLegs = TotalLegsFrozen(refKey, _matchedTargetLegCountsFrozen, _targetByRefFrozen);

            if (totalSourceLegs > 1 && totalTargetLegs == 1)
            {
                classifiedTargetIds.Add(targetTx.Id.Value);
                count++;
            }
        }

        return count;
    }

    // ── Phase: Amount/Date mismatch — THE SUSPECT (steps 5+6) ─────────────────
    // .Where(...).MinBy(...) per transaction — O(k) per call where k = bucket size.
    // If bucket sizes grow with N (they do here — NOISE references are unique per
    // transaction, so buckets stay O(1), but this isolates the LINQ overhead itself).

    [Benchmark]
    public int MismatchPhase()
    {
        var classifiedTargetIds = new HashSet<Guid>(); // empty — isolates the Where/MinBy cost alone
        var count = 0;

        foreach (var sourceTx in _unmatchedSource)
        {
            if (!_targetByRef.TryGetValue(sourceTx.NormalizedReference.Value, out var candidates))
                continue;

            var bestTarget = candidates
                .Where(t => !classifiedTargetIds.Contains(t.Id.Value))
                .MinBy(t => Math.Abs(t.Amount.Amount - sourceTx.Amount.Amount));

            if (bestTarget is not null) count++;
        }

        return count;
    }

    // ── Phase: fallback (steps 7+8) ───────────────────────────────────────────

    [Benchmark]
    public int FallbackPhases()
    {
        var classifiedSourceIds = new HashSet<Guid>(); // empty — worst case, nothing pre-classified
        var classifiedTargetIds = new HashSet<Guid>();
        var count = 0;

        foreach (var sourceTx in _unmatchedSource)
        {
            if (classifiedSourceIds.Contains(sourceTx.Id.Value)) continue;
            count++;
        }

        foreach (var targetTx in _unmatchedTarget)
        {
            if (classifiedTargetIds.Contains(targetTx.Id.Value)) continue;
            count++;
        }

        return count;
    }

    private static int TotalLegs(
        string refKey,
        IReadOnlyDictionary<string, int> matchedLegCounts,
        IReadOnlyDictionary<string, List<Transaction>> unmatchedByRef)
    {
        var matched = matchedLegCounts.GetValueOrDefault(refKey, 0);
        var unmatched = unmatchedByRef.TryGetValue(refKey, out var group) ? group.Count : 0;
        return matched + unmatched;
    }

    /// <summary>
    /// Identical logic to TotalLegs, but typed against FrozenDictionary so the
    /// JIT can specialize/inline against the concrete frozen lookup implementation
    /// rather than going through the IReadOnlyDictionary interface dispatch.
    /// </summary>
    private static int TotalLegsFrozen(
        string refKey,
        FrozenDictionary<string, int> matchedLegCounts,
        FrozenDictionary<string, List<Transaction>> unmatchedByRef)
    {
        var matched = matchedLegCounts.GetValueOrDefault(refKey, 0);
        var unmatched = unmatchedByRef.TryGetValue(refKey, out var group) ? group.Count : 0;
        return matched + unmatched;
    }
}