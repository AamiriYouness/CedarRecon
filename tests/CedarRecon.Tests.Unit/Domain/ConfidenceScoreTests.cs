using CedarRecon.Domain.ValueObjects;
using Shouldly;

namespace CedarRecon.Tests.Unit.Domain;

public sealed class ConfidenceScoreTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(0.7777)]
    public void Of_ValidRange_Creates(decimal value)
    {
        var score = ConfidenceScore.Of(value);
        score.Value.ShouldBeInRange(0.0m, 1.0m);
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(1.001)]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    public void Of_OutsideRange_Throws(decimal value)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ConfidenceScore.Of(value));
    }

    [Fact]
    public void Exact_IsFullConfidence()
    {
        ConfidenceScore.Exact.Value.ShouldBe(1.0m);
    }

    [Fact]
    public void FuzzyMin_IsSeventyPercent()
    {
        ConfidenceScore.FuzzyMin.Value.ShouldBe(0.70m);
    }

    [Fact]
    public void HighConfidence_CorrectlyClassifies()
    {
        ConfidenceScore.Of(0.95m).IsHighConfidence.ShouldBeTrue();
        ConfidenceScore.Of(0.89m).IsMediumConfidence.ShouldBeTrue();
        ConfidenceScore.Of(0.60m).IsLowConfidence.ShouldBeTrue();
    }

    [Fact]
    public void Comparison_Operators_Work()
    {
        var low = ConfidenceScore.Of(0.5m);
        var high = ConfidenceScore.Of(0.9m);
        (low < high).ShouldBeTrue();
        (high <= low).ShouldBeFalse();
    }
}