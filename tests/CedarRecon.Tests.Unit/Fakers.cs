using Bogus;
using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Enums;

namespace CedarRecon.Tests.Unit;

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
}
