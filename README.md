# HIVE

HIVE is an internal-first distributed runtime for organizations made of AI agents and people. This repository is currently an F0 engineering prototype, not a production-ready product.

## Project status

### Current — F0 runtime and initial F1 slice

The repository contains the F0 runtime through the F0.8 stabilization cut: a local Akka.NET cluster, PostgreSQL persistence, GitOps organization configuration, the vertical directive flow, and the isolated audit/export boundary. It also contains the first F1 read-only organization slice through `US-F1-01-T10`: public REST snapshots, organization-scoped authorization, live-state projection, SignalR change notifications with REST/ETag fallback, the typed TypeScript client for that public surface verified against the published OpenAPI document, and a read-only React organogram view built on it — all in [`console/`](console/), operated per the [console guide](docs/configuration.md#console-web-application). Start with the [F0 roadmap and exit criteria](docs/bible.html#phase-panel-f0), then use the [configuration guide](docs/configuration.md#run-locally-without-docker-compose) or the [Docker Compose runbook](docs/configuration.md#run-with-docker-compose).

F0.8 makes the solution eligible for controlled measurement; it does not satisfy the product-quality gate. After the approved post-F0.8 preflight, the separately authorized three-run [calibration](evidence/evaluation/bug-triage-lab-v1/post-f0-8-calibration-report.v1.md#decision) preserved the frozen manifest but failed projection coverage and all quality thresholds except cost, so the result is `rejected-for-freeze-request`. The run ids are burned, no freeze or holdout is authorized, and F1a remains closed.

### Experimental — Evaluation Lab

The Evaluation Lab is disabled by default and lives in separate tooling that observes the runtime through a bounded public audit/export contract. Its reference manifest has one completed authorized calibration; that authorization and its run ids cannot be reused. Existing evaluation-specific Compose overlays, plans, run ids, datasets, and reports are historical evidence rather than templates for new experiments. See the canonical [Evaluation Lab boundary](docs/bible.html#evaluation-lab) and its [experimental operating notes](docs/configuration.md#evaluation-lab-experimental).

### Planned — not implemented

The remaining [F1](docs/bible.html#phase-panel-f1) work, [F2](docs/bible.html#phase-panel-f2), and [F3](docs/bible.html#phase-panel-f3) remain roadmap. In particular, the console's degraded-mode and filtering surfaces, the human inbox and identity, production connectors, Kubernetes deployment, and strong multi-tenant product surface are not implemented.

## Documentation map

| Need | Source |
|---|---|
| Vision, durable contracts, ADRs, roadmap, and acceptance criteria | [Solution bible](docs/bible.html#vision) |
| Setup, configuration, runbooks, environment variables, and logging | [Configuration guide](docs/configuration.md) |
| Experimental outcomes and reproducibility records | [`evidence/`](evidence/) and the [artifact index](evidence/evaluation/artifact-index.v1.json) |
| What changed in an implementation task | Git commit history |

The README is a navigation and status summary, not a second source of product decisions. New durable decisions belong in the bible, operational instructions in the configuration guide, experimental results in evidence reports, and implementation narrative in commits. Historical plans, overlays, datasets, reports, and version-history entries remain unchanged.
