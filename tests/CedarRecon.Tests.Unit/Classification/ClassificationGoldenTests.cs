using System.Runtime.CompilerServices;
using System.Text.Json;
using CedarRecon.Classification;
using CedarRecon.Reference;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CedarRecon.Tests.Unit.Classification;

/// <summary>
/// Classification golden tests — hardens the existing Dictionary/Columnar
/// equivalence suite (ExceptionClassifierEquivalenceTests) with committed
/// golden files. Uses ScenarioBuilder.Build (synthetic matched pairs, not
/// BuildRaw) deliberately — classification logic is under test here, not
/// matching; feeding it pre-declared matched pairs isolates that.
/// </summary>
public class ClassificationGoldenTests
{
    private static readonly JsonSerializerOptions GoldenJsonOptions = new() { WriteIndented = true };

    [Theory]
    [InlineData("small", 200, 1)]
    [InlineData("medium", 5_000, 2)]
    public async Task Golden_MatchesCommittedExpectedOutput(string name, int n, int seed)
    {
        var (source, target, matched) = ScenarioBuilder.Build(n, seed);

        var results = Columnar().Classify(source, target, matched);
        var actual = GoldenClassificationSerializer.ToComparable(results, ScenarioBuilder.BaseDate);

        var path = Path.Combine(AppContext.BaseDirectory, "Classification", "Golden", $"{name}.expected.json");
        var expectedJson = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        var expected = JsonSerializer.Deserialize<List<GoldenUnmatchedTransaction>>(expectedJson);

        actual.ShouldBe(expected);
    }

    private static ColumnarExceptionClassifier Columnar() =>
        new(NullLogger<ColumnarExceptionClassifier>.Instance);

    /// <summary>
    /// Run this once (via IDE test runner or `dotnet test --filter`) to
    /// (re)generate every committed golden file, then re-add the Skip
    /// attribute before committing — it's a generator, not a permanent
    /// test. Inspect each file's diff before committing; a golden file
    /// changing should always be a deliberate, reviewed act.
    /// </summary>
    [Fact(Skip = "Manual golden-file generation — run explicitly, not part of CI")]
    public async Task GENERATE_AllGoldenFiles()
    {
        await GenerateGoldenFile("small", 200, seed: 1);
        await GenerateGoldenFile("medium", 5_000, seed: 2);
    }

    private static async Task GenerateGoldenFile(
        string name, int n, int seed, [CallerFilePath] string sourceFilePath = "")
    {
        var (source, target, matched) = ScenarioBuilder.Build(n, seed);
        var results = Columnar().Classify(source, target, matched);
        var comparable = GoldenClassificationSerializer.ToComparable(results, ScenarioBuilder.BaseDate);
        var json = JsonSerializer.Serialize(comparable, GoldenJsonOptions);

        var goldenDir = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "Golden");
        Directory.CreateDirectory(goldenDir);

        var path = Path.Combine(goldenDir, $"{name}.expected.json");
        await File.WriteAllTextAsync(path, json);

        Console.WriteLine($"Wrote {results.Count} classified rows to {path}");
    }
}