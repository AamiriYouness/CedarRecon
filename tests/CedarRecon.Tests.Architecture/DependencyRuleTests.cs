using NetArchTest.Rules;

namespace CedarRecon.Tests.Architecture;

public class DependencyRuleTests
{
    private const string Core = "CedarRecon.Core";
    private const string Indexing = "CedarRecon.Indexing";
    private const string Execution = "CedarRecon.Execution";
    private const string Classification = "CedarRecon.Classification";
    private const string Infrastructure = "CedarRecon.Infrastructure";

    [Fact]
    public void Core_Should_Not_Depend_On_Anything_Else()
    {
        var result = Types.InAssembly(typeof(Core.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOnAny(Indexing, Execution, Classification, Infrastructure)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Indexing_Should_Not_Depend_On_Anything_Else()
    {
        var result = Types.InAssembly(typeof(Indexing.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOnAny(Core, Execution, Classification, Infrastructure)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Indexing_Should_Have_No_Domain_Types_In_Public_Surface()
    {
        // Belt-and-suspenders for the "primitives only" rule beyond assembly refs:
        // catches the ColumnarIndexBuilder-style leak (a using that slips back in later)
        // by asserting no type in Indexing lives in a Core-shaped namespace.
        var result = Types.InAssembly(typeof(Indexing.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOnAny("CedarRecon.Domain") // catch stragglers if anything still refs old namespace during migration
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Execution_Should_Only_Depend_On_Core_And_Indexing()
    {
        var result = Types.InAssembly(typeof(Execution.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOnAny(Classification, Infrastructure)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Classification_Should_Only_Depend_On_Core_And_Indexing()
    {
        var result = Types.InAssembly(typeof(Classification.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOnAny(Execution, Infrastructure)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Infrastructure_Should_Only_Depend_On_Core()
    {
        var result = Types.InAssembly(typeof(Infrastructure.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOnAny(Indexing, Execution, Classification)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : "Violating types: " + string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>());
}
