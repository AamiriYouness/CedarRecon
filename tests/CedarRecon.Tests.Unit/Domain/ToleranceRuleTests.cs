using CedarRecon.Domain.ValueObjects;
using Shouldly;

namespace CedarRecon.Tests.Unit.Domain;

public sealed class ToleranceRuleTests
{
    [Fact]
    public void Constructor_NegativeAbsoluteTolerance_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new ToleranceRule(-0.01m, 0m, 0));
    }

    [Fact]
    public void Constructor_PercentageOver100_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new ToleranceRule(0m, 101m, 0));
    }

    [Fact]
    public void Constructor_NegativeDateWindow_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new ToleranceRule(0m, 0m, -1));
    }

    [Theory]
    [InlineData(100, 100.009, 0.01, 0, 0, true)]   // Within absolute
    [InlineData(100, 100.02, 0.01, 0, 0, false)]   // Outside absolute
    [InlineData(1000, 1004.9, 0, 0.5, 0, true)]    // Within percentage
    [InlineData(1000, 1005.1, 0, 0.5, 0, false)]   // Outside percentage
    public void IsAmountWithinTolerance_Correct(
        decimal source, decimal target,
        decimal absT, decimal pctT, int days, bool expected)
    {
        var rule = new ToleranceRule(absT, pctT, days);
        rule.IsAmountWithinTolerance(source, target).ShouldBe(expected);
    }

    [Theory]
    [InlineData("2024-01-01", "2024-01-03", 2, true)]   // Within 2-day window
    [InlineData("2024-01-01", "2024-01-04", 2, false)]  // Outside 2-day window
    [InlineData("2024-01-01", "2024-01-01", 0, true)]   // Same day, zero window
    public void IsDateWithinTolerance_Correct(
        string source, string target, int days, bool expected)
    {
        var rule = new ToleranceRule(0m, 0m, days);
        var s = DateTimeOffset.Parse(source);
        var t = DateTimeOffset.Parse(target);
        rule.IsDateWithinTolerance(s, t).ShouldBe(expected);
    }

    [Fact]
    public void Strict_RejectsAnyDifference()
    {
        var rule = ToleranceRule.Strict;
        rule.IsAmountWithinTolerance(100m, 100.001m).ShouldBeFalse();
    }

    [Fact]
    public void Standard_AllowsOnecentDifference()
    {
        var rule = ToleranceRule.Standard;
        rule.IsAmountWithinTolerance(100m, 100.01m).ShouldBeTrue();
        rule.IsAmountWithinTolerance(100m, 101m).ShouldBeFalse();
    }
}