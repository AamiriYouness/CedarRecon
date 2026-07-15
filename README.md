<div align="center">
  <img src="docs/assets/logo.svg" alt="CedarRecon" width="140" />
  <h1>CedarRecon</h1>
  <p>
    <a href="LICENSE"><img src="https://img.shields.io/badge/License-Apache%202.0-0F6B4E.svg" alt="License" /></a>
    <img src="https://img.shields.io/badge/Status-Active%20Development-B8F36B" alt="Status" />
    <img src="https://img.shields.io/badge/Version-v0.9.x-87D7B0" alt="Version" />
    <img src="https://img.shields.io/badge/.NET-10-0F6B4E" alt=".NET 10" />
    <a href="https://github.com/AamiriYouness/CedarRecon/actions/workflows/ci.yml"><img src="https://github.com/AamiriYouness/CedarRecon/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
    <a href="https://codecov.io/gh/AamiriYouness/CedarRecon"><img src="https://codecov.io/gh/AamiriYouness/CedarRecon/branch/main/graph/badge.svg" alt="codecov" /></a>
  </p>
</div>

High-performance financial reconciliation execution engine — columnar
execution, cost-based planner, ReconQL DSL, composable matching
strategies. Built for banks, insurers, payment processors, and treasury
operations.

CedarRecon is engineered like an analytical query engine (DuckDB, Arrow,
Velox), not like a CRUD application: struct-of-arrays batches, histogram
sort, reference interning, lock-free match claiming, and a strict split
between match logic (ReconQL), deployment configuration (Job Definitions),
and execution.

## Performance

Measured with BenchmarkDotNet on a fixed workload distribution
(60% 1:1, 15% duplicates, 10% splits, 10% consolidations, 5% mismatches).
Every number below is reproducible from the committed benchmark suite.

| Measurement (N = 1M)        | Dictionary baseline | Columnar engine | Delta       |
|-----------------------------|---------------------|-----------------|-------------|
| Whole-method classification | 1,368 ms            | 646 ms          | 2.1x faster |
| MismatchScan                | 12.57 µs/1K         | 4.54 µs/1K      | −64%        |
| MissingSweep                | 2.96 µs/1K          | 1.45 µs/1K      | −51%        |
| IndexBuild                  | 395 µs/1K           | 296 µs/1K       | −25%        |

We also publish what **didn't** work — FrozenDictionary (−41%),
index-sort, scatter-sort, ScaledLong modulo arithmetic (3.36x slower) —
because negative results with numbers are worth more than positive
results without them. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#7-benchmark-discipline).

## How it works

```
Job Definition (YAML)          deployment: sources, mapping, tolerances
        |
        v  parameter injection
ReconQL (.reconql)             match logic only
        |
        v  lexer -> parser -> AST -> semantic analysis
Logical Plan
        |
        v  statistics + cost model
Physical Plan
        |
        v
Indexing  ->  Execution  ->  Classification  ->  Output
(columnar)    (matching)     (8-phase cascade)
```

The same `.reconql` file runs unchanged across dev, UAT, and production —
only the Job Definition changes. Tolerances are parameters injected at
plan time (`$tolerance.amount`), never constants in match logic.

Full details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)

## Current status — v0.9.0 (Engine Core)

Working today:

- Columnar classification engine: 8-phase exception cascade, no LINQ on
  hot paths, byte[] processing state
- Matching strategies: Exact, Fuzzy, Partial — composable pipeline over
  an O(1) hash engine with CAS-based claim resolution
- Exact money via minor units (scale 10^4)
- 28 equivalence tests proving the columnar path matches the reference
  implementation
- Benchmark suite with fixed distribution and regression thresholds

In progress for the v0.9.0 release: monorepo restructure, golden-file
regression suite, CI benchmark gates, Docker image. Track it in the
[v0.9.0 milestone](https://github.com/AamiriYouness/CedarRecon/milestones).

## Quick start

> A Docker image (`ghcr.io/aamiriyouness/cedarrecon`) ships with the
> v0.9.0 release. Until then, build from source:

```bash
git clone https://github.com/AamiriYouness/CedarRecon.git
cd CedarRecon
dotnet build
dotnet test                                   # equivalence + unit tests
dotnet run -c Release --project tests/CedarRecon.Indexing.Tests -- --filter '*Benchmark*'
```

Requires the .NET 10 SDK.

## Roadmap

| Version | Theme                                                    |
|---------|----------------------------------------------------------|
| v0.9.0  | Engine core (current)                                    |
| v1.0.0  | Cost-based planner, ReconQL v1, Job Definitions          |
| v1.1.0  | Generic record model                                     |
| v1.2.0  | Ingestion: MT940, CAMT.053, CSV, Excel                   |
| v1.3.0  | Quality gates: validation, dead letter queue             |
| v2.0.0  | Metadata-driven platform, ReconQL v2                     |
| v3.0.0  | Enterprise: workflow, audit, API, RBAC                   |
| v4.0.0  | Distributed execution                                    |
| v5.0.0  | ML-assisted matching                                     |

Details per version: [docs/ROADMAP.md](docs/ROADMAP.md)

## Design commitments

- **Deterministic.** Same inputs, same plan, same output. Financial
  reconciliation is auditable or it is nothing.
- **Measured.** No optimization merges without BenchmarkDotNet evidence;
  a ≥15% regression fails CI.
- **Data stays home.** The engine runs inside your infrastructure.
  No financial data leaves it, ever.

## License

Apache-2.0
