# Post-F0.8 calibration report v1

Date: 2026-07-31  
Scope: `US-F0-18-T01`  
Decision: `rejected-for-freeze-request`

## Authorization and frozen identity

The user explicitly authorized the calibration request prepared by
`US-F0-17-T03`. The three runs were executed sequentially as one block without
intermediate dataset inspection, tuning, retry, or interruption. No holdout was
executed or inspected.

| Input | Identity |
| --- | --- |
| Preflight decision | `evidence/preflight/us-f0-17-preflight-decision.v1.md` |
| Preflight decision SHA-256 | `6548c4f8f8aec41d690fdf4055fd9792312bc7bf97c8c42185f3725c29adf9fa` |
| Reviewed functional commit | `ba634f7037c2f9fe767175452bcddacf4d4246dc` |
| Experiment manifest | `config/experiments/bug-triage-lab-v1/experiment.v1.json` |
| Manifest SHA-256 | `e3ee9c5911129395b34fd68611088206eaa33bfa56c94e9a6264793c6357697d` |
| Effective configuration SHA-256 | `e0e0389bbb391d13fb8f8f5bfb47f80baf417b1bb02e3310bb857eeccd5dfe0d` |
| Provider/model | `openai` / `gpt-5-mini-2025-08-07` |
| Output mode / main timeout / output ceiling | `json-schema` / 60,000 ms / 8,192 tokens |
| Outcome mode / verifier ceiling | `enforcement` / 30,000 ms |

Before the first corpus case, manifest/artifact tests passed 16/16, preparation
reproduced both configuration identities, the generic Compose adapter rendered
successfully, and all three run ids and outputs were absent. `BUG-002` was found
and completed at the image-build gate by adding the two already referenced
project manifests to the Docker restore layer. It changed no runtime source,
manifest, organization, prompt, provider/model, limit, policy, corpus, baseline,
rubric, or threshold. The API image then built and the experimental host was
healthy before the block started.

## Immutable execution block

| Run | Start UTC | End UTC | Exit |
| --- | --- | --- | --- |
| `post-f0-8-calibration-001` | `2026-07-31T08:23:11Z` | `2026-07-31T08:49:13Z` | 1 |
| `post-f0-8-calibration-002` | `2026-07-31T08:49:13Z` | `2026-07-31T09:13:18Z` | 1 |
| `post-f0-8-calibration-003` | `2026-07-31T09:13:18Z` | `2026-07-31T09:38:52Z` | 1 |

Each command used:

```powershell
dotnet run --project src/Hive.Evaluation.Tooling --no-build --no-restore -- `
  evaluate --run-id <fixed-run-id> --base-url http://localhost:8080 `
  --manifest config/experiments/bug-triage-lab-v1/experiment.v1.json `
  --output artifacts/evaluation/<fixed-run-id>.json
```

## Completeness and effective configuration

| Run | Configuration | Terminal | Explicit cost state | Scoreable projection | Terminal codes |
| --- | --- | ---: | ---: | ---: | --- |
| `001` | validated | 30/30 | 30/30 | 18/30 | 19 `result-emitted`, 11 `timeout` |
| `002` | validated | 30/30 | 30/30 | 18/30 | 20 `result-emitted`, 10 `timeout` |
| `003` | validated | 30/30 | 30/30 | 19/30 | 20 `result-emitted`, 10 `timeout` |

Provider, model, output mode, timeout ceilings, output ceiling, continuation
budget, outcome policy, and verifier ceiling matched the manifest in all 90
cases. Projection failures were 12 `projection-missing` in `001`, 12
`projection-missing` in `002`, and 10 `projection-missing` plus one
`projection-invalid` in `003`. No invalid provider-output diagnostic was
recorded.

## Frozen quality and cost gates

| Metric (threshold) | `001` | `002` | `003` | Result |
| --- | ---: | ---: | ---: | --- |
| Decision agreement (≥ 0.90) | 0.466667 | 0.466667 | 0.500000 | FAIL |
| Escalation recall (≥ 0.90) | 0.333333 | 0.333333 | 0.000000 | FAIL |
| Severity (≥ 0.60) | 0.366667 | 0.416667 | 0.433333 | FAIL |
| Missing information (≥ 0.35) | 0.153872 | 0.208485 | 0.194444 | FAIL |
| Full-corpus macro (≥ 0.65) | 0.322189 | 0.358803 | 0.369722 | FAIL |
| Average known cost (≤ USD 0.02) | 0.008120 | 0.008434 | 0.008426 | PASS |

The decision matrices were respectively `TP=2/FN=4/TN=12/FP=4`,
`TP=2/FN=4/TN=12/FP=5`, and `TP=0/FN=6/TN=15/FP=3`; timeouts account for the
11, 10, and 10 unclassified predictions. Known total cost was USD 0.154288,
0.168674, and 0.168514 over 19, 20, and 20 available observations. Gateway
latency p95 for uncensored cases was 56,351 ms, 55,639 ms, and 58,935 ms; the
right-censored counts were 11, 10, and 10.

`BUG-003` records that the manifest-driven runner omitted `run_analysis` even
though every immutable case result, scoring row, `corpus_score`, and effective
configuration validation was present. This report applies the existing closed
coverage and metric definitions directly to those fields; it does not modify or
repeat any dataset.

## Content-addressed raw artifacts

Publication timestamp: `2026-07-31T09:46:00Z`  
Retain until: `2027-07-31T09:46:00Z`

| Run | Exact-byte SHA-256 | Bytes | Location |
| --- | --- | ---: | --- |
| `001` | `032c8374dbc40b328ea5828066647ec3b3420571717c4acb07f56a0e17374c7f` | 203,358 | `file:///C:/Users/luis_/Documents/hive-evaluation-artifacts/sha256/03/032c8374dbc40b328ea5828066647ec3b3420571717c4acb07f56a0e17374c7f.json` |
| `002` | `f5142be92ed99b10af65a2365b2bb27230d26920f1160524bf231665ea1896e2` | 201,443 | `file:///C:/Users/luis_/Documents/hive-evaluation-artifacts/sha256/f5/f5142be92ed99b10af65a2365b2bb27230d26920f1160524bf231665ea1896e2.json` |
| `003` | `8ade64228de7aeaf6964b9e7dd7a025c2092e60ff35ea536f7fdb173cc12d5fd` | 207,658 | `file:///C:/Users/luis_/Documents/hive-evaluation-artifacts/sha256/8a/8ade64228de7aeaf6964b9e7dd7a025c2092e60ff35ea536f7fdb173cc12d5fd.json` |

The exact raw bytes are published without transformation to the external
user-protected NTFS filesystem store above. The tracked artifact index owns the
same hashes, sizes, retention, manifest reference, and this report reference.

## Decision

`rejected-for-freeze-request`

All three runs fail the mandatory 30/30 scoreable-projection gate and every
quality threshold except cost. This result does not authorize a freeze, another
calibration, a holdout, go/no-go, configuration promotion, or reopening F1a.

## Evidence minimization

This report and the tracked index contain no credential, prompt, provider
output, reasoning, corpus content, organizational content, or provider error
body.
