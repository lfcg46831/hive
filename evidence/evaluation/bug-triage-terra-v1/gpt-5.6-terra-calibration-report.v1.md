# GPT-5.6 Terra calibration report v1

Date: 2026-07-15  
Work item: `US-F0-13-T16`  
Protocol outcome: **rejected — no evidence for another model-only recovery cycle**

## Protocol

This one-run calibration-only exception measured whether a higher-capability model materially changed the final T14 `Report`/`Escalation` boundary before implementing a deterministic boundary or separate intent verifier. It preserved `triage-v2`, the generic HIVE intent rule, code, calibration corpus, human baseline, rubric, negotiated JSON Schema, 4096-token output limit, 45-second provider timeout, 120-second polling timeout, and one-second polling interval. Only the effective model and pricing entry changed.

The same-key smoke test for `gpt-5.6-terra` succeeded before the corpus was consumed and returned the requested model id. The isolated registry then resolved `identityPromptRef=triage-v2`, `model=gpt-5.6-terra`, `maxTokens=4096`, and unchanged `delivery.bug-triage` authority. The single predeclared run id was `terra-calibration-001`. No holdout was executed or inspected.

The run required 30/30 terminal, explicit-cost-state, and scoreable-projection coverage plus every threshold from bible rev 2.12: decision ≥ 0.90, escalation recall ≥ 0.90, severity ≥ 0.60, missing-information ≥ 0.35, full-corpus macro ≥ 0.65, and average known cost ≤ US$0.02.

## Frozen inputs

| Input | SHA-256 |
|---|---|
| `docker-compose.evaluation.gpt-5.6-terra.yml` | `4d703a62660757ddad337cc65bc7e609afd8d0651f9b9fd5faf30584f49b1420` |
| `config/experiments/gpt-5.6-terra/organization.yaml` | `bfc351f065237b1a932de7cb0bcdb8ebdbb02cea233ff303eef83d6fd0aa0756` |
| `prompts/triage-v2.md` | `b43eb2bbe52b86e0e05a36c5892883e90e0d732dd7b5059454d9f526de50261e` |
| `src/Hive.Actors/Positions/AiDirectivePrompt.cs` | `2c96d01081f1efd8720855b703a3352fc4dad242c4c5c699124ba729a5fd2561` |
| `bug-triage-corpus.v1.json` | `329c133848897503227b460fbd5beec3550149bc8c51e8a0dfb6dc138d5580f7` |
| `bug-triage-rubric.v1.json` | `7e046acb0e4d8ee51e881646ba7fa0881e8dbebf43d4e51273a7d2cf3d907d43` |

The built `hive-api` image was `sha256:1dd79bb29d7bfff167910dd756adbb5c7946eff5ed35b43d6b6085939142e2aa`. All 30 cases recorded model id `gpt-5.6-terra` and output constraint mode `json-schema`. Successful calls used pricing catalog `openai-2026-07-15-gpt-5.6-terra`, with the official standard rates of USD 2.50 input and USD 15.00 output per one million text tokens ([official GPT-5.6 Terra model page](https://developers.openai.com/api/docs/models/gpt-5.6-terra), accessed 2026-07-15).

## Results

| Run | Terminal | Cost state | Scoreable | Provider failure | Corpus macro | Decision* | Escalation recall* | Severity* | Missing information* | Avg known cost | p50 | p95 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `terra-calibration-001` | 30/30 | 30/30 | 27/30 | 3 `credentials-missing` | 0.503154 | 0.814815 | 0/5 = 0.000000 | 0.777778 | 0.121123 | US$0.014419 | 6,692 ms | 11,380 ms |

`*` Dimension scores and the decision matrix use only scoreable cases. `corpus_score` retains the full 30-case denominator. The run is not gate-eligible because scoreable coverage is incomplete.

Three accepted cases (`triage-015`, `triage-026`, and `triage-029`) terminated as `credentials-missing`, with explicit `cost-unavailable` state and missing projections. `triage-015` was the only failed case whose human reference was `Escalation`; the other two referenced `Report`. There were no invalid-output diagnostics, and all successful cases had valid projections for every dimension.

Known estimated spend was US$0.389308 over 27 successful corpus calls (US$0.014419 average), with 71,492 input and 14,038 output tokens. The smoke call is excluded. Cost remained below the rev 2.12 threshold. Latency percentiles use successful provider calls only.

## Decision-boundary analysis

All 27 scoreable outputs were canonical `Report`; none was `Escalation`. The scoreable decision matrix was:

| Escalation TP | Escalation FN | Report TN | Report FP |
|---:|---:|---:|---:|
| 0 | 5 | 22 | 0 |

The false-negative escalation cases were `triage-005`, `triage-011`, `triage-022`, `triage-025`, and `triage-030`. Even granting the failed reference-escalation case `triage-015` as a true positive yields a best possible full-corpus recall of 1/6 = 0.166667, far below the required 0.90. Operational recovery cannot rescue the critical gate, so repeating the run solely for the intermittent authorization failures is not justified.

Compared with GPT-5.4 mini T15, Terra removed the observed true-positive `triage-022` escalation and returned to the all-Report behavior seen with Luna, while using more output tokens and materially higher cost per successful call. Since this is one exploratory run, it does not establish general model rankings; it does establish that this exact Terra profile cannot meet the predeclared gate.

## Decision

The GPT-5.6 Terra model-only exception is rejected:

- scoreable coverage was 27/30 instead of the mandatory 30/30;
- escalation recall was zero on scoreable cases and at most 0.166667 under the most favorable treatment of the failed reference-escalation case;
- decision agreement, missing-information F1, and full-corpus macro failed;
- severity and average known cost passed but cannot compensate for the critical decision failure;
- the higher-cost model did not improve the decision boundary in this run.

No freeze, repeat model-only cycle, or holdout is justified; the final no-go and closed F1a remain in force. Further technical work should implement and calibrate the separately specified deterministic boundary or intent verifier. The discovery interviews remain an independent value/market requirement.

## Dataset hash

| Dataset | SHA-256 |
|---|---|
| `terra-calibration-001.json` | `362fb0f949efb36473258af8dee998d7cb03e54b534b3fb52c9b1f46b4f6cb61` |
