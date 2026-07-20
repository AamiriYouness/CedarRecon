using NetArchTest.Rules;
using Xunit;

namespace CedarRecon.Tests.Architecture;

/// <summary>
/// Enforces the project reference graph accepted in ADR-09. Each rule
/// checks assembly-level dependencies via project references — it cannot
/// catch a domain type smuggled into Indexing's public surface via a
/// generic constraint or an object-typed parameter (see
/// tools/CedarRecon.CodeAnalyzer for that class of check, and ADR-09's
/// "Relocation of the reference classifier" section for the concrete
/// ReferenceInterner/IndexedTransaction cases this project-level check
/// alone would have missed).
/// </summary>
public class DependencyRuleTests
{
    private const string Core = "CedarRecon.Core";
    private const string Indexing = "CedarRecon.Indexing";
    private const string Execution = "CedarRecon.Execution";
    private const string Classification = "CedarRecon.Classification";
    private const string Infrastructure = "CedarRecon.Infrastructure";
    private const string Reference = "CedarRecon.Reference";

    // ── Core ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Core_Should_Not_Depend_On_Anything_Else()
    {
        var result = Types.InAssembly(typeof(Core.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOnAny(Indexing, Execution, Classification, Infrastructure, Reference)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    // ── Indexing — the zero-domain-type project ─────────────────────────────

    [Fact]
    public void Indexing_Should_Not_Depend_On_Anything_Else()
    {
        var result = Types.InAssembly(typeof(Indexing.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOnAny(Core, Execution, Classification, Infrastructure, Reference)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Indexing_Should_Have_No_Dependency_On_Core_Namespace()
    {
        // Assembly-reference-level check for the zero-domain-type rule.
        // This catches "Indexing references the Core project" but not a
        // domain type appearing in a public signature via some other route
        // (object-typed param, generic constraint) — that class of leak is
        // tools/CedarRecon.CodeAnalyzer's job, not this test's.
        var result = Types.InAssembly(typeof(Indexing.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOn("CedarRecon.Core")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    // ── Execution ────────────────────────────────────────────────────────────

    [Fact]
    public void Execution_Should_Only_Depend_On_Core_And_Indexing()
    {
        var result = Types.InAssembly(typeof(Execution.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOnAny(Classification, Infrastructure, Reference)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    // ── Classification ───────────────────────────────────────────────────────

    [Fact]
    public void Classification_Should_Only_Depend_On_Core_And_Indexing()
    {
        var result = Types.InAssembly(typeof(Classification.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOnAny(Execution, Infrastructure, Reference)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Execution_And_Classification_Should_Not_Depend_On_Each_Other()
    {
        // Redundant with the two tests above individually, but stated
        // explicitly per ADR-09 ("neither calls the other") since this is
        // a named architectural property worth its own failure message,
        // not just an incidental consequence of the broader rules.
        var executionResult = Types.InAssembly(typeof(Execution.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOn(Classification)
            .GetResult();

        var classificationResult = Types.InAssembly(typeof(Classification.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOn(Execution)
            .GetResult();

        Assert.True(executionResult.IsSuccessful, Describe(executionResult));
        Assert.True(classificationResult.IsSuccessful, Describe(classificationResult));
    }

    // ── Infrastructure ───────────────────────────────────────────────────────

    [Fact]
    public void Infrastructure_Should_Only_Depend_On_Core()
    {
        var result = Types.InAssembly(typeof(Infrastructure.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOnAny(Indexing, Execution, Classification, Reference)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    // ── Reference (the equivalence oracle) ──────────────────────────────────

    [Fact]
    public void Reference_Should_Only_Depend_On_Core()
    {
        // CedarRecon.Reference (DictionaryBuilder, ExceptionClassifier) is
        // the equivalence oracle — Core + Logging.Abstractions only, no
        // test-framework dependency, so it can be shared by both
        // Tests.Unit and Tests.Performance without one test project
        // depending on another's test framework. See ADR-09, "Relocation
        // of the reference classifier."
        var result = Types.InAssembly(typeof(Reference.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOnAny(Indexing, Execution, Classification, Infrastructure)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void No_Src_Project_Should_Depend_On_Reference()
    {
        // The oracle exists to be tested against, never to be depended on
        // by production code — a src/ project referencing it would mean
        // shipping test/reference code in the product, or worse, production
        // logic silently relying on the deliberately-slow dictionary path.
        var assemblies = new[]
        {
            typeof(Core.AssemblyMarker).Assembly,
            typeof(Indexing.AssemblyMarker).Assembly,
            typeof(Execution.AssemblyMarker).Assembly,
            typeof(Classification.AssemblyMarker).Assembly,
            typeof(Infrastructure.AssemblyMarker).Assembly,
        };

        foreach (var assembly in assemblies)
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOn(Reference)
                .GetResult();

            Assert.True(result.IsSuccessful,
                $"{assembly.GetName().Name}: {Describe(result)}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Describe(TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : "Violating types: " + string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>());
}