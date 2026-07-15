<!-- evaluation-report-schema-version: 1 -->

# Bug triage holdout v2 - unit economics and quality

This is the versioned US-F0-13-T05 evidence artefact. It measures the frozen holdout and does not define thresholds or make the US-F0-13-T06 go/no-go decision.

## Evidence

| Field | Value |
| --- | --- |
| Report id | `bug-triage-unit-economics-quality-v2` |
| Run id | `holdout-v2` |
| Partition | `holdout` |
| Freeze id | `bug-triage-holdout-v1` |
| Code version | `us-f0-13-t12-v1` |
| Configuration version | `acme-delivery-bug-triage-v5` |
| Dataset | `holdout-v2.json` (`normalized-text-sha256:5928f9a83058d0c6de78617b30947cd9346fc54f3a402b5363f0d2154975f1d1`) |
| Report profile | `bug-triage-report-profile.v2.json` (`normalized-text-sha256:6e81cc6fb1f996e289b3da0b50dc94ccc640c8a5e31f2a2fa5362e71d6533280`) |
| Evidence status | `gate-eligible` |
| Gate eligible | yes |
| Failure codes | none |

## Quality

| Metric | Complete | Total | Rate |
| --- | ---: | ---: | ---: |
| Auditable terminal | 30 | 30 | 100.00 % |
| Explicit cost state | 30 | 30 | 100.00 % |
| Scoreable projection | 30 | 30 | 100.00 % |

Corpus macro score: **0.6481**.

| Dimension | Cases | Macro agreement |
| --- | ---: | ---: |
| `decision` | 30 | 0.9333 |
| `missing-information` | 30 | 0.3017 |
| `severity` | 30 | 0.7500 |

### Decision analysis

| Baseline | Predicted | Cases |
| --- | --- | ---: |
| `report` | `report` | 21 |
| `report` | `escalation` | 1 |
| `escalation` | `report` | 1 |
| `escalation` | `escalation` | 7 |

Predicted `escalation` rate: **26.67 %** (8/30); baseline rate: **26.67 %** (8/30); recall: **87.50 %**; unclassified: **0**.

Invalid-output diagnostics: **none**.

Envelope diagnostics: **none**.

## Unit economics

Daily projection assumption: **50 triages per position/day**.

| Currency | Costed triages | Unavailable | Known total | Cost/triage | Cost/position/day |
| --- | ---: | ---: | ---: | ---: | ---: |
| `USD` | 30 | 0 | 0.167863 | 0.005595 | 0.279772 |

### Model cost sensitivity

The scenarios reprice observed input/output token usage only. They do not estimate the alternative model's quality, output length, latency, cached-token mix, or operational behaviour.

| Provider/model | Pricing | Usage complete | Input tokens | Output tokens | Repriced total | Cost/triage | Cost/position/day |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `openai/gpt-5-mini` | `openai-public-pricing-2026-07-15` (0.25/2 USD per 1000000 input/output tokens) | 30/30 | 77188 | 74281 | 0.167859 USD | 0.005595 | 0.279765 |
| `openai/gpt-5-nano` | `openai-public-pricing-2026-07-15` (0.05/0.4 USD per 1000000 input/output tokens) | 30/30 | 77188 | 74281 | 0.033572 USD | 0.001119 | 0.055953 |

## Latency

| Observed | Right-censored | p50 | p95 | p99 | Max |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 30 | 0 | 23487 ms | 30031 ms | 30421 ms | 30421 ms |

## Limitations and interpretation

- The holdout is gate-eligible as evidence, but threshold comparison and the decision remain exclusively in US-F0-13-T06.
- A missing cost or token observation is never treated as zero; affected normalized projections are shown as unavailable.
- The position/day projection is linear at the profile assumption of 50 triages and excludes non-model infrastructure, human review, retries, storage, and support costs.
- Model sensitivity is a token-price scenario, not evidence that another model preserves the measured quality or latency.

## Pricing sources

- `openai/gpt-5-mini`: [openai-public-pricing-2026-07-15](https://developers.openai.com/api/docs/models/gpt-5-mini), accessed 2026-07-15.
- `openai/gpt-5-nano`: [openai-public-pricing-2026-07-15](https://developers.openai.com/api/docs/models/gpt-5-nano), accessed 2026-07-15.
