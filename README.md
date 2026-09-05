# HIVE

HIVE is an internal-first distributed runtime for organizations made of AI agents and people. This repository is an engineering prototype with an F0 runtime and F1 capabilities under development; it is not production-ready.

## Project status

### Current — F0 runtime and F1 implementation in progress

Implemented surfaces include:

- The F0 runtime: Akka.NET clustering, PostgreSQL persistence, GitOps organization configuration, directive execution, and the isolated audit/export boundary. See the [F0 roadmap](docs/bible.html#phase-panel-f0) and [local setup](docs/configuration.md#run-locally-without-docker-compose) or [Docker Compose runbook](docs/configuration.md#run-with-docker-compose).
- The React/TypeScript organogram and human inbox, backed by public REST/SignalR APIs, with message content, responses, approvals, and API/contract and frontend test suites. See the [console guide](docs/configuration.md#console-web-application).
- Occupant-channel email components for [SMTP delivery](docs/configuration.md#outbound-occupant-email-smtp) and [IMAP intake](docs/configuration.md#inbound-occupant-email-imap), plus the opt-in [GitHub Issues connector plugin](docs/configuration.md#github-issues-connector-instances). Email delivery and admission still depend on active identity bindings; the identity implementation remains outstanding.
- [AI gateway](docs/configuration.md#ai-gateway) resilience, including retries, provider fallback, admission limits, and circuit breakers.

**Validation:** automated coverage exists for these engineering surfaces, including inbox flows, email components, GitHub integration, and gateway resilience. That coverage does not establish product quality or market value: the historical F0 quality gate remains **no-go**, and the [post-F0.8 calibration](evidence/evaluation/bug-triage-lab-v1/post-f0-8-calibration-report.v1.md#decision) was rejected for a freeze request. Recorded outcomes belong to their evaluated revisions; they do not certify the current checkout.

**Decision to advance:** [bible §1.5, rev 2.88](docs/bible.html#vision) authorizes the full F1 (F1a and F1b) as instrumentation to observe and improve real work. It preserves the historical no-go and the burned `holdout-v2` corpus. F1 completion still depends on the technical and market gates in the [F1 roadmap](docs/bible.html#phase-panel-f1); engineering progress does not satisfy those gates or authorize a new evaluation run.

### Experimental — Evaluation Lab

The Evaluation Lab is disabled by default and lives in separate tooling that observes the runtime through a bounded public audit/export contract. Its reference manifest has one completed authorized calibration; that authorization and its run ids cannot be reused. Existing evaluation-specific Compose overlays, plans, run ids, datasets, and reports are historical evidence rather than templates for new experiments. See the canonical [Evaluation Lab boundary](docs/bible.html#evaluation-lab) and its [experimental operating notes](docs/configuration.md#evaluation-lab-experimental).

### Planned — not implemented

Remaining [F1](docs/bible.html#phase-panel-f1) work includes real OIDC identity and occupation bindings (`US-F1-09`), data classification/minimization and privacy controls (`US-F1-08`), and continuous evaluation in the real loop (`US-F1-10`). The inbox currently uses a static configured person credential. See the roadmap for the remaining F1 scope and exit criteria.

[F2](docs/bible.html#phase-panel-f2) includes Kubernetes deployment; MCP interoperability is a future F2 commitment, with its own story still to be specified. [F3](docs/bible.html#phase-panel-f3), including the strong multi-tenant product surface, is a post-validation backlog without a delivery commitment. The governing value and market gates remain in [bible §1.5](docs/bible.html#vision).

## Documentation map

| Need | Source |
|---|---|
| Vision, durable contracts, ADRs, roadmap, and acceptance criteria | [Solution bible](docs/bible.html#vision) |
| Setup, configuration, runbooks, environment variables, and logging | [Configuration guide](docs/configuration.md) |
| Experimental outcomes and reproducibility records | [`evidence/`](evidence/) and the [artifact index](evidence/evaluation/artifact-index.v1.json) |
| What changed in an implementation task | Git commit history |

The README is a navigation and status summary, not a second source of product decisions. New durable decisions belong in the bible, operational instructions in the configuration guide, experimental results in evidence reports, and implementation narrative in commits. Historical plans, overlays, datasets, reports, and version-history entries remain unchanged.
