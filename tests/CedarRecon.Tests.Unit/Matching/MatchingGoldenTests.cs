using System.Runtime.CompilerServices;
using System.Text.Json;
using CedarRecon.Core.Enums;
using CedarRecon.Reference;
using Shouldly;

namespace CedarRecon.Tests.Unit.Matching;

/// <summary>
/// Matching golden tests — no second "reference" matching implementation
/// exists (unlike classification's Dictionary/Columnar pair), so this is
/// regression protection: capture what HashMatchingEngine + the real
/// Exact/Fuzzy/Partial strategy pipeline produce today, fail if a future
/// change silently alters the match set.
///
/// Input comes from ScenarioBuilder.BuildRaw — raw source/target
/// transactions only, no pre-fabricated MatchedPair list. Matching is
/// genuinely exercised, not assumed.
/// </summary>
public class MatchingGoldenTests
{
    private static readonly JsonSerializerOptions GoldenJsonOptions = new() { WriteIndented = true };

    [Theory]
    [InlineData("small", 200, 1, false)]
    [InlineData("medium", 5_000, 2, false)]
    [InlineData("skewed", 3_000, 3, true)]
    public async Task Golden_MatchesCommittedExpectedOutput(string name, int n, int seed, bool useSkewed)
    {
        var (source, target) = useSkewed
            ? ScenarioBuilder.BuildRawSkewed(n, seed)
            : ScenarioBuilder.BuildRaw(n, seed);

        var matches = await MatchingTestHelper.RunMatching(source, target);
        var actual = GoldenMatchSerializer.ToComparable(matches);

        var path = Path.Combine(
            AppContext.BaseDirectory, "Matching", "Golden", $"{name}.expected.json");
        var expectedJson = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        var expected = JsonSerializer.Deserialize<List<GoldenMatch>>(expectedJson);

        actual.ShouldBe(expected);
    }

    /// <summary>
    /// Not a golden comparison — a sanity check that BuildRaw's MATCHABLE-*
    /// category (exact reference+amount on both sides) actually gets
    /// claimed by ExactMatchStrategy specifically, not just matched by
    /// something. Catches a regression where Fuzzy/Partial silently starts
    /// claiming candidates Exact should have won outright.
    /// </summary>
    [Fact]
    public async Task MatchableCandidates_AreClaimedByExactStrategy()
    {
        var (source, target) = ScenarioBuilder.BuildRaw(500, seed: 42);

        var matches = await MatchingTestHelper.RunMatching(source, target);

        var exactMatches = matches.Where(m =>
            m.Source.NormalizedReference.Value.StartsWith("MATCHABLE", StringComparison.Ordinal)).ToList();

        exactMatches.ShouldNotBeEmpty();
        exactMatches.ShouldAllBe(m => m.Strategy == MatchStrategy.Exact);
    }

    /// <summary>
    /// Same rationale as MatchableCandidates_AreClaimedByExactStrategy —
    /// confirms MISMATCH-* candidates (small amount delta, shared date,
    /// within default tolerance) are actually claimed by FuzzyMatchStrategy
    /// specifically, not silently falling through to unmatched. Note:
    /// StartsWith("MISMATCH", ...) not "MISMATCH-" — TransactionReference
    /// .FromRaw strips dashes during normalization, so the stored
    /// NormalizedReference.Value never contains one.
    /// </summary>
    [Fact]
    public async Task MismatchCandidates_AreClaimedByFuzzyStrategy()
    {
        var (source, target) = ScenarioBuilder.BuildRaw(500, seed: 42);

        var matches = await MatchingTestHelper.RunMatching(source, target);

        var fuzzyMatches = matches.Where(m =>
            m.Source.NormalizedReference.Value.StartsWith("MISMATCH", StringComparison.Ordinal)).ToList();

        fuzzyMatches.ShouldNotBeEmpty();
        fuzzyMatches.ShouldAllBe(m => m.Strategy == MatchStrategy.Fuzzy);
    }

    /// <summary>
    /// Helper to (re)generate golden files. Not a test — run manually via
    /// GENERATE_AllGoldenFiles below, inspect the diff, and only commit the
    /// regenerated file(s) as part of a PR that explains why the match set
    /// was deliberately expected to change. See MatchingGoldenTests class doc.
    ///
    /// Writes to the SOURCE tree (via CallerFilePath), not AppContext.BaseDirectory
    /// — the latter resolves to bin/Debug/net10.0/... (build output), which looks
    /// like it worked but writes somewhere git never sees. CallerFilePath captures
    /// this file's real compile-time source path, so GetGoldenDirectory() resolves
    /// correctly regardless of build configuration or working directory.
    /// </summary>
    private static async Task GenerateGoldenFile(
        string name, int n, int seed, bool useSkewed = false,
        [CallerFilePath] string sourceFilePath = "")
    {
        var (source, target) = useSkewed
            ? ScenarioBuilder.BuildRawSkewed(n, seed)
            : ScenarioBuilder.BuildRaw(n, seed);
        var matches = await MatchingTestHelper.RunMatching(source, target);
        var comparable = GoldenMatchSerializer.ToComparable(matches);
        var json = JsonSerializer.Serialize(comparable, GoldenJsonOptions);

        var goldenDir = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "Golden");
        Directory.CreateDirectory(goldenDir);

        var path = Path.Combine(goldenDir, $"{name}.expected.json");
        await File.WriteAllTextAsync(path, json);

        Console.WriteLine($"Wrote {matches.Count} matches to {path}");
    }

    /// <summary>
    /// Run this once (via IDE test runner or `dotnet test --filter`) to
    /// (re)generate every committed golden file, then delete or skip this
    /// method before committing — it's a generator, not a permanent test.
    /// Inspect each file's diff before committing; a golden file changing
    /// should always be a deliberate, reviewed act.
    /// </summary>
    [Fact(Skip = "Manual golden-file generation — run explicitly, not part of CI")]
    public async Task GENERATE_AllGoldenFiles()
    {
        await GenerateGoldenFile("small", 200, seed: 1);
        await GenerateGoldenFile("medium", 5_000, seed: 2);
        await GenerateGoldenFile("skewed", 3_000, seed: 3, useSkewed: true);
    }
}