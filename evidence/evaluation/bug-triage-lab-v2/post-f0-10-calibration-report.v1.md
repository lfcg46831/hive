# Post-F0.10 calibration report v1

Date: 2026-08-01  
Scope: `US-F0-21-T01`  
Decision: `rejected-for-freeze-request`

## Authorization and frozen identity

The user explicitly authorized the calibration request prepared by
`US-F0-20-T03`. The three runs were executed sequentially as one block without
intermediate dataset inspection, tuning, retry, or interruption. No holdout was
executed or inspected.

| Input | Identity |
| --- | --- |
| Preflight decision | `evidence/preflight/us-f0-20-preflight-decision.v1.md` |
| Preflight decision SHA-256 | `8d56e5748fca9e2edb08cf47aba0fa79de292a01eca852e96d7d4739dd0ee036` |
| Reviewed functional commit | `b484551bf3f8dbf2f59a13b94638e4a2dea72f41` |
| Reviewed functional tree | `f121af94be0f76cab82712a6658fd13c7c300f7a` |
| Experiment manifest | `config/experiments/bug-triage-lab-v2/experiment.v1.json` |
| Manifest SHA-256 | `a51e80a8492cfa27413c9d6ee5d334f88c695aace7dbe45c1864b2f4a90c19f0` |
| Effective configuration SHA-256 | `cabdca7c16a693f707d1406932587947b2ae55897a71d084105e506f4507ea2f` |
| Provider/model | `openai` / `gpt-5-mini-2025-08-07` |
| Output mode / main timeout / output ceiling | `json-schema` / 60,000 ms / 8,192 tokens |
| Execution budget / iterations / runner timeout | 90,000 ms / 4 / 120,000 ms |
| Outcome mode / verifier ceiling | `enforcement` / 30,000 ms |

Before the first corpus case, the manifest/artifact tests passed 19/19,
preparation reproduced both configuration identities, the generic Compose
adapter rendered successfully, all three run ids and outputs were absent, and
the API and PostgreSQL containers were healthy. The functional paths remained
identical to the reviewed commit.

## Immutable execution block

| Run | Start UTC | End UTC | Exit |
| --- | --- | --- | ---: |
| `post-f0-10-calibration-001` | `2026-08-01T08:12:29Z` | `2026-08-01T08:32:33Z` | 1 |
| `post-f0-10-calibration-002` | `2026-08-01T08:32:33Z` | `2026-08-01T08:49:10Z` | 1 |
| `post-f0-10-calibration-003` | `2026-08-01T08:49:10Z` | `2026-08-01T09:01:35Z` | 1 |

Each command used:

```powershell
dotnet run --project src/Hive.Evaluation.Tooling --no-build --no-restore -- `
  evaluate --run-id <fixed-run-id> --base-url http://localhost:8080 `
  --manifest config/experiments/bug-triage-lab-v2/experiment.v1.json `
  --output artifacts/evaluation/<fixed-run-id>.json
```

Each command returned 1 because its closed `run_analysis` status was
`not-ready` with `projection-incomplete`. The script continued directly to the
next fixed id without opening a dataset or changing the host/configuration.

## Completeness and effective configuration

| Run | Configuration | Terminal | Explicit cost state | Scoreable projection | Terminal codes |
| --- | --- | ---: | ---: | ---: | --- |
| `001` | validated | 30/30 | 30/30 | 28/30 | 29 `result-emitted`, 1 `timeout` |
| `002` | validated | 30/30 | 30/30 | 27/30 | 29 `result-emitted`, 1 `ai-output-invalid` |
| `003` | validated | 30/30 | 30/30 | 27/30 | 30 `result-emitted` |

Provider, model, output mode, timeout ceilings, output ceiling, continuation
budget, outcome policy, and verifier ceiling matched the manifest in all 90
cases. Projection failures were two `projection-missing` in `001`, three
`projection-missing` in `002`, and two `projection-missing` plus one
`projection-invalid` in `003`. The only invalid-output diagnostic was one
`contradictory-combination` at
`outcome_proposal.proposal.proposed_intent` in `002`.

