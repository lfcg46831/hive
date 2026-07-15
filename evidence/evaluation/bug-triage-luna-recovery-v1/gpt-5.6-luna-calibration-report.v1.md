# GPT-5.6 Luna calibration recovery report v1

Date: 2026-07-15  
Work item: `US-F0-13-T13`  
Protocol outcome: **rejected — not a candidate for a new freeze**

## Protocol

This calibration-only experiment isolated one change: the effective `bug-triage` model was replaced by `gpt-5.6-luna`. The corpus, human baseline, business prompt, rubric, negotiated JSON Schema, code, 4096-token output limit, 45-second provider timeout, 120-second polling timeout, and one-second polling interval remained unchanged. Three independent run IDs were executed sequentially without tuning between results. No holdout was executed or inspected.

The predeclared promotion rule required every run independently to achieve 30/30 terminal, explicit-cost-state, and scoreable-projection coverage and every quality/cost threshold from bible rev 2.12. A calibration failure cannot reopen the no-go recorded by rev 2.13.

## Frozen inputs for this experiment

| Input | SHA-256 |
|---|---|
| `docker-compose.evaluation.gpt-5.6-luna.yml` | `fb3a634c8ef4456a953b25fb2b370a75916994f41d6f5029f720439d7ef4e67f` |
| `config/experiments/gpt-5.6-luna/organization.yaml` | `502560c75b547e468f7af705f7a99a2025e030bb0ba18dfde1397f1644396aba` |
| `bug-triage-corpus.v1.json` | `329c133848897503227b460fbd5beec3550149bc8c51e8a0dfb6dc138d5580f7` |
| `bug-triage-rubric.v1.json` | `7e046acb0e4d8ee51e881646ba7fa0881e8dbebf43d4e51273a7d2cf3d907d43` |
| `prompts/triage-v1.md` | `ca34ee56910831337d3888df014a30ec96d3c09fd5499b2bb5b81a551b573a34` |

The runtime registry and gateway default both resolved to `gpt-5.6-luna`; every one of the 90 case records retained that model ID and effective output mode `json-schema`. Successful calls used pricing catalog `openai-2026-07-15-gpt-5.6-luna`, USD 1.00 input and USD 6.00 output per one million text tokens, sourced from the [official GPT-5.6 Luna model page](https://developers.openai.com/api/docs/models/gpt-5.6-luna) on 2026-07-15.

## Results

| Run | Terminal | Cost state | Scoreable | HTTP 401/403 mapped as `credentials-missing` | Corpus macro | Decision* | Escalation recall* | Severity* | Missing information* | Avg known cost | p50 | p95 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `luna-calibration-001` | 30/30 | 30/30 | 23/30 | 7 | 0.413523 | 0.826087 | 0/4 = 0.000000 | 0.695652 | 0.137352 | US$0.006158 | 5,479 ms | 8,878 ms |
| `luna-calibration-002` | 30/30 | 30/30 | 21/30 | 9 | 0.385383 | 0.761905 | 0/5 = 0.000000 | 0.785714 | 0.134218 | US$0.005901 | 4,586 ms | 7,489 ms |
| `luna-calibration-003` | 30/30 | 30/30 | 27/30 | 3 | 0.479028 | 0.777778 | 0/6 = 0.000000 | 0.703704 | 0.150353 | US$0.006042 | 5,447 ms | 9,267 ms |

`*` Dimension scores and the displayed decision matrix denominators use only scoreable cases and therefore have selection bias. The dataset `corpus_score` keeps the full 30-case denominator, assigning no score to projection-missing cases. Neither view is gate-eligible because coverage is incomplete.

Across all runs, 71/90 cases were scoreable and 19/90 (21.1%) ended in a provider HTTP 401 or 403, both intentionally collapsed by the safe adapter taxonomy to `credentials-missing`; those 19 had explicit `cost-unavailable` state and `projection-missing`. The failures were interleaved with successful calls under the same process, credential, model, and configuration. Four case IDs failed in two runs and eleven failed in one run; no case failed in all three. There were no invalid-output or envelope diagnostics among the successful calls.

Known estimated spend was US$0.428667 over 71 successful calls (US$0.006038 average), with 183,669 input and 40,833 output tokens. Latency percentiles above include successful provider calls only. Cost met the rev 2.12 unit threshold on available observations, but cost cannot compensate for incomplete coverage or quality failure.

## Decision

The model-only recovery hypothesis is rejected for this HIVE profile and provider/account configuration:

- All three runs failed the mandatory 30/30 scoreability condition.
- On every scoreable subset, the model produced zero escalations: observed escalation recall was 0.000000, far below the critical 0.90 threshold. Treating unclassified escalation cases conservatively also yields 0/6 for every complete corpus.
- Decision agreement, missing-information F1, and full-corpus macro score were below threshold in every run. Severity and known unit cost passed, while latency was materially lower than the prior GPT-5 mini calibration.
- Resolving the intermittent 401/403 behavior alone would not establish recovery because the core escalation and missing-information failures remain.

This result does not claim that GPT-5.6 Luna is generally incapable; it establishes that the exact alias, account/endpoint, prompt, schema, and HIVE integration tested here are not a viable replacement candidate. No new freeze or holdout is justified. The final no-go from `holdout-v2` remains in force, and the five discovery interviews remain an independent value/market requirement.

## Dataset hashes

| Dataset | SHA-256 |
|---|---|
| `luna-calibration-001.json` | `0e14fe16742663dac82284b15c3c88eccd595ef622a0d3e2389df996db1796af` |
| `luna-calibration-002.json` | `8de58f7e6cf068d2f0c87dc4ecf276c71734eecf166632c4ba28c70fe13711dc` |
| `luna-calibration-003.json` | `2d628d288195b3cacff0092fbd6f38338971cb8d9599d33a2f42dcf300395ee3` |
