namespace CedarRecon.Application.Classification.Indexed;

/// <summary>
/// Maps normalized reference strings to dense sequential integer IDs.
///
/// Why this exists:
/// Every lookup in the classification cascade keys on
/// Transaction.NormalizedReference.Value (a string). Every Dictionary&lt;string,T&gt;
/// operation — TryGetValue, Add, the internal grouping in GroupBy — pays for
/// string hashing (walks every character) and, on collision, string equality
/// comparison (walks characters again). At N=1,000,000 transactions, with
/// TotalLegs() called twice per transaction in the Split/Consolidated phase
/// alone, that's millions of string hash computations on the hot path.
///
/// Interning once, up front, converts every subsequent lookup from
/// "hash and compare a string" to "use an int as an array index" — collapsing
/// O(string length) per comparison to O(1) with no comparison at all, since
/// array indexing has no notion of "hash collision" to resolve.
///
/// IDs are assigned sequentially (0, 1, 2, ...) in first-seen order, which
/// means downstream code can size a flat array to [interner.Count] and index
/// into it directly by ReferenceId — no dictionary needed for the actual
/// classification hot path, only for this one-time encoding step.
///
/// Thread-safety: NOT thread-safe. Build during a single-threaded indexing
/// pass before any parallel work starts — this matches the same discipline
/// used for HashMatchingEngine.BuildTargetIndexAsync (build once, single
/// writer, then read-only).
/// </summary>
public sealed class ReferenceInterner
{
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly List<string> _values = [];

    /// <summary>Number of distinct references interned so far.</summary>
    public int Count => _values.Count;

    /// <summary>
    /// Returns the existing ID for <paramref name="reference"/>, or assigns
    /// and returns a new one if this is the first time it's been seen.
    /// </summary>
    public int GetOrAdd(string reference)
    {
        if (_ids.TryGetValue(reference, out var id))
            return id;

        id = _values.Count;
        _ids.Add(reference, id);
        _values.Add(reference);
        return id;
    }

    /// <summary>
    /// Reverse lookup — ID back to the original string. Diagnostics/export
    /// only; never call this on the classification hot path.
    /// </summary>
    public string GetValue(int id) => _values[id];

    /// <summary>
    /// True if <paramref name="reference"/> has already been interned.
    /// Does not assign a new ID — use during a read-only phase where an
    /// unseen reference should be treated as "no group exists" rather than
    /// silently creating one.
    /// </summary>
    public bool TryGetId(string reference, out int id) => _ids.TryGetValue(reference, out id);
}
