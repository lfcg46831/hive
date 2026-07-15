# GPT-5.6 Luna prompt recovery report v1

Date: 2026-07-15  
Work item: `US-F0-13-T14`  
Protocol outcome: **rejected — not a candidate for a new freeze**

## Protocol

This calibration-only experiment tested whether a more explicit decision boundary could recover escalation behavior from `gpt-5.6-luna`. It introduced two prompting changes and no scoring or output-contract change:

- the business identity was versioned from `triage-v1` to `triage-v2`, with an explicit two-check decision procedure for evidence sufficient to support severity and a safe next step;
- the generic HIVE protocol was made explicit that a response asking the superior to decide, authorize, or choose must use `Escalation` and must never place that request inside `Report`.

The corpus, human baseline, rubric, negotiated JSON Schema, model alias, 4096-token output limit, 45-second provider timeout, 120-second polling timeout, and one-second polling interval remained unchanged. The first attempt was zero-shot: it added no examples, chain-of-thought request, second model call, verifier, schema heuristic, or function-specific compiled logic.

The existing real-provider smoke test completed successfully with the same local key/project and `gpt-5.6-luna` before the corpus was started. The three run IDs were then launched sequentially by one command, without inspecting or tuning between results. No holdout was executed or inspected.

The predeclared promotion rule required every run independently to achieve 30/30 terminal, explicit-cost-state, and scoreable-projection coverage and every quality/cost threshold from bible rev 2.12. Calibration success could only justify designing a future freeze; it could not reopen the no-go recorded by rev 2.13.

## Frozen inputs for this experiment

| Input | SHA-256 |
|---|---|
| `docker-compose.evaluation.gpt-5.6-luna-prompt-recovery.yml` | `174292e9939224cc6bcaefd48590abd66f10c1e0a5ad0f397e90a8950f8f2538` |
| `config/experiments/gpt-5.6-luna-prompt-recovery/organization.yaml` | `4a13242e8d5c54765ee26bfa039bde88479f388770d89f1220746f9a4d58115e` |
| `prompts/triage-v2.md` | `b43eb2bbe52b86e0e05a36c5892883e90e0d732dd7b5059454d9f526de50261e` |
| `src/Hive.Actors/Positions/AiDirectivePrompt.cs` | `2c96d01081f1efd8720855b703a3352fc4dad242c4c5c699124ba729a5fd2561` |
| `bug-triage-corpus.v1.json` | `329c133848897503227b460fbd5beec3550149bc8c51e8a0dfb6dc138d5580f7` |
| `bug-triage-rubric.v1.json` | `7e046acb0e4d8ee51e881646ba7fa0881e8dbebf43d4e51273a7d2cf3d907d43` |

The built `hive-api` image was `sha256:1dd79bb29d7bfff167910dd756adbb5c7946eff5ed35b43d6b6085939142e2aa`. Before execution, both health endpoints returned HTTP 200 and the registry resolved `identityPromptRef=triage-v2`, `model=gpt-5.6-luna`, `maxTokens=4096`, and the unchanged `delivery.bug-triage` authority. Every case retained model ID `gpt-5.6-luna` and effective output mode `json-schema`.

Successful calls used pricing catalog `openai-2026-07-15-gpt-5.6-luna`, USD 1.00 input and USD 6.00 output per one million text tokens, sourced from the [official GPT-5.6 Luna model page](https://developers.openai.com/api/docs/models/gpt-5.6-luna) on 2026-07-15.

## Results

| Run | Terminal | Cost state | Scoreable | `credentials-missing` | Corpus macro | Decision* | Escalation recall* | Severity* | Missing information* | Avg known cost | p50 | p95 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `luna-prompt-calibration-001` | 30/30 | 30/30 | 26/30 | 4 | 0.479961 | 0.807692 | 0/5 = 0.000000 | 0.750000 | 0.139981 | US$0.006148 | 5,560 ms | 8,938 ms |
| `luna-prompt-calibration-002` | 30/30 | 30/30 | 24/30 | 6 | 0.435396 | 0.833333 | 0/4 = 0.000000 | 0.729167 | 0.111532 | US$0.006331 | 5,746 ms | 8,871 ms |
| `luna-prompt-calibration-003` | 30/30 | 30/30 | 23/30 | 7 | 0.415167 | 0.826087 | 0/4 = 0.000000 | 0.717391 | 0.121739 | US$0.006409 | 6,033 ms | 7,868 ms |

`*` Dimension scores and decision-matrix denominators use only scoreable cases and therefore have selection bias. `corpus_score` keeps the full 30-case denominator. None of the runs is gate-eligible because coverage is incomplete.

Across all runs, 73/90 cases were scoreable and 17/90 (18.9%) ended in provider authorization failures mapped by the safe adapter taxonomy to `credentials-missing`. Those failures had explicit `cost-unavailable` state and missing projection. They remained interleaved with successful calls even after the same-key Luna preflight succeeded. There were no invalid-output or evaluation-envelope diagnostics among successful calls.

Known estimated spend was US$0.459200 over 73 successful calls (US$0.006290 average), with 193,052 input and 44,358 output tokens. Unit cost remained below the rev 2.12 threshold.

## Decision-boundary analysis

Every scoreable output was a `Report`: 26/26, 24/24, and 23/23. Across the three scoreable subsets, the decision matrices were:

| Run | Escalation TP | Escalation FN | Report TN | Report FP |
|---|---:|---:|---:|---:|
| `luna-prompt-calibration-001` | 0 | 5 | 21 | 0 |
| `luna-prompt-calibration-002` | 0 | 4 | 20 | 0 |
| `luna-prompt-calibration-003` | 0 | 4 | 19 | 0 |

The model still identified the underlying evidence gaps: each of the 13 scoreable human-escalation cases emitted between 4 and 13 valid missing-information labels. It nevertheless selected `Report` in all 13. The prompt change therefore did not move the canonical intent boundary, while preserving the pre-existing bias toward reporting.

Compared with T13, scoreable coverage changed only from 71/90 to 73/90 and authorization failures from 19 to 17. Escalation recall remained exactly zero, missing-information quality remained far below threshold, and full-corpus macro remained below 0.50 in every run. The differences in coverage and other scores do not establish a prompt effect because the scoreable subsets changed with interleaved authorization failures.

## Decision

The zero-shot prompt-only recovery variant is rejected:

- all three runs failed mandatory 30/30 scoreability;
- escalation recall was 0.000000 in every run, far below the critical 0.90 threshold;
- decision agreement, missing-information F1, and full-corpus macro failed in every run;
- severity and known unit cost passed, but cannot compensate for coverage and critical decision failure;
- successful same-key preflight did not eliminate the provider authorization instability.

No new freeze or holdout is justified, F1a remains closed, and the final no-go from `holdout-v2` remains in force. Another wording-only iteration is not supported by these results. If technical investigation continues, the next distinct hypothesis should be an explicitly specified deterministic boundary or separate constrained intent verifier, tested in calibration before any new freeze. The five discovery interviews remain an independent value/market requirement.

## Dataset hashes

| Dataset | SHA-256 |
|---|---|
| `luna-prompt-calibration-001.json` | `2b6f88e7a71d09c59d77341c88c35de68bf6047e57a1aab2dd25ff4398f9d568` |
| `luna-prompt-calibration-002.json` | `61180274551438af4464576e56a183fd3a7081fe3a84f28f966aaf3c3d3d82be` |
| `luna-prompt-calibration-003.json` | `da43a03d292f6e8e96bed3220771ec2ac8ffd90f1bb1c1067fefba8dd171f743` |
