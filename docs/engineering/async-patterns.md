# Async Patterns

## ConfigureAwait(false)

### The rule

All `await` calls in `CedarRecon.Core`, `CedarRecon.Indexing`,
`CedarRecon.Execution`, and `CedarRecon.Classification` use
`ConfigureAwait(false)`. CA2007 is enforced as an error in `.editorconfig`.

```csharp
// CORRECT — engine and library code
await BuildTargetIndexAsync(targets, ct).ConfigureAwait(false);

// WRONG — engine and library code
await BuildTargetIndexAsync(targets, ct);
```

### Why

When you `await` without `ConfigureAwait(false)`, the continuation after
the await attempts to resume on the captured `SynchronizationContext` —
the request thread in ASP.NET, the UI thread in WPF/WinForms, the test
runner thread in some unit test frameworks.

In library and engine code this causes two problems:

**Deadlock risk** — if a caller holds a synchronization context and waits
synchronously (`.Result`, `.Wait()`) for a task that needs to resume on
that same context, you deadlock. ASP.NET Core removed the synchronization
context so this specific pattern does not deadlock there — but CedarRecon
is designed to run embedded in any host. WPF dashboards, WinForms tools,
and older test runners DO have synchronization contexts. Defensive
`ConfigureAwait(false)` protects against all of them.

**Performance** — even without deadlock, resuming on a captured context
requires a thread switch back to the original thread. `ConfigureAwait(false)`
allows the continuation to resume on any available thread pool thread —
no context switch, lower latency.

### Where it applies

```
CedarRecon.Core           → ConfigureAwait(false) always
CedarRecon.Indexing       → ConfigureAwait(false) always
CedarRecon.Execution      → ConfigureAwait(false) always
CedarRecon.Classification → ConfigureAwait(false) always
CedarRecon.Infrastructure → ConfigureAwait(false) always
CedarRecon.ReconQL        → ConfigureAwait(false) always
CedarRecon.JobDefinition  → ConfigureAwait(false) always

CedarRecon.Api            → Not required (ASP.NET Core has no
                            SynchronizationContext) but preferred
                            for consistency
```

---

## CancellationToken — always forward

Every async method that accepts a `CancellationToken` must forward it
to every async call it makes. CA2016 is enforced as an error.

```csharp
// CORRECT
public async Task BuildTargetIndexAsync(
    IAsyncEnumerable<Transaction> targets,
    CancellationToken ct)
{
    await foreach (var tx in targets.WithCancellation(ct)
                                    .ConfigureAwait(false))
        RegisterTarget(tx);
}

// WRONG — token accepted but not forwarded
public async Task BuildTargetIndexAsync(
    IAsyncEnumerable<Transaction> targets,
    CancellationToken ct)
{
    await foreach (var tx in targets)  // ← ct ignored
        RegisterTarget(tx);
}
```

A cancellation token that is accepted but not forwarded means cancellation
requests are silently ignored inside the method. For a reconciliation run
processing N=1M transactions, an ignored token means the run cannot be
cancelled once started.

---

## No fire-and-forget

Never discard a Task without awaiting it:

```csharp
// WRONG — exception is silently swallowed, run disappears
_ = RunClassificationAsync(batch, ct);

// CORRECT — await it
await RunClassificationAsync(batch, ct).ConfigureAwait(false);

// CORRECT — if background work is genuinely intentional
_ = Task.Run(async () =>
{
    try
    {
        await RunClassificationAsync(batch, ct).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        ExecutionLog.BackgroundTaskFailed(_logger, ex);
    }
}, ct);
```

In a financial reconciliation engine, a silently swallowed exception means
a run that appeared to succeed but produced no output — a correctness bug,
not just a reliability issue.

---

## ValueTask vs Task

Use `ValueTask` for hot-path async methods that frequently complete
synchronously. Use `Task` for I/O-bound operations that always go async.

```csharp
// ValueTask — frequently synchronous (cache hit path)
public ValueTask<RefGroup?> TryGetGroupAsync(int keyId)
{
    if (_groups.TryGetValue(keyId, out var group))
        return ValueTask.FromResult<RefGroup?>(group); // no allocation
    return new ValueTask<RefGroup?>(LoadGroupAsync(keyId));
}

// Task — always async (file read, database query)
public Task<Dataset> LoadDatasetAsync(string path, CancellationToken ct)
    => _reader.ReadAsync(path, ct);
```

CA2012 is enforced as an error — never await a `ValueTask` more than once.
A `ValueTask` that has already been awaited is in an undefined state.

```csharp
// WRONG — ValueTask awaited twice
var vt = GetGroupAsync(keyId);
var first  = await vt;  // ok
var second = await vt;  // undefined behaviour, CA2012 error
```
