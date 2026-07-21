using CedarRecon.Core.Entities;
using CedarRecon.Core.ValueObjects;
using CedarRecon.Execution;
using Shouldly;

namespace CedarRecon.Tests.Unit.Execution;

/// <summary>
/// Concurrency test for MatchContext.TryClaim — its own doc comment claims
/// thread-safety via ConcurrentDictionary CAS. This test verifies that
/// guarantee holds under actual parallel contention rather than trusting
/// the comment: repeated 1000x per the issue's acceptance criteria, since
/// a race condition may not surface on every run.
/// </summary>
public class MatchContextConcurrencyTests
{
    [Fact]
    public void TryClaim_ParallelContentionOnSameTarget_ExactlyOneWinner()
    {
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            var context = new MatchContext();
            var target = BuildTransaction();

            var winnerCount = 0;

            Parallel.For(0, 8, _ =>
            {
                if (context.TryClaim(target))
                    Interlocked.Increment(ref winnerCount);
            });

            winnerCount.ShouldBe(1,
                $"Iteration {iteration}: expected exactly one winner, got {winnerCount}");
        }
    }

    [Fact]
    public void TryClaim_DifferentTargets_AllSucceed()
    {
        // Sanity check alongside the contention test above — TryClaim
        // should never incorrectly reject a genuinely distinct target
        // just because concurrent claims are happening on OTHER targets
        // at the same time.
        var context = new MatchContext();
        var targets = Enumerable.Range(0, 100).Select(_ => BuildTransaction()).ToList();

        var results = new bool[targets.Count];

        Parallel.For(0, targets.Count, i =>
        {
            results[i] = context.TryClaim(targets[i]);
        });

        results.ShouldAllBe(r => r);
    }

    [Fact]
    public void IsClaimed_ReflectsTryClaimResult()
    {
        var context = new MatchContext();
        var target = BuildTransaction();

        context.IsClaimed(target).ShouldBeFalse();
        context.TryClaim(target).ShouldBeTrue();
        context.IsClaimed(target).ShouldBeTrue();
        context.TryClaim(target).ShouldBeFalse(); // already claimed
    }

    [Fact]
    public void GetUnmatchedTargets_ExcludesClaimedTargets()
    {
        var context = new MatchContext();
        var claimed = BuildTransaction();
        var unclaimed = BuildTransaction();

        context.RegisterTarget(claimed);
        context.RegisterTarget(unclaimed);
        context.TryClaim(claimed);

        var result = context.GetUnmatchedTargets();

        result.ShouldContain(unclaimed);
        result.ShouldNotContain(claimed);
    }

    private static Transaction BuildTransaction() => new()
    {
        Id = TransactionId.From(Guid.NewGuid()),
        NormalizedReference = TransactionReference.FromRaw("TEST"),
        Amount = Money.Of(100m, "USD"),
        ValueDate = DateTimeOffset.UtcNow,
        Description = "test",
        SourceFileName = "test.csv",
        SourceRowNumber = 1,
    };
}
