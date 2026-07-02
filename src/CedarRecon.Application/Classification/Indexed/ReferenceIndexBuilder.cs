using CedarRecon.Domain.Entities;

namespace CedarRecon.Application.Classification.Indexed;

/// <summary>
/// Immutable result of ReferenceIndexBuilder.Build() — everything
/// IndexedExceptionClassifier needs to run the cascade.
///
/// Generic over the two ISortedRowView implementations (worklist item 4):
/// TSourceView and TTargetView are the sort strategies applied to the source
/// and target row arrays respectively. In practice both are always the same
/// concrete type (both StructSortedRowView or both IndexSortedRowView) but
/// keeping them as separate type parameters costs nothing and avoids a
/// constraint that isn't actually required.
///
/// SourceOriginals/TargetOriginals (worklist item 1): the original
/// unmatchedSource/unmatchedTarget lists passed into Build(), kept verbatim
/// so IndexedTransaction.OriginalIndex can index into them to rehydrate a
/// full Transaction lazily, only at result-materialization time.
/// </summary>
public sealed class ReferenceIndex<TSourceView, TTargetView>
    where TSourceView : struct, ISortedRowView
    where TTargetView : struct, ISortedRowView
{
    public TSourceView SourceRows { get; }
    public TTargetView TargetRows { get; }
    public RefGroup[] Groups { get; }
    public ReferenceInterner Interner { get; }
    public IReadOnlyList<Transaction> SourceOriginals { get; }
    public IReadOnlyList<Transaction> TargetOriginals { get; }

    public ReferenceIndex(
        TSourceView sourceRows,
        TTargetView targetRows,
        RefGroup[] groups,
        ReferenceInterner interner,
        IReadOnlyList<Transaction> sourceOriginals,
        IReadOnlyList<Transaction> targetOriginals)
    {
        SourceRows = sourceRows;
        TargetRows = targetRows;
        Groups = groups;
        Interner = interner;
        SourceOriginals = sourceOriginals;
        TargetOriginals = targetOriginals;
    }
}

/// <summary>
/// Factory interface for sort strategies — produces a sorted view over a
/// raw IndexedTransaction[] array. Implemented by StructSortStrategyFactory
/// and IndexSortStrategyFactory; injected into ReferenceIndexBuilder so the
/// sort strategy is a construction-time choice, not a per-call parameter,
/// keeping Classify() a pure function of its inputs.
/// </summary>
public interface ISortStrategyFactory<TSourceView, TTargetView>
    where TSourceView : struct, ISortedRowView
    where TTargetView : struct, ISortedRowView
{
    TSourceView SortSource(IndexedTransaction[] rows);
    TTargetView SortTarget(IndexedTransaction[] rows);
}

