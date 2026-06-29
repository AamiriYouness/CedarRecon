using Bogus;
using CedarRecon.Domain.Entities;
using CedarRecon.Domain.ValueObjects;

namespace CedarRecon.Tests.Unit.Helpers;

/// <summary>
/// Fluent builder for Transaction test fixtures.
///
/// Two modes:
///   1. TransactionBuilder.Default() — deterministic defaults, set only what the test cares about.
///   2. TransactionBuilder.Random()  — Bogus-generated values, seeded for reproducibility.
///      Use for volume/property-based tests where you want realistic but varied data.
///
/// Bogus seed is fixed (42) so test runs are reproducible in CI.
/// </summary>
internal class TransactionBuilder
{
    private static readonly Faker Faker = new Faker();

    // Deterministic defaults — stable across runs, easy to reason about in assertions
    private Guid? _id = null;
    private decimal _amount = 1_000m;
    private string _currency = "USD";
    private DateTimeOffset _valueDate = new(2024, 3, 15, 0, 0, 0, TimeSpan.Zero);
    private string _reference = "REF-001";
    private string _description = "Test transaction";
    private string? _iban = null;
    private string? _counterpartyName = null;
    private DateTimeOffset? _bookingDate = null;
    private string _sourceFileName = "test.csv";
    private int _sourceRowNumber = 1;

    // ── Factory methods ───────────────────────────────────────────────────────

    /// <summary>Fully deterministic — use when the test needs to reason about exact values.</summary>
    public static TransactionBuilder Default() => new();

    /// <summary>
    /// Bogus-backed — all fields randomised from realistic financial data.
    /// Seed is fixed so the same sequence is generated every run.
    /// Use when the test only cares about structure, not specific values.
    /// </summary>
    public static TransactionBuilder Random()
    {
        Faker.Random = new Randomizer(42);
        return new TransactionBuilder()
            .WithAmount(Faker.Finance.Amount(1, 100_000))
            .WithCurrency(Faker.Finance.Currency().Code)
            .WithValueDate(Faker.Date.RecentOffset(30).ToUniversalTime())
            .WithReference(Faker.Finance.Account(10))
            .WithDescription(Faker.Lorem.Sentence(3))
            .WithIban(Faker.Finance.Iban())
            .WithCounterpartyName(Faker.Company.CompanyName())
            .WithSourceFileName(Faker.System.FileName("csv"))
            .WithSourceRowNumber(Faker.IndexFaker + 1);
    }

    // ── Fluent setters ────────────────────────────────────────────────────────

    public TransactionBuilder WithId(Guid id) { _id = id; return this; }
    public TransactionBuilder WithAmount(decimal amount) { _amount = amount; return this; }
    public TransactionBuilder WithCurrency(string currency) { _currency = currency; return this; }
    public TransactionBuilder WithValueDate(DateTimeOffset d) { _valueDate = d; return this; }
    public TransactionBuilder WithReference(string reference) { _reference = reference; return this; }
    public TransactionBuilder WithDescription(string description) { _description = description; return this; }
    public TransactionBuilder WithIban(string? iban) { _iban = iban; return this; }
    public TransactionBuilder WithCounterpartyName(string? name) { _counterpartyName = name; return this; }
    public TransactionBuilder WithBookingDate(DateTimeOffset? date) { _bookingDate = date; return this; }
    public TransactionBuilder WithSourceFileName(string fileName) { _sourceFileName = fileName; return this; }
    public TransactionBuilder WithSourceRowNumber(int row) { _sourceRowNumber = row; return this; }

    // ── Build ─────────────────────────────────────────────────────────────────

    public Transaction Build() => new()
    {
        Id = TransactionId.From(_id ?? Guid.NewGuid()),
        NormalizedReference = TransactionReference.FromRaw(_reference),
        Amount = Money.Of(_amount, _currency),
        ValueDate = _valueDate,
        Description = _description,
        Iban = _iban,
        CounterpartyName = _counterpartyName,
        BookingDate = _bookingDate,
        SourceFileName = _sourceFileName,
        SourceRowNumber = _sourceRowNumber,
    };

    /// <summary>
    /// Build a list of N transactions — each gets a unique reference and row number
    /// so they don't all land in the same index bucket.
    /// </summary>
    public static IReadOnlyList<Transaction> BuildMany(int count, Action<TransactionBuilder, int>? configure = null)
    {
        var results = new List<Transaction>(count);
        for (var i = 0; i < count; i++)
        {
            var builder = Random().WithReference($"REF-{i:D6}").WithSourceRowNumber(i + 1);
            configure?.Invoke(builder, i);
            results.Add(builder.Build());
        }
        return results;
    }
}