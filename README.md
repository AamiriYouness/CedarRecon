# CedarRecon

**High-performance, metadata-driven financial reconciliation platform**

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)
[![Status](https://img.shields.io/badge/Status-Active%20Development-yellow)]()
[![Version](https://img.shields.io/badge/Version-v0.9.x-orange)]()
[![.NET](https://img.shields.io/badge/.NET-10-purple)]()

</div>

---

> ⚠️ **CedarRecon is not yet production-ready.**
> The API is unstable and will change between versions until v1.0.0.
> Do not use in production financial systems without understanding this.

---

## What is CedarRecon?

CedarRecon is a high-performance financial reconciliation engine built in
.NET, inspired by database execution engines. It matches financial
transactions, classifies exceptions, and explains discrepancies — at scale,
with full audit traceability.

It is designed for teams who need reconciliation that is:

- **Fast** — columnar execution engine, histogram sort, cache-local scans
- **Explainable** — every exception has a classified reason and evidence
- **Flexible** — metadata-driven rules, no hardcoded business logic
- **Embeddable** — a library first, not a black-box service

## Who is it for?

CedarRecon is built for financial institutions and systems where
reconciliation is a core operational requirement:

- **Banks** — nostro/vostro, interbank, GL reconciliation
- **Insurers** — premium, claims, bordereaux reconciliation
- **Payment processors** — settlement, card, clearing reconciliation
- **Investment firms** — trade, position, custody reconciliation
- **Treasury operations** — cash, FX, collateral reconciliation
- **ERP systems** — AP/AR, intercompany reconciliation

## Current State (v0.9.x)

The execution engine core is complete and benchmarked:

| Component | Status |
|---|---|
| Matching engine (Exact, Fuzzy, Partial) | ✅ Complete |
| Hash-based indexing | ✅ Complete |
| Parallel execution | ✅ Complete |
| Exception classification (8 types) | ✅ Complete |
| Columnar execution engine | ✅ Complete |
| Histogram sort index builder | ✅ Complete |
| BenchmarkDotNet performance suite | ✅ Complete |

## Performance

Whole-method benchmark, exception classification at N=1M transactions:

| Engine | Mean | vs baseline |
|---|---|---|
| Dictionary-based (original) | 1,368 ms | 1.00x |
| Columnar + histogram sort | **646 ms** | **0.47x (2.1x faster)** |

See [docs/adrs/07-index-build-algorithms.md](docs/adrs/07-index-build-algorithms.md)
for the full investigation including all rejected approaches and negative
results.

## Roadmap

CedarRecon is planned across seven phases:

- **Phase 1** (v0.9–v1.0) — High-performance core ← *current*
- **Phase 2** (v1.1–v1.4) — Generic financial model, reference data, quality gates, ingestion
- **Phase 3** (v2.0–v2.3) — Metadata-driven reconciliation, match evidence, exception cases
- **Phase 4** (v3.0–v3.1) — Rule compiler, CedarReconQL DSL
- **Phase 5** (v4.0–v4.1) — Execution planner (database-inspired)
- **Phase 6** (v5.0–v5.5) — Enterprise platform (workflow, audit, scheduling, API, dashboard)
- **Phase 7** (v6.0) — Distributed execution

See [ROADMAP.md](ROADMAP.md) for the full version-by-version plan.

## Architecture

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the complete platform
pipeline, design principles, and data residency model.

## Documentation

| Document | Description |
|---|---|
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Platform pipeline and design principles |
| [ROADMAP.md](ROADMAP.md) | Version-by-version release plan |
| [05-exception-classification-engine.md](docs/adrs/05-exception-classification-engine.md) | Classification domain model and cascade |
| [06-columnar-execution-engine.md](docs/adrs/06-columnar-execution-engine.md) | Columnar execution model |
| [07-index-build-algorithms.md](docs/adrs/07-index-build-algorithms.md) | Index build investigation and results |
| [08-histogram-columnar-builder.md](docs/adrs/08-histogram-columnar-builder.md) | Histogram sort algorithm deep-dive |
| [ADR-columnar-classification-engine.md](docs/adrs/ADR-columnar-classification-engine.md) | Architecture decision record |

## License

Apache 2.0 — see [LICENSE](LICENSE).

---

Built by [Youness](https://github.com/youness) · Casablanca, Morocco
