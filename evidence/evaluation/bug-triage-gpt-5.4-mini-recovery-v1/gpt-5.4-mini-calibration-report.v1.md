# GPT-5.4 mini calibration report v1

Date: 2026-07-15  
Work item: `US-F0-13-T15`  
Protocol outcome: **rejected — not a candidate for a new freeze**

## Protocol

This calibration-only experiment isolated the model against the final T14 prompting profile. It preserved `triage-v2`, the generic HIVE intent boundary, code, calibration corpus, human baseline, rubric, negotiated JSON Schema, 4096-token output limit, 45-second provider timeout, 120-second polling timeout, and one-second polling interval. The only experimental substitutions were the effective model and its pricing entry.

The first same-key smoke call requested the public `gpt-5.4-mini` alias and completed at the provider, which returned resolved model id `gpt-5.4-mini-2026-03-17`; only the local smoke assertion failed because it expected the alias literally. No corpus case had been consumed, so the experiment profile was pinned to that official snapshot and the smoke test was repeated successfully before calibration. The three run IDs were then launched sequentially by one command, without inspection or tuning between results. No holdout was executed or inspected.

The predeclared promotion rule required every run independently to achieve 30/30 terminal, explicit-cost-state, and scoreable-projection coverage and every quality/cost threshold from bible rev 2.12. A calibration success could only justify designing a future freeze; it could not reopen the no-go recorded by rev 2.13.

## Frozen inputs for this experiment

| Input | SHA-256 |
|---|---|
| `docker-compose.evaluation.gpt-5.4-mini-prompt-recovery.yml` | `9de3a2c4db3888c93df53956e739d8ca4756ba755d258a0c6bd5f620cd6bdf12` |
| `config/experiments/gpt-5.4-mini-prompt-recovery/organization.yaml` | `17f7888e5102ad72713d00a67c965ec4d72dc10ce4469f1098874dc9dccde854` |
| `prompts/triage-v2.md` | `b43eb2bbe52b86e0e05a36c5892883e90e0d732dd7b5059454d9f526de50261e` |
| `src/Hive.Actors/Positions/AiDirectivePrompt.cs` | `2c96d01081f1efd8720855b703a3352fc4dad242c4c5c699124ba729a5fd2561` |
| `bug-triage-corpus.v1.json` | `329c133848897503227b460fbd5beec3550149bc8c51e8a0dfb6dc138d5580f7` |
| `bug-triage-rubric.v1.json` | `7e046acb0e4d8ee51e881646ba7fa0881e8dbebf43d4e51273a7d2cf3d907d43` |

The built `hive-api` image was `sha256:1dd79bb29d7bfff167910dd756adbb5c7946eff5ed35b43d6b6085939142e2aa`. Before execution, readiness returned HTTP 200 and the registry resolved `identityPromptRef=triage-v2`, `model=gpt-5.4-mini-2026-03-17`, `maxTokens=4096`, and the unchanged `delivery.bug-triage` authority. Every case retained the snapshot model id and effective output mode `json-schema`.

