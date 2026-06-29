using CedarRecon.Application.Matching;
using CedarRecon.Tests.Unit.Helpers;
using Shouldly;

namespace CedarRecon.Tests.Unit.Matching;

/// <summary>
/// Tests for MatchContext — the shared claim registry.
///
/// Invariants under test:
///   - TryClaim returns true on first call for a given transaction
///   - TryClaim returns false on subsequent calls for the same transaction
///   - IsClaimed reflects TryClaim results correctly
///   - GetUnmatchedTargets returns only unclaimed registered targets
///   - RegisterTarget + GetUnmatchedTargets are consistent
///   - Concurrent TryClaim: exactly one caller wins per transaction
/// </summary>
public class MatchContextTests
{
    // ── TryClaim ─────────────────────────────────────────────────────────────

    [Fact]
    public void TryClaim_FirstCall_ReturnsTrue()
    {
        var ctx = new MatchContext();
        var tx = TransactionBuilder.Default().Build();

        ctx.TryClaim(tx).ShouldBeTrue();
    }

    [Fact]
    public void TryClaim_SecondCallSameTransaction_ReturnsFalse()
    {
        var ctx = new MatchContext();
        var tx = TransactionBuilder.Default().Build();

        ctx.TryClaim(tx);
        ctx.TryClaim(tx).ShouldBeFalse();
    }

    [Fact]
    public void TryClaim_DifferentTransactions_BothReturnTrue()
    {
        var ctx = new MatchContext();
        var tx1 = TransactionBuilder.Default().Build();
        var tx2 = TransactionBuilder.Default().Build();

        ctx.TryClaim(tx1).ShouldBeTrue();
        ctx.TryClaim(tx2).ShouldBeTrue();
    }

    // ── IsClaimed ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsClaimed_BeforeClaim_ReturnsFalse()
    {
        var ctx = new MatchContext();
        var tx = TransactionBuilder.Default().Build();

        ctx.IsClaimed(tx).ShouldBeFalse();
    }

    [Fact]
    public void IsClaimed_AfterClaim_ReturnsTrue()
    {
        var ctx = new MatchContext();
        var tx = TransactionBuilder.Default().Build();

        ctx.TryClaim(tx);

        ctx.IsClaimed(tx).ShouldBeTrue();
    }

    // ── GetUnmatchedTargets ───────────────────────────────────────────────────

    [Fact]
    public void GetUnmatchedTargets_WhenNoneRegistered_ReturnsEmpty()
    {
        var ctx = new MatchContext();
        ctx.GetUnmatchedTargets().ShouldBeEmpty();
    }

    [Fact]
    public void GetUnmatchedTargets_WhenAllRegisteredAndNoneClaimed_ReturnsAll()
    {
        var ctx = new MatchContext();
        var tx1 = TransactionBuilder.Default().Build();
        var tx2 = TransactionBuilder.Default().Build();

        ctx.RegisterTarget(tx1);
        ctx.RegisterTarget(tx2);

        ctx.GetUnmatchedTargets().ShouldBe([tx1, tx2], ignoreOrder: true);
    }

    [Fact]
    public void GetUnmatchedTargets_WhenSomeClaimed_ReturnsOnlyUnclaimed()
    {
        var ctx = new MatchContext();
        var claimed = TransactionBuilder.Default().Build();
        var free = TransactionBuilder.Default().Build();

        ctx.RegisterTarget(claimed);
        ctx.RegisterTarget(free);
        ctx.TryClaim(claimed);

        var unmatched = ctx.GetUnmatchedTargets();

        unmatched.ShouldContain(free);
        unmatched.ShouldNotContain(claimed);
    }

    [Fact]
    public void GetUnmatchedTargets_WhenAllClaimed_ReturnsEmpty()
    {
        var ctx = new MatchContext();
        var tx1 = TransactionBuilder.Default().Build();
        var tx2 = TransactionBuilder.Default().Build();

        ctx.RegisterTarget(tx1);
        ctx.RegisterTarget(tx2);
        ctx.TryClaim(tx1);
        ctx.TryClaim(tx2);

        ctx.GetUnmatchedTargets().ShouldBeEmpty();
    }

    // ── Concurrency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TryClaim_UnderConcurrentCalls_ExactlyOneCallerWins()
    {
        var ctx = new MatchContext();
        var tx = TransactionBuilder.Default().Build();

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => ctx.TryClaim(tx)));

        var results = await Task.WhenAll(tasks);
        var winners = results.Count(r => r);

        winners.ShouldBe(1);
    }

    [Fact]
    public async Task TryClaim_UnderConcurrentCalls_MultipleDistinctTransactions_EachWinsOnce()
    {
        var ctx = new MatchContext();
        var transactions = Enumerable.Range(0, 10)
            .Select(_ => TransactionBuilder.Default().Build())
            .ToList();

        // For each transaction, 5 threads race to claim it
        var tasks = transactions.SelectMany(tx =>
            Enumerable.Range(0, 5).Select(_ => Task.Run(() => ctx.TryClaim(tx))));

        var results = await Task.WhenAll(tasks);

        // Total successes should equal number of distinct transactions
        results.Count(r => r).ShouldBe(transactions.Count);
    }
}
