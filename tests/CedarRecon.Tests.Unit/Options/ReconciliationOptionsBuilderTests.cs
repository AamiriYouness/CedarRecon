using CedarRecon.Application.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CedarRecon.Tests.Unit.Options;

/// <summary>
/// Tests for ReconciliationOptionsBuilder.
///
/// Sections:
///   A — Create() defaults
///   B — Fluent setters produce correct values
///   C — WithTolerance nested builder
///   D — FromConfig binds correctly, missing keys use defaults
///   E — From(existing) copies all values
///   F — Build() validation — invalid ranges throw
///   G — Immutability — Build() can be called multiple times
/// </summary>
public class ReconciliationOptionsBuilderTests
{
    [Fact]
    public void Create_Build_ReturnsProductionDefaults()
    {
        var opts = ReconciliationOptionsBuilder.Create().Build();

        opts.ExactMatchConfidence.ShouldBe(1.00m);
        opts.FuzzyMatchMaxConfidence.ShouldBe(0.95m);
        opts.FuzzyMatchMinConfidence.ShouldBe(0.70m);
        opts.PartialMatchMinConfidence.ShouldBe(0.60m);
        opts.DefaultToleranceRule.AbsoluteTolerance.ShouldBe(5.00m);
        opts.DefaultToleranceRule.PercentageTolerance.ShouldBe(0.50m);
        opts.DefaultToleranceRule.DateWindowDays.ShouldBe(3);
    }

    [Fact]
    public void WithExactMatchConfidence_SetsValue()
    {
        var opts = ReconciliationOptionsBuilder.Create()
            .WithExactMatchConfidence(0.99m)
            .Build();

        opts.ExactMatchConfidence.ShouldBe(0.99m);
    }

    [Fact]
    public void WithFuzzyConfidence_SetsBothBounds()
    {
        var opts = ReconciliationOptionsBuilder.Create()
            .WithFuzzyConfidence(min: 0.65m, max: 0.90m)
            .Build();

        opts.FuzzyMatchMinConfidence.ShouldBe(0.65m);
        opts.FuzzyMatchMaxConfidence.ShouldBe(0.90m);
    }

    [Fact]
    public void WithPartialMatchMinConfidence_SetsValue()
    {
        var opts = ReconciliationOptionsBuilder.Create()
            .WithPartialMatchMinConfidence(0.55m)
            .Build();

        opts.PartialMatchMinConfidence.ShouldBe(0.55m);
    }

    [Fact]
    public void FluentCalls_AreChainable_ReturnSameBuilder()
    {
        // Verify the builder pattern — each call returns the same instance
        var builder = ReconciliationOptionsBuilder.Create();

        var b1 = builder.WithExactMatchConfidence(1.0m);
        var b2 = b1.WithFuzzyConfidence(0.70m, 0.95m);
        var b3 = b2.WithPartialMatchMinConfidence(0.60m);

        b1.ShouldBeSameAs(builder);
        b2.ShouldBeSameAs(builder);
        b3.ShouldBeSameAs(builder);
    }

    [Fact]
    public void WithTolerance_Absolute_SetsAbsoluteTolerance()
    {
        var opts = ReconciliationOptionsBuilder.Create()
            .WithTolerance(t => t.Absolute(10m))
            .Build();

        opts.DefaultToleranceRule.AbsoluteTolerance.ShouldBe(10m);
    }

    [Fact]
    public void WithTolerance_Percentage_SetsPercentageTolerance()
    {
        var opts = ReconciliationOptionsBuilder.Create()
            .WithTolerance(t => t.Percentage(2.5m))
            .Build();

        opts.DefaultToleranceRule.PercentageTolerance.ShouldBe(2.5m);
    }

    [Fact]
    public void WithTolerance_DateWindow_SetsDateWindowDays()
    {
        var opts = ReconciliationOptionsBuilder.Create()
            .WithTolerance(t => t.DateWindow(7))
            .Build();

        opts.DefaultToleranceRule.DateWindowDays.ShouldBe(7);
    }

    [Fact]
    public void WithTolerance_Strict_SetsAllToZero()
    {
        var opts = ReconciliationOptionsBuilder.Create()
            .WithTolerance(t => t.Strict())
            .Build();

        opts.DefaultToleranceRule.AbsoluteTolerance.ShouldBe(0m);
        opts.DefaultToleranceRule.PercentageTolerance.ShouldBe(0m);
        opts.DefaultToleranceRule.DateWindowDays.ShouldBe(0);
    }

