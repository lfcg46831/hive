# HIVE

HIVE is an internal-first distributed runtime for organizations made of AI agents and people. This repository is currently an F0 engineering prototype, not a production-ready product.

## Project status

### Current — F0 technical prototype

The repository contains the F0 runtime through the F0.8 stabilization cut: a local Akka.NET cluster, PostgreSQL persistence, GitOps organization configuration, the vertical directive flow, and the isolated audit/export boundary. Start with the [F0 roadmap and exit criteria](docs/bible.html#phase-panel-f0), then use the [configuration guide](docs/configuration.md#run-locally-without-docker-compose) or the [Docker Compose runbook](docs/configuration.md#run-with-docker-compose).

F0.8 makes the solution ready to request a new authorized preflight or calibration; it does not satisfy the product-quality gate. After correcting `BUG-001`, the repeated local qualification and both real-provider smokes passed on the integrated functional identity; the closed [post-F0.8 preflight decision](evidence/preflight/us-f0-17-preflight-decision.v1.md#decision) is `eligible-to-request-calibration`. Calibration remains unexecuted and requires separate authorization, the latest measured candidate was [rejected for freeze](evidence/evaluation/bug-triage-hybrid-outcome-verifier30-v1/hybrid-outcome-verifier30-calibration-report.v1.md#decision), no new holdout is authorized, and F1a remains closed.

### Experimental — Evaluation Lab

The Evaluation Lab is disabled by default and lives in separate tooling that observes the runtime through a bounded public audit/export contract. Its reference manifest is prepared, not authorized for a corpus run. Existing evaluation-specific Compose overlays, plans, run ids, datasets, and reports are historical evidence rather than templates for new experiments. See the canonical [Evaluation Lab boundary](docs/bible.html#evaluation-lab) and its [experimental operating notes](docs/configuration.md#evaluation-lab-experimental).

### Planned — not implemented

[F1](docs/bible.html#phase-panel-f1), [F2](docs/bible.html#phase-panel-f2), and [F3](docs/bible.html#phase-panel-f3) remain roadmap. In particular, the operational React UI, human inbox and identity, production connectors, Kubernetes deployment, and strong multi-tenant product surface are not implemented by the current F0 cut.

## Documentation map

| Need | Source |
|---|---|
| Vision, durable contracts, ADRs, roadmap, and acceptance criteria | [Solution bible](docs/bible.html#vision) |
| Setup, configuration, runbooks, environment variables, and logging | [Configuration guide](docs/configuration.md) |
| Experimental outcomes and reproducibility records | [`evidence/`](evidence/) and the [artifact index](evidence/evaluation/artifact-index.v1.json) |
| What changed in an implementation task | Git commit history |

The README is a navigation and status summary, not a second source of product decisions. New durable decisions belong in the bible, operational instructions in the configuration guide, experimental results in evidence reports, and implementation narrative in commits. Historical plans, overlays, datasets, reports, and version-history entries remain unchanged.
