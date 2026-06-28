using CedarRecon.Domain.ValueObjects;
using Shouldly;

namespace CedarRecon.Tests.Unit.Domain;

public sealed class MoneyTests
{
    [Theory]
    [InlineData(100.005, "EUR", 100.01)]  // AwayFromZero
    [InlineData(100.004, "USD", 100.00)]
    [InlineData(-100.005, "GBP", -100.01)] // AwayFromZero applies to negative too
    [InlineData(0, "EUR", 0)]
    public void Of_RoundsToTwoDecimalPlacesAwayFromZero(decimal input, string currency, decimal expected)
    {
        var money = Money.Of(input, currency);
        money.Amount.ShouldBe(expected);
    }

    [Theory]
    [InlineData("eur", "EUR")]
    [InlineData("USD", "USD")]
    [InlineData("  gbp  ", "GBP")]
    public void Of_NormalizesCurrencyToUppercase(string rawCurrency, string expectedCurrency)
    {
        var money = Money.Of(100m, rawCurrency);
        money.Currency.ShouldBe(expectedCurrency);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("US")]   // Not 3 chars
    [InlineData("USDA")] // Too long
    public void Of_InvalidCurrency_Throws(string currency)
    {
        Should.Throw<ArgumentException>(() => Money.Of(100m, currency));
    }

    [Fact]
    public void Add_SameCurrency_Correct()
    {
        var a = Money.Of(100.50m, "EUR");
        var b = Money.Of(200.25m, "EUR");
        var result = a.Add(b);
        result.Amount.ShouldBe(300.75m);
        result.Currency.ShouldBe("EUR");
    }

    [Fact]
    public void Add_DifferentCurrencies_Throws()
    {
        var eur = Money.Of(100m, "EUR");
        var usd = Money.Of(100m, "USD");
        Should.Throw<InvalidOperationException>(() => eur.Add(usd));
    }

    [Theory]
    [InlineData(100, 100, 0.01, true)]   // Within absolute tolerance
    [InlineData(100, 100.02, 0.01, false)]    // Outside absolute tolerance
    [InlineData(1000, 1000, 0.01, true)]      // Exact match
    public void IsWithinAbsoluteTolerance_Correct(
        decimal source, decimal target, decimal tolerance, bool expected)
    {
        var sourceMoney = Money.Of(source, "EUR");
        var targetMoney = Money.Of(target, "EUR");
        sourceMoney.IsWithinAbsoluteTolerance(targetMoney, tolerance).ShouldBe(expected);
    }

    [Theory]
    [InlineData(1000, 1005, 0.5, true)]   // Exactly 0.5% difference
    [InlineData(1000, 1006, 0.5, false)]  // 0.6% > 0.5%
    [InlineData(0, 0, 0.5, true)]         // Both zero
    public void IsWithinPercentageTolerance_Correct(
        decimal source, decimal target, decimal pct, bool expected)
    {
        var s = Money.Of(source, "EUR");
        var t = Money.Of(target, "EUR");
        s.IsWithinPercentageTolerance(t, pct).ShouldBe(expected);
    }

    [Fact]
    public void Money_Immutable_OriginalUnchangedAfterAdd()
    {
        var original = Money.Of(100m, "EUR");
        _ = original.Add(Money.Of(50m, "EUR"));
        original.Amount.ShouldBe(100m); // Original unchanged
    }
}