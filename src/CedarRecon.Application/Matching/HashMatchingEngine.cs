using CedarRecon.Application.Matching.Strategies;
using CedarRecon.Domain;
using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace CedarRecon.Application.Matching;

/// <summary>
/// O(n) hash-based matching engine.
///
/// Architecture:
///   Phase 1 — BuildTargetIndexAsync: O(n) scan of all target transactions
///             → builds Dictionary&lt;string, List&lt;Transaction&gt;&gt; keyed by Transaction.IndexKey
///             → IndexKey = NormalizedReference.Value + "|" + Amount.Currency
///   Phase 2 — Match(source): O(1) dictionary lookup → ordered strategy pipeline
///             → Exact → Fuzzy → Partial → Unmatched
///
/// NEVER O(n²). If you see a nested foreach over all targets, flag immediately.
///
/// Thread-safety:
///   - Index is built exclusively during BuildTargetIndexAsync (single-threaded write)
///   - After build, _targetIndex is read-only → safe for concurrent Match() calls
///   - MatchContext owns all claim state via ConcurrentDictionary (lock-free CAS)
///   - _targetIndex and _context are swapped as a unit at end of build — no partial state
/// </summary>
public sealed class HashMatchingEngine : IMatchingEngine
{
    private readonly ILogger<HashMatchingEngine> _logger;
    private readonly IReadOnlyList<IMatchStrategy> _strategies;

    // Built during BuildTargetIndexAsync — read-only after that
    // Key: Transaction.IndexKey (NormalizedReference + "|" + Currency)
    private Dictionary<string, List<Transaction>> _targetIndex = [];

    // Owns claimed-target tracking and unmatched-target reporting for this run.
    // Swapped atomically with _targetIndex at end of BuildTargetIndexAsync.
    private MatchContext _context = new();

    public HashMatchingEngine(
        ILogger<HashMatchingEngine> logger,
        IReadOnlyList<IMatchStrategy>? strategies = null)
    {
        _logger = logger;
        _strategies = strategies ?? MatchStrategyFactory.CreateDefault();
    }

   
    /// <summary>
    /// Build the hash index. O(n) over target transactions.
    /// Must complete — and the returned Task must be awaited — before any Match() calls.
    ///
    /// <paramref name="expectedCount"/>: pass the approximate number of target transactions
    /// so the dictionary is pre-sized without rehashing. Default 65_536 (not 1M — that
    /// allocated ~40MB for datasets that might have 10K transactions).
    /// </summary>
    public async Task BuildTargetIndexAsync(
        IAsyncEnumerable<Transaction> targetTransactions,
        int expectedCount = 65_536,
        CancellationToken ct = default)
    {
        var context = new MatchContext();
        var index = new Dictionary<string, List<Transaction>>(
            capacity: expectedCount,
            comparer: StringComparer.Ordinal);

        var total = 0;

        await foreach (var tx in targetTransactions.ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            context.RegisterTarget(tx);
            total++;

            // O(1) insert — CollectionsMarshal.GetValueRefOrAddDefault would be marginally
            // faster here but this is readable and the build is single-threaded anyway.
            if (!index.TryGetValue(tx.IndexKey, out var bucket))
            {
                bucket = new List<Transaction>(1);
                index[tx.IndexKey] = bucket;
            }
            bucket.Add(tx);
        }

        // Swap both pieces of state atomically.
        // Any Match() call issued after this line sees the fully built index.
        // There is no window where _targetIndex is new but _context is old.
        _targetIndex = index;
        _context = context;

        _logger.LogInformation(
            "Target index built: {UniqueKeys} unique keys from {Total} transactions using {Strategies} strategies",
            index.Count, total, _strategies.Count);
    }

    /// <summary>
    /// Match one source transaction against the index.
    /// Thread-safe — safe to call from Parallel.ForEachAsync concurrently.
    ///
    /// O(1) dictionary lookup. Strategy cascade runs on the small bucket only (k candidates),
    /// never on all targets. End-to-end run is O(n) amortised for well-distributed data.
    /// Worst case is O(n·k) if all transactions share the same IndexKey — bounded by bucket cap.
    /// </summary>
    public MatchResult Match(Transaction source, ReconciliationOptions options)
    {
        // O(1) — only transactions with same NormalizedReference + Currency are candidates
        if (!_targetIndex.TryGetValue(source.IndexKey, out var candidates))
            return Unmatched(source, DiscrepancyType.MissingInTarget);

        // Strategy pipeline — first non-null result wins
        // Strategies are stateless; all claim logic flows through _context
        foreach (var strategy in _strategies)
        {
            var pair = strategy.TryMatch(source, candidates, options, _context);
            if (pair is not null)
                return new MatchedResult(pair);
        }

        return Unmatched(source, DiscrepancyType.MissingInTarget);
    }

    /// <summary>
    /// All target transactions that were never claimed by any strategy.
    /// Call only after all source transactions have been processed.
    /// </summary>
    public IReadOnlyList<Transaction> GetUnmatchedTargets() =>
        _context.GetUnmatchedTargets();

    private static UnmatchedResult Unmatched(Transaction source, DiscrepancyType reason) =>
        new(new UnmatchedTransaction(source, reason));
}