Successful calls used pricing catalog `openai-2026-07-15-gpt-5.4-mini`, USD 0.75 input and USD 4.50 output per one million text tokens, sourced from the [official GPT-5.4 mini model page](https://developers.openai.com/api/docs/models/gpt-5.4-mini) on 2026-07-15.

## Results

| Run | Terminal | Cost state | Scoreable | Provider timeout | Corpus macro | Decision* | Escalation recall* | Severity* | Missing information* | Avg known cost | p50 | p95 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `gpt-5-4-mini-calibration-001` | 30/30 | 30/30 | 26/30 | 4 | 0.489088 | 0.846154 | 1/5 = 0.200000 | 0.730769 | 0.156334 | US$0.003474 | 3,651 ms | 6,106 ms |
| `gpt-5-4-mini-calibration-002` | 30/30 | 30/30 | 27/30 | 3 | 0.451237 | 0.703704 | 1/6 = 0.166667 | 0.685185 | 0.144139 | US$0.003768 | 3,375 ms | 7,150 ms |
| `gpt-5-4-mini-calibration-003` | 30/30 | 30/30 | 26/30 | 4 | 0.477389 | 0.769231 | 1/6 = 0.166667 | 0.769231 | 0.145239 | US$0.003501 | 3,622 ms | 5,929 ms |

`*` Dimension scores and decision-matrix denominators use only scoreable cases and therefore have selection bias. `corpus_score` keeps the full 30-case denominator. None of the runs is gate-eligible because scoreable coverage is incomplete.

Across all runs, 79/90 cases were scoreable and 11/90 (12.2%) ended at the configured provider timeout with explicit `cost-unavailable` state and missing projection. There were no provider authorization failures, invalid-output diagnostics, or evaluation-envelope diagnostics.

Known estimated spend was US$0.283076 over 79 successful corpus calls (US$0.003583 average), with 208,887 input and 28,089 output tokens. The two smoke calls are not included in this dataset total. Unit cost remained below the rev 2.12 threshold. Latency percentiles in the table use successful provider calls only; timeout cases are right-censored at the configured boundary and excluded from those percentiles.

## Decision-boundary analysis

Unlike GPT-5.6 Luna under T14, GPT-5.4 mini emitted canonical escalations. The scoreable decision matrices were:

| Run | Escalation TP | Escalation FN | Report TN | Report FP |
|---|---:|---:|---:|---:|
| `gpt-5-4-mini-calibration-001` | 1 | 4 | 21 | 0 |
| `gpt-5-4-mini-calibration-002` | 1 | 5 | 18 | 3 |
| `gpt-5-4-mini-calibration-003` | 1 | 5 | 19 | 1 |

`triage-022` was the single true-positive escalation in all three runs. False-positive escalations appeared for `triage-004`, `triage-026`, and `triage-027`; `triage-027` repeated in two runs. One timeout, `triage-030` in run 001, had an escalation reference. Even granting that timeout as a true positive yields a best-case full-corpus recall of 2/6 = 0.333333 for run 001; runs 002 and 003 scored all six reference escalations and remained at 1/6 = 0.166667. Timeout recovery therefore cannot approach the required 0.90 recall.

Compared with T14 Luna, the snapshot demonstrated a real but insufficient intent shift: observed recall rose from zero to 0.166667–0.200000 and successful-call latency/cost decreased. It did not recover coverage, decision agreement, missing-information quality, or corpus macro. Compared with the same-corpus GPT-5 mini `calibration-ready-v7`, it added some escalation behavior but lost 30/30 reliability and remained below that run's 0.525815 corpus macro.

## Decision

The GPT-5.4 mini model-only recovery hypothesis is rejected for this profile:

- all three runs failed mandatory 30/30 scoreability because of provider timeouts;
- escalation recall was at most 0.200000 observed and 0.333333 under the most favorable treatment of the only relevant timeout, far below 0.90;
- decision agreement, missing-information F1, and full-corpus macro failed in every run;
- severity and known unit cost passed, but cannot compensate for coverage and critical decision failure;
- the snapshot showed partial escalation capability, but false positives and false negatives remained material and unstable.

No new freeze or holdout is justified, F1a remains closed, and the final no-go from `holdout-v2` remains in force. A timeout-only rerun is not supported because perfect recovery of timed-out cases cannot satisfy the critical recall threshold. If technical investigation continues, it should test the separately specified deterministic boundary or constrained intent verifier rather than another model or wording-only substitution. The five discovery interviews remain an independent value/market requirement.

## Dataset hashes

| Dataset | SHA-256 |
|---|---|
| `gpt-5-4-mini-calibration-001.json` | `e01dbf384142ac46f65f12b1118592949960552fb93003df8786b54de3a1a233` |
| `gpt-5-4-mini-calibration-002.json` | `d8c9f418953aac847425250541935b17c76f21debdcdc43a3b71f57ab40b1b7b` |
| `gpt-5-4-mini-calibration-003.json` | `d77276aca2e842b384932c169e131041d411b8a0e68267c81f75504a415def0e` |