    [Fact]
    public void WithTolerance_AllThree_AllSetCorrectly()
    {
        var opts = ReconciliationOptionsBuilder.Create()
            .WithTolerance(t => t.Absolute(3m).Percentage(1m).DateWindow(5))
            .Build();

        opts.DefaultToleranceRule.AbsoluteTolerance.ShouldBe(3m);
        opts.DefaultToleranceRule.PercentageTolerance.ShouldBe(1m);
        opts.DefaultToleranceRule.DateWindowDays.ShouldBe(5);
    }

    [Fact]
    public void FromConfig_WithFullConfig_BindsAllValues()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CedarRecon:ExactMatchConfidence"] = "0.99",
            ["CedarRecon:FuzzyMatchMaxConfidence"] = "0.90",
            ["CedarRecon:FuzzyMatchMinConfidence"] = "0.65",
            ["CedarRecon:PartialMatchMinConfidence"] = "0.55",
            ["CedarRecon:Tolerance:AbsoluteTolerance"] = "10",
            ["CedarRecon:Tolerance:PercentageTolerance"] = "1.5",
            ["CedarRecon:Tolerance:DateWindowDays"] = "7",
        });

        var opts = ReconciliationOptionsBuilder
            .FromConfig(config.GetSection("CedarRecon"))
            .Build();

        opts.ExactMatchConfidence.ShouldBe(0.99m);
        opts.FuzzyMatchMaxConfidence.ShouldBe(0.90m);
        opts.FuzzyMatchMinConfidence.ShouldBe(0.65m);
        opts.PartialMatchMinConfidence.ShouldBe(0.55m);
        opts.DefaultToleranceRule.AbsoluteTolerance.ShouldBe(10m);
        opts.DefaultToleranceRule.PercentageTolerance.ShouldBe(1.5m);
        opts.DefaultToleranceRule.DateWindowDays.ShouldBe(7);
    }

    [Fact]
    public void FromConfig_WithEmptyConfig_UsesAllDefaults()
    {
        var config = BuildConfig(new Dictionary<string, string?>());

        var opts = ReconciliationOptionsBuilder
            .FromConfig(config.GetSection("CedarRecon"))
            .Build();

        // Must match the same defaults as Create().Build()
        var defaults = ReconciliationOptionsBuilder.Create().Build();

        opts.ExactMatchConfidence.ShouldBe(defaults.ExactMatchConfidence);
        opts.FuzzyMatchMaxConfidence.ShouldBe(defaults.FuzzyMatchMaxConfidence);
        opts.FuzzyMatchMinConfidence.ShouldBe(defaults.FuzzyMatchMinConfidence);
        opts.PartialMatchMinConfidence.ShouldBe(defaults.PartialMatchMinConfidence);
        opts.DefaultToleranceRule.AbsoluteTolerance.ShouldBe(defaults.DefaultToleranceRule.AbsoluteTolerance);
        opts.DefaultToleranceRule.DateWindowDays.ShouldBe(defaults.DefaultToleranceRule.DateWindowDays);
    }

    [Fact]
    public void FromConfig_WithPartialConfig_MissingKeysUseDefaults()
    {
        // Only tolerance is configured — confidence scores fall back to defaults
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CedarRecon:Tolerance:DateWindowDays"] = "10",
        });

        var opts = ReconciliationOptionsBuilder
            .FromConfig(config.GetSection("CedarRecon"))
            .Build();

        opts.ExactMatchConfidence.ShouldBe(1.00m);       // default
        opts.DefaultToleranceRule.DateWindowDays.ShouldBe(10); // from config
    }

    [Fact]
    public void From_CopiesAllValues_FromExistingOptions()
    {
        var original = ReconciliationOptionsBuilder.Create()
            .WithExactMatchConfidence(0.98m)
            .WithFuzzyConfidence(0.65m, 0.90m)
            .WithPartialMatchMinConfidence(0.55m)
            .WithTolerance(t => t.Absolute(8m).Percentage(1m).DateWindow(5))
            .Build();

        var copy = ReconciliationOptionsBuilder.From(original).Build();

        copy.ExactMatchConfidence.ShouldBe(original.ExactMatchConfidence);
        copy.FuzzyMatchMaxConfidence.ShouldBe(original.FuzzyMatchMaxConfidence);
        copy.FuzzyMatchMinConfidence.ShouldBe(original.FuzzyMatchMinConfidence);
        copy.PartialMatchMinConfidence.ShouldBe(original.PartialMatchMinConfidence);
        copy.DefaultToleranceRule.AbsoluteTolerance.ShouldBe(original.DefaultToleranceRule.AbsoluteTolerance);
        copy.DefaultToleranceRule.PercentageTolerance.ShouldBe(original.DefaultToleranceRule.PercentageTolerance);
        copy.DefaultToleranceRule.DateWindowDays.ShouldBe(original.DefaultToleranceRule.DateWindowDays);
    }

    [Fact]
    public void From_AllowsOverridingOneField_RestUnchanged()
    {
        var original = ReconciliationOptionsBuilder.Create().Build();

        var modified = ReconciliationOptionsBuilder.From(original)
            .WithTolerance(t => t.DateWindow(14))
            .Build();

        // Only DateWindowDays changed
        modified.DefaultToleranceRule.DateWindowDays.ShouldBe(14);

        // Everything else is identical to original
        modified.ExactMatchConfidence.ShouldBe(original.ExactMatchConfidence);
        modified.FuzzyMatchMinConfidence.ShouldBe(original.FuzzyMatchMinConfidence);
        modified.DefaultToleranceRule.AbsoluteTolerance.ShouldBe(original.DefaultToleranceRule.AbsoluteTolerance);
    }

    [Fact]
    public void Build_WhenExactConfidenceAboveOne_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            ReconciliationOptionsBuilder.Create()
                .WithExactMatchConfidence(1.01m)
                .Build());
    }

    [Fact]
    public void Build_WhenExactConfidenceNegative_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            ReconciliationOptionsBuilder.Create()
                .WithExactMatchConfidence(-0.01m)
                .Build());
    }

    [Fact]
    public void Build_WhenFuzzyMinExceedsMax_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            ReconciliationOptionsBuilder.Create()
                .WithFuzzyConfidence(min: 0.90m, max: 0.70m) // min > max
                .Build());
    }

    [Fact]
    public void Build_WhenFuzzyMinEqualsMax_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            ReconciliationOptionsBuilder.Create()
                .WithFuzzyConfidence(min: 0.80m, max: 0.80m) // equal
                .Build());
    }

    [Fact]
    public void Build_WhenAggregateConfidenceNegative_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            ReconciliationOptionsBuilder.Create()
                .WithPartialMatchMinConfidence(-0.1m)
                .Build());
    }

    [Fact]
    public void Build_WhenToleranceNegative_Throws()
    {
        // ToleranceRule constructor validates negative tolerances
        Should.Throw<ArgumentOutOfRangeException>(() =>
            ReconciliationOptionsBuilder.Create()
                .WithTolerance(t => t.Absolute(-1m))
                .Build());
    }

    [Fact]
    public void Build_WhenPercentageOver100_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            ReconciliationOptionsBuilder.Create()
                .WithTolerance(t => t.Percentage(101m))
                .Build());
    }

    [Fact]
    public void Build_CalledTwice_ReturnsTwoDistinctInstances()
    {
        var builder = ReconciliationOptionsBuilder.Create();

        var first = builder.Build();
        var second = builder.Build();

        // Different instances — not the same reference
        first.ShouldNotBeSameAs(second);

        // But same values
        first.ExactMatchConfidence.ShouldBe(second.ExactMatchConfidence);
        first.DefaultToleranceRule.DateWindowDays
            .ShouldBe(second.DefaultToleranceRule.DateWindowDays);
    }

    [Fact]
    public void Build_MutatingBuilderAfterBuild_DoesNotAffectPreviousInstance()
    {
        var builder = ReconciliationOptionsBuilder.Create();
        var first = builder.Build();

        // Change builder state after first build
        builder.WithTolerance(t => t.DateWindow(99));
        var second = builder.Build();

        // First instance must be unaffected
        first.DefaultToleranceRule.DateWindowDays.ShouldBe(3);   // original default
        second.DefaultToleranceRule.DateWindowDays.ShouldBe(99); // mutated
    }


    private static IConfiguration BuildConfig(
        Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
