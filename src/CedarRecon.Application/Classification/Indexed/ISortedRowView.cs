using System;
using System.Collections.Generic;
using System.Text;

namespace CedarRecon.Application.Classification.Indexed;

/// <summary>
/// Worklist item 4: abstracts over the two competing strategies for getting
/// IndexedTransaction rows in ReferenceId-sorted order, so BuildGroups and
/// every classifier phase can read "the row at sort-order position N"
/// without knowing or caring which strategy produced that ordering.
///
/// THIS IS A MEASURED TRADEOFF, NOT AN ASSUMED WIN EITHER DIRECTION — per
/// the worklist instruction, do not adopt index-sort just because it sounds
/// clever; this investigation has a confirmed track record of plausible
/// hypotheses (FrozenDictionary, worklist item 2's allocation framing,
/// worklist item 3's reverse-list removal) NOT holding up against actual
/// benchmark data. Both implementations below are kept side by side so
/// IndexedClassifierPhaseBenchmark can measure both IndexBuild (sort cost)
/// AND the downstream scan phases (indirection cost) before a choice is
/// documented and the loser deleted.
///
/// Three distinct "position" concepts are in play in this codebase and must
/// not be conflated — this type's job is specifically to manage the
/// boundary between the second and third:
///   1. ORIGINAL-INPUT position: IndexedTransaction.OriginalIndex, a row's
///      index into unmatchedSource/unmatchedTarget. Stable regardless of
///      sorting; used by result materialization to rehydrate Transaction via
///      ReferenceIndex.SourceOriginals/TargetOriginals.
///   2. SORT-ORDER position: what RefGroup.SourceStart/SourceCount and
///      TargetStart/TargetCount describe — a contiguous range in
///      ReferenceId-sorted order. This meaning does NOT change between the
///      two strategies below; only what resolving a sort-order position
///      actually touches in memory changes.
///   3. ROWS-ARRAY position: where a row physically lives in the
///      IndexedTransaction[] array. For StructSorted, this IS the sort-order
///      position (the array was sorted in place). For IndexSorted, the rows
///      array stays in ORIGINAL-INPUT order and sort-order position is an
///      index into a separate int[] rowOrder that must first be dereferenced.
/// </summary>
public interface ISortedRowView
{
    /// <summary>Number of rows in this view (source or target side).</summary>
    int Length { get; }

    /// <summary>
    /// Returns the row at the given SORT-ORDER position (0..Length-1,
    /// ReferenceId-ascending). This is what BuildGroups and every
    /// classifier scan phase call instead of indexing a raw array directly.
    /// </summary>
    ref readonly IndexedTransaction this[int sortOrderPosition] { get; }
}

/// <summary>
/// Strategy 1 (current/baseline): IndexedTransaction[] sorted in place via
/// Array.Sort. Sort-order position IS rows-array position — this view is a
/// thin wrapper with no indirection, [] is a direct array read.
///
/// Sort cost: O(n log n) comparisons AND moves — Array.Sort on a struct
/// array copies whole ~20-24-byte structs on every swap, not just a key.
/// Read cost downstream (BuildGroups, every scan phase): zero indirection,
/// straight array access — exactly what the rest of this codebase already
/// optimizes for (sequential, prefetchable, no pointer chasing).
/// </summary>
public readonly struct StructSortedRowView(IndexedTransaction[] sortedRows) : ISortedRowView
{
    private readonly IndexedTransaction[] _sortedRows = sortedRows;

    public int Length => _sortedRows.Length;

    public ref readonly IndexedTransaction this[int sortOrderPosition] => ref _sortedRows[sortOrderPosition];
}

/// <summary>
/// Strategy 2 (alternative, item 4 candidate): rows array stays in ORIGINAL
/// INPUT order, untouched. A separate int[] rowOrder is sorted by
/// ReferenceId instead — rowOrder[sortOrderPosition] gives the rows-array
/// position to read. Every access through this view costs one extra array
/// read (rowOrder[i]) before the actual row read (rows[rowOrder[i]]).
///
/// Sort cost: O(n log n) comparisons over rowOrder, but each swap moves
/// only a 4-byte int, not a ~20-24-byte struct — the hypothesis this
/// strategy is testing is that smaller-element sorting outweighs the
/// indirection cost added to every downstream read. NOT assumed true;
/// that's exactly what IndexedClassifierPhaseBenchmark needs to confirm
/// across BOTH IndexBuild (where this should win, if anything does) AND
/// every scan phase (where this could lose, since every phase does many
/// more reads than the one-time sort does swaps).
/// </summary>
public readonly struct IndexSortedRowView(IndexedTransaction[] rows, int[] rowOrder) : ISortedRowView
{
    private readonly IndexedTransaction[] _rows = rows;
    private readonly int[] _rowOrder = rowOrder;

    public int Length => _rowOrder.Length;

    public ref readonly IndexedTransaction this[int sortOrderPosition] => ref _rows[_rowOrder[sortOrderPosition]];
}