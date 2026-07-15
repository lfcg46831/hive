<!-- evaluation-report-schema-version: 1 -->

# Bug triage holdout v1 — unit economics and quality

This is the versioned US-F0-13-T05 evidence artefact. It measures the frozen holdout and does not define thresholds or make the US-F0-13-T06 go/no-go decision.

## Evidence

| Field | Value |
| --- | --- |
| Report id | `bug-triage-unit-economics-quality-v1` |
| Run id | `holdout-v1` |
| Partition | `holdout` |
| Freeze id | `bug-triage-holdout-v1` |
| Code version | `us-f0-13-t09e-v4` |
| Configuration version | `acme-delivery-bug-triage-v5` |
| Dataset | `holdout-v1.json` (`normalized-text-sha256:517e1091a5e1b7c2c4099b9525b99ecd75a5c6078f53bf0ceb69f007065797e9`) |
| Report profile | `bug-triage-report-profile.v1.json` (`normalized-text-sha256:3e6da67694d50082f67f4d45e3758f1e594f0e0d1c70e64f41e3b5870d1313b6`) |
| Evidence status | `incomplete` |
| Gate eligible | no |
| Failure codes | `projection-incomplete` |

## Quality

| Metric | Complete | Total | Rate |
| --- | ---: | ---: | ---: |
| Auditable terminal | 30 | 30 | 100.00 % |
| Explicit cost state | 30 | 30 | 100.00 % |
| Scoreable projection | 23 | 30 | 76.67 % |

Corpus macro score: **0.5751**.

| Dimension | Cases | Macro agreement |
| --- | ---: | ---: |
| `decision` | 30 | 0.9667 |
| `missing-information` | 30 | 0.1980 |
| `severity` | 30 | 0.6167 |

### Decision analysis

| Baseline | Predicted | Cases |
| --- | --- | ---: |
| `report` | `report` | 21 |
| `report` | `escalation` | 1 |
| `escalation` | `report` | 0 |
| `escalation` | `escalation` | 8 |

Predicted `escalation` rate: **30.00 %** (9/30); baseline rate: **26.67 %** (8/30); recall: **100.00 %**; unclassified: **0**.

Invalid-output diagnostics: **none**.

## Unit economics

Daily projection assumption: **50 triages per position/day**.

| Currency | Costed triages | Unavailable | Known total | Cost/triage | Cost/position/day |
| --- | ---: | ---: | ---: | ---: | ---: |
| `USD` | 30 | 0 | 0.162192 | 0.005406 | 0.270320 |

### Model cost sensitivity

The scenarios reprice observed input/output token usage only. They do not estimate the alternative model's quality, output length, latency, cached-token mix, or operational behaviour.

| Provider/model | Pricing | Usage complete | Input tokens | Output tokens | Repriced total | Cost/triage | Cost/position/day |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `openai/gpt-5-mini` | `openai-public-pricing-2026-07-15` (0.25/2 USD per 1000000 input/output tokens) | 30/30 | 69202 | 72444 | 0.162189 USD | 0.005406 | 0.270314 |
| `openai/gpt-5-nano` | `openai-public-pricing-2026-07-15` (0.05/0.4 USD per 1000000 input/output tokens) | 30/30 | 69202 | 72444 | 0.032438 USD | 0.001081 | 0.054063 |

## Latency

| Observed | Right-censored | p50 | p95 | p99 | Max |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 30 | 0 | 24521 ms | 30903 ms | 35671 ms | 35671 ms |

## Limitations and interpretation

- This holdout is not gate-eligible. Its quality figures describe the observed run, but US-F0-13-T06 must not use it for a go/no-go decision.
- A missing cost or token observation is never treated as zero; affected normalized projections are shown as unavailable.
- The position/day projection is linear at the profile assumption of 50 triages and excludes non-model infrastructure, human review, retries, storage, and support costs.
- Model sensitivity is a token-price scenario, not evidence that another model preserves the measured quality or latency.

## Pricing sources

- `openai/gpt-5-mini`: [openai-public-pricing-2026-07-15](https://developers.openai.com/api/docs/models/gpt-5-mini), accessed 2026-07-15.
- `openai/gpt-5-nano`: [openai-public-pricing-2026-07-15](https://developers.openai.com/api/docs/models/gpt-5-nano), accessed 2026-07-15.