/// <summary>
/// Factory for StructSortedRowView: sorts the IndexedTransaction[] in place
/// via Array.Sort (moves whole structs on every swap), returns a view
/// wrapping the now-sorted array.
/// </summary>
public sealed class StructSortStrategyFactory
    : ISortStrategyFactory<StructSortedRowView, StructSortedRowView>
{
    public static readonly StructSortStrategyFactory Instance = new();

    public StructSortedRowView SortSource(IndexedTransaction[] rows) => Sort(rows);
    public StructSortedRowView SortTarget(IndexedTransaction[] rows) => Sort(rows);

    private static StructSortedRowView Sort(IndexedTransaction[] rows)
    {
        Array.Sort(rows, static (a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
        return new StructSortedRowView(rows);
    }
}

/// <summary>
/// Factory for IndexSortedRowView: leaves IndexedTransaction[] in original
/// input order, builds and sorts a parallel int[] rowOrder by ReferenceId
/// (each element is a rows-array index, 4 bytes vs ~20-24 bytes per struct
/// swap), returns a view wrapping both.
/// </summary>
public sealed class IndexSortStrategyFactory
    : ISortStrategyFactory<IndexSortedRowView, IndexSortedRowView>
{
    public static readonly IndexSortStrategyFactory Instance = new();

    public IndexSortedRowView SortSource(IndexedTransaction[] rows) => Sort(rows);
    public IndexSortedRowView SortTarget(IndexedTransaction[] rows) => Sort(rows);

    private static IndexSortedRowView Sort(IndexedTransaction[] rows)
    {
        var rowOrder = new int[rows.Length];
        for (var i = 0; i < rowOrder.Length; i++) rowOrder[i] = i;
        Array.Sort(rowOrder, (a, b) => rows[a].ReferenceId.CompareTo(rows[b].ReferenceId));
        return new IndexSortedRowView(rows, rowOrder);
    }
}

/// <summary>
/// Builds the sorted IndexedTransaction[] arrays and the unified RefGroup[]
/// index from raw unmatched source/target transactions and matched pairs.
///
/// Generic over ISortedRowView (worklist item 4): the sort strategy is
/// selected at build time via the injected factory and baked into the
/// returned ReferenceIndex type, invisible to IndexedExceptionClassifier
/// .Classify() — keeping Classify() a pure function of its inputs regardless
/// of which sort strategy is in use. See SortedRowView.cs for the two
/// concrete strategies and the tradeoff rationale.
/// </summary>
public sealed class ReferenceIndexBuilder<TSourceView, TTargetView>
    where TSourceView : struct, ISortedRowView
    where TTargetView : struct, ISortedRowView
{
    private readonly ISortStrategyFactory<TSourceView, TTargetView> _factory;

    public ReferenceIndexBuilder(ISortStrategyFactory<TSourceView, TTargetView> factory)
    {
        _factory = factory;
    }

    public ReferenceIndex<TSourceView, TTargetView> Build(
        IReadOnlyList<Transaction> unmatchedSource,
        IReadOnlyList<Transaction> unmatchedTarget,
        IReadOnlyList<MatchedPair> matchedPairs)
    {
        var interner = new ReferenceInterner();

        // ── Step 1: encode source/target rows, interning references ──────────
        // OriginalIndex is the row's position in unmatchedSource/unmatchedTarget
        // (the ORIGINAL input lists), assigned before sorting. The sort
        // strategy operates on a separate level (sort order vs rows-array
        // position) and never touches OriginalIndex — it stays a stable
        // pointer into the original input regardless of strategy.
        var sourceRowsRaw = EncodeRows(unmatchedSource, interner);
        var targetRowsRaw = EncodeRows(unmatchedTarget, interner);

        // ── Step 2: sort by ReferenceId via the configured strategy ──────────
        // The factory both performs the sort and wraps the result in the
        // view type — StructSortStrategyFactory sorts in place and wraps;
        // IndexSortStrategyFactory builds/sorts a parallel int[] rowOrder
        // without touching the rows array, then wraps both.
        var sourceRows = _factory.SortSource(sourceRowsRaw);
        var targetRows = _factory.SortTarget(targetRowsRaw);

        // ── Step 3: encode matched-pair leg counts into dense arrays ──────────
        var matchedSourceLegCounts = new int[interner.Count];
        var matchedTargetLegCounts = new int[interner.Count];

        foreach (var pair in matchedPairs)
        {
            var sourceRefId = interner.GetOrAdd(pair.Source.NormalizedReference.Value);
            var targetRefId = interner.GetOrAdd(pair.Target.NormalizedReference.Value);

            EnsureCapacity(ref matchedSourceLegCounts, sourceRefId);
            EnsureCapacity(ref matchedTargetLegCounts, targetRefId);

            matchedSourceLegCounts[sourceRefId]++;
            matchedTargetLegCounts[targetRefId]++;
        }

        // ── Step 4: single merge pass over sorted source/target -> RefGroup[] ─
        var groups = BuildGroups(
            sourceRows, targetRows,
            matchedSourceLegCounts, matchedTargetLegCounts,
            interner.Count);

        return new ReferenceIndex<TSourceView, TTargetView>(
            sourceRows, targetRows, groups, interner, unmatchedSource, unmatchedTarget);
    }

    // ── Row encoding ─────────────────────────────────────────────────────────

    private static IndexedTransaction[] EncodeRows(
        IReadOnlyList<Transaction> transactions,
        ReferenceInterner interner)
    {
        var rows = new IndexedTransaction[transactions.Count];

        for (var i = 0; i < transactions.Count; i++)
        {
            var tx = transactions[i];
            var refId = interner.GetOrAdd(tx.NormalizedReference.Value);
            var date = DateOnly.FromDateTime(tx.ValueDate.UtcDateTime.Date);
            var amountMinor = MoneyMinorUnitsConverter.ToMinorUnits(tx.Amount.Amount);

            rows[i] = new IndexedTransaction(i, refId, amountMinor, date.DayNumber);
        }

        return rows;
    }

    // ── Matched-count array growth ────────────────────────────────────────────

    private static void EnsureCapacity(ref int[] array, int requiredIndex)
    {
        if (requiredIndex < array.Length) return;
        var newSize = Math.Max(requiredIndex + 1, array.Length * 2);
        Array.Resize(ref array, newSize);
    }

    // ── Group merge ───────────────────────────────────────────────────────────

    private static RefGroup[] BuildGroups(
        TSourceView sortedSource,
        TTargetView sortedTarget,
        int[] matchedSourceLegCounts,
        int[] matchedTargetLegCounts,
        int internedCount)
    {
        var groups = new RefGroup[internedCount];
        var count = 0;
        var si = 0;
        var ti = 0;

        while (si < sortedSource.Length || ti < sortedTarget.Length)
        {
            int currentRefId;

            if (si < sortedSource.Length && ti < sortedTarget.Length)
                currentRefId = Math.Min(sortedSource[si].ReferenceId, sortedTarget[ti].ReferenceId);
            else if (si < sortedSource.Length)
                currentRefId = sortedSource[si].ReferenceId;
            else
                currentRefId = sortedTarget[ti].ReferenceId;

            var sourceStart = si;
            while (si < sortedSource.Length && sortedSource[si].ReferenceId == currentRefId)
                si++;
            var sourceCount = si - sourceStart;

            var targetStart = ti;
            while (ti < sortedTarget.Length && sortedTarget[ti].ReferenceId == currentRefId)
                ti++;
            var targetCount = ti - targetStart;

            var matchedSource = currentRefId < matchedSourceLegCounts.Length
                ? matchedSourceLegCounts[currentRefId] : 0;
            var matchedTarget = currentRefId < matchedTargetLegCounts.Length
                ? matchedTargetLegCounts[currentRefId] : 0;

            groups[count++] = new RefGroup
            {
                ReferenceId = currentRefId,
                SourceStart = sourceStart,
                SourceCount = sourceCount,
                TargetStart = targetStart,
                TargetCount = targetCount,
                MatchedSourceCount = matchedSource,
                MatchedTargetCount = matchedTarget,
            };
        }

        if (count < groups.Length)
            Array.Resize(ref groups, count);

        return groups;
    }
}