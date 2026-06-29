using CedarRecon.Domain.ValueObjects;

namespace CedarRecon.Domain;

/// <summary>
/// Central configuration object threaded through the entire pipeline.
/// No hardcoded values exist anywhere in the codebase — all tunables live here.
/// Bind from appsettings.json via IOptions&lt;ReconciliationOptions&gt;.
/// </summary>
public class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";

    // ── Pipeline parallelism ──────────────────────────────────────────────────
    public int NormalizeParallelism { get; init; } = Environment.ProcessorCount;
    public int EnrichParallelism { get; init; } = Environment.ProcessorCount;
    public int ClassifyParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>BoundedCapacity for all TPL Dataflow blocks.</summary>
    public int BlockBoundedCapacity { get; init; } = 10_000;

    // ── Matching ──────────────────────────────────────────────────────────────
    public ToleranceRule DefaultToleranceRule { get; init; } = ToleranceRule.Standard;

    /// <summary>Minimum confidence score to consider a match valid (default 0.50).</summary>
    public decimal MinimumConfidenceThreshold { get; init; } = 0.50m;

    // ── Confidence score bands ─────────────────
    public decimal ExactMatchConfidence { get; init; } = 1.0m;
    public decimal FuzzyMatchMaxConfidence { get; init; } = 0.99m;
    public decimal FuzzyMatchMinConfidence { get; init; } = 0.70m;
    public decimal PartialMatchMaxConfidence { get; init; } = 0.69m;
    public decimal PartialMatchMinConfidence { get; init; } = 0.50m;

    // ── Ingestion ─────────────────────────────────────────────────────────────
    /// <summary>Maximum file size in bytes before FileTooLarge error (default 500 MB).</summary>
    public long MaxFileSizeBytes { get; init; } = 500L * 1024 * 1024;

    /// <summary>Maximum rows to buffer from IAsyncEnumerable before applying backpressure.</summary>
    public int IngestionBatchSize { get; init; } = 1_000;

    // ── Dead-letter / error handling ──────────────────────────────────────────
    public int MaxConsecutiveErrors { get; init; } = 100;

    /// <summary>If true, a single fatal file failure aborts the entire run. False = dead-letter and continue.</summary>
    public bool AbortOnFatalError { get; init; } = false;

    // ── Date handling ─────────────────────────────────────────────────────────
    /// <summary>Assumed timezone for dates without explicit offset (default UTC).</summary>
    public string DefaultTimezone { get; init; } = "UTC";

    // ── Reporting ─────────────────────────────────────────────────────────────
    public int MaxMatchedPairsPerPage { get; init; } = 100;
    public int MaxUnmatchedPerPage { get; init; } = 100;
}