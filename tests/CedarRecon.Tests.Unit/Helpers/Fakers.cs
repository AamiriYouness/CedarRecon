using Bogus;
using CedarRecon.Domain.Entities;
using CedarRecon.Domain.ValueObjects;

namespace CedarRecon.Tests.Unit.Helpers;

/// <summary>
/// Centralized Bogus fakers — reused across all test classes.
/// Seeded for reproducible test runs.
/// </summary>
public static class Fakers
{
    private const int Seed = 42;

    public static Faker<RawTransaction> RawTransaction() => new Faker<RawTransaction>()
        .UseSeed(Seed)
        .RuleFor(t => t.Reference, f => f.Finance.Account(10))
        .RuleFor(t => t.Amount, f => f.Finance.Amount(1, 100_000).ToString("F2"))
        .RuleFor(t => t.Currency, f => f.Finance.Currency().Code)
        .RuleFor(t => t.ValueDate, f => f.Date.Recent(30).ToString("yyyy-MM-dd"))
        .RuleFor(t => t.Description, f => f.Lorem.Sentence(3))
        .RuleFor(t => t.Iban, f => f.Finance.Iban())
        .RuleFor(t => t.CounterpartyName, f => f.Company.CompanyName())
        .RuleFor(t => t.BookingDate, f => f.Date.Recent(30).ToString("yyyy-MM-dd"))
        .RuleFor(t => t.RowNumber, f => f.IndexFaker + 1)
        .RuleFor(t => t.SourceFileName, f => f.System.FileName("csv"));

    // <summary>
    /// Generates realistic Transaction domain objects.
    /// Every field is valid by construction — fakers respect value object constraints.
    ///
    /// Use .Generate() for a single transaction, .Generate(n) for a list.
    /// </summary>
    public static Faker<Transaction> Transaction() =>
        new Faker<Transaction>()
            .UseSeed(Seed)
            .CustomInstantiator(f => new Transaction
            {
                Id = TransactionId.From(f.Random.Guid()),
                NormalizedReference = TransactionReference.FromRaw(f.Finance.Account(10)),
                Amount = Money.Of(f.Finance.Amount(1, 100_000), f.Finance.Currency().Code),
                ValueDate = f.Date.RecentOffset(30).ToUniversalTime(),
                Description = f.Lorem.Sentence(3),
                Iban = f.Finance.Iban(),
                CounterpartyName = f.Company.CompanyName(),
                BookingDate = f.Date.RecentOffset(30).ToUniversalTime(),
                SourceFileName = f.System.FileName("csv"),
                SourceRowNumber = f.IndexFaker + 1,
            });

    /// <summary>
    /// Generates a pair of transactions that should exact-match each other.
    /// Source and target share the same reference, currency, amount, and date.
    /// Amount and date are pinned — only Id and metadata differ.
    /// </summary>
    public static (Transaction Source, Transaction Target) MatchingPair(
        decimal? amount = null,
        string? currency = null,
        DateTimeOffset? valueDate = null)
    {
        var f = new Faker();
        f.Random = new Randomizer(Seed);
        var amt = amount ?? f.Finance.Amount(100, 10_000);
        var cur = currency ?? "USD";
        var date = valueDate ?? f.Date.RecentOffset(30).ToUniversalTime();
        var reference = f.Finance.Account(10);

        var source = new Transaction
        {
            Id = TransactionId.From(Guid.NewGuid()),
            NormalizedReference = TransactionReference.FromRaw(reference),
            Amount = Money.Of(amt, cur),
            ValueDate = date,
            Description = f.Lorem.Sentence(3),
            SourceFileName = "source.csv",
            SourceRowNumber = 1,
        };

        var target = new Transaction
        {
            Id = TransactionId.From(Guid.NewGuid()),
            NormalizedReference = TransactionReference.FromRaw(reference), // same reference → same bucket
            Amount = Money.Of(amt, cur),                 // same amount
            ValueDate = date,                               // same date
            Description = f.Lorem.Sentence(3),               // description can differ
            SourceFileName = "target.csv",
            SourceRowNumber = 1,
        };

        return (source, target);
    }
}