## Frozen quality and cost gates

| Metric (threshold) | `001` | `002` | `003` | Result |
| --- | ---: | ---: | ---: | --- |
| Decision agreement (≥ 0.90) | 0.700000 | 0.800000 | 0.700000 | FAIL |
| Escalation recall (≥ 0.90) | 0.666667 | 0.333333 | 0.333333 | FAIL |
| Severity (≥ 0.60) | 0.633333 | 0.583333 | 0.666667 | FAIL |
| Missing information (≥ 0.35) | 0.297778 | 0.284074 | 0.330307 | FAIL |
| Full-corpus macro (≥ 0.65) | 0.535889 | 0.543593 | 0.558941 | FAIL |
| Average known cost (≤ USD 0.02) | 0.008287 | 0.008525 | 0.008542 | PASS |

The decision matrices were respectively `TP=4/FN=2/TN=17/FP=6` plus one
unclassified case, `TP=2/FN=4/TN=22/FP=1` plus one unclassified case, and
`TP=2/FN=4/TN=19/FP=5`. Known total cost was USD 0.240333 over 29 available
observations, USD 0.255742 over 30, and USD 0.256254 over 30. Gateway latency
p95 was 53,328 ms, 44,350 ms, and 32,981 ms; only `001` had one right-censored
observation.

Outcome-resolution coverage was 29/30, 29/30, and 30/30. The verifier ran 20,
27, and 25 times; undetermined results numbered 1, 1, and 2; overrides numbered
1, 1, and 2; and verifier latency p95 was 12,006 ms, 12,866 ms, and 8,384 ms.
These diagnostics do not change the frozen coverage or quality decision.

## Content-addressed raw artifacts

Publication timestamp: `2026-08-01T09:03:07Z`  
Retain until: `2027-08-01T09:03:07Z`

| Run | Exact-byte SHA-256 | Bytes | Location |
| --- | --- | ---: | --- |
| `001` | `ac5e4c511b23da9d3c3ec7a62b3c582806322c6cbf83e3932d35e52d92a46700` | 215,792 | `file:///C:/Users/luis_/Documents/hive-evaluation-artifacts/sha256/ac/ac5e4c511b23da9d3c3ec7a62b3c582806322c6cbf83e3932d35e52d92a46700.json` |
| `002` | `fff5178929011c405c3f9fb2d7cb3b7a865a759414ec59d9895acfcbfb44a08f` | 220,399 | `file:///C:/Users/luis_/Documents/hive-evaluation-artifacts/sha256/ff/fff5178929011c405c3f9fb2d7cb3b7a865a759414ec59d9895acfcbfb44a08f.json` |
| `003` | `034bb23bad4db402ae0a49f518ec66c927769822178aea0ae3effae633b6bceb` | 221,788 | `file:///C:/Users/luis_/Documents/hive-evaluation-artifacts/sha256/03/034bb23bad4db402ae0a49f518ec66c927769822178aea0ae3effae633b6bceb.json` |

The exact raw bytes are published without transformation to the external
user-protected NTFS filesystem store above. The tracked artifact index owns the
same hashes, sizes, retention, manifest reference, and this report reference.

## Decision

`rejected-for-freeze-request`

All three runs fail the mandatory 30/30 scoreable-projection gate. All also
fail decision agreement, escalation recall, missing-information, and the
full-corpus macro threshold; `002` additionally fails severity. Cost passes in
all three but cannot compensate for coverage or quality. This result does not
authorize a freeze, another calibration, a holdout, go/no-go, configuration
promotion, or reopening F1a.

## Evidence minimization

This report and the tracked index contain no credential, prompt, provider
output, reasoning, corpus content, organizational content, or provider error
body.
