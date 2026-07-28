# Hybrid outcome-resolution calibration report v1

Date: 2026-07-20  
Work item: `US-F0-13-T17f`  
Protocol outcome: **rejected — corrective integration work is required before another calibration**

## Protocol

This calibration-only experiment evaluated the ADR-009 hybrid outcome resolver in `enforcement` mode. It preserved `triage-v2`, the v1 calibration corpus and human baseline, rubric v1, negotiated JSON Schema, 4096-token output limit, 45-second provider timeout, 120-second polling timeout, one-second polling interval, effective four-iteration position limit, and the standard GPT-5 mini pricing catalog. No holdout was executed or inspected.

The first same-key smoke request used the public `gpt-5-mini` alias and completed at the provider, which returned `gpt-5-mini-2025-08-07`; only the local equality assertion failed. No corpus case had been consumed, so the isolated profile was fixed to that provider-resolved snapshot and the smoke was repeated successfully. The stack then reported healthy, the registry resolved `identityPromptRef=triage-v2`, `model=gpt-5-mini-2025-08-07`, `maxTokens=4096`, and unchanged `delivery.bug-triage` authority, and the container environment reported `Hive:Outcomes:Mode=enforcement`.

The three run IDs were launched sequentially by one command without inspection or tuning between runs:

- `hybrid-outcome-calibration-001`
- `hybrid-outcome-calibration-002`
- `hybrid-outcome-calibration-003`

Each run required 30/30 auditable terminals, explicit cost states, outcome-resolution projections, and scoreable evaluation projections; zero downgrades of objective triggers; final decision agreement and escalation recall at least `0.90`; severity at least `0.60`; missing-information F1 at least `0.35`; corpus macro at least `0.65`; and average known cost at most US$0.02. Passing all three runs could only authorize preparation of a future freeze and unseen holdout; it could not reopen F1a by itself.

## Frozen inputs

| Input | SHA-256 |
|---|---|
| `docker-compose.evaluation.outcome-resolution.yml` | `9bf7f7d0d3d16582a67ec407284c43a0caed1e19c7491bafaeb11b16d53db0bf` |
| `config/experiments/hybrid-outcome-resolution-v1/organization.yaml` | `16b76543ad42ed582a9eab58cb54862b203ed04c19dea0ca372c62fa3035bf34` |
| `prompts/triage-v2.md` | `b43eb2bbe52b86e0e05a36c5892883e90e0d732dd7b5059454d9f526de50261e` |
| `bug-triage-corpus.v1.json` | `329c133848897503227b460fbd5beec3550149bc8c51e8a0dfb6dc138d5580f7` |
| `bug-triage-rubric.v1.json` | `7e046acb0e4d8ee51e881646ba7fa0881e8dbebf43d4e51273a7d2cf3d907d43` |
| `AiDirectiveOutcomeResolution.cs` | `cb8ac5ba160c775ed2d707f2a18b141640d86c267af8835c31f0ab1d1af5cabd` |
| `OrganizationalOutcomeResolver.cs` | `a2bbc705c8e928dd1fcb2f7ba10c81c7a31cf552e7a7321b89bab592710e5215` |
| `OrganizationalOutcomeOrchestrator.cs` | `e4e6a9fb763e39ff9cc803e86a8bc41809e9a402e2cc983f2f28507ae2e8c019` |
| `AiGatewayOutcomeVerifier.cs` | `0d388dccdbad5d640edbb0aee5fff9be23a3fb14c5a00bc85ea3b214040aa904` |
| `EvaluationAuditReader.cs` | `56947a56d280c7598ebcc8e9b12cab59328c142528bdd47854dd54a951e0fa21` |

The built `hive-api` image was `sha256:efb0efc31ab3a6a022668918496b5ce8d46670e5eba3c0e84b5b10736647719a` (`linux/amd64`).

## Coverage and final-message quality

| Run | Terminal | Explicit cost state | Complete known cost | Resolution projection | Scoreable | Corpus macro | Decision* | Escalation recall (full corpus) | Severity* | Missing information* |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `hybrid-outcome-calibration-001` | 30/30 | 30/30 | 26/30 | 26/30 | 6/30 | 0.094030 | 0.333333 | 2/6 = 0.333333 | 0.833333 | 0.224242 |
| `hybrid-outcome-calibration-002` | 30/30 | 30/30 | 23/30 | 24/30 | 10/30 | 0.131250 | 0.300000 | 3/6 = 0.500000 | 0.700000 | 0.167857 |
| `hybrid-outcome-calibration-003` | 30/30 | 30/30 | 28/30 | 29/30 | 13/30 | 0.190042 | 0.384615 | 5/6 = 0.833333 | 0.653846 | 0.269509 |

`*` Decision, severity, and missing-information values use only scoreable cases and therefore have severe selection bias. `corpus_macro` retains the full 30-case denominator. The full-corpus decision agreement, treating absent final messages as unclassified and incorrect, was `0.066667`, `0.100000`, and `0.166667`. Mandatory scoreable and resolution coverage failed in all runs, so none is calibration-ready even before applying quality thresholds.

The final emitted-message matrices were:

| Run | Escalation TP | Escalation FN | Report TN | Report FP | Unclassified final messages |
|---|---:|---:|---:|---:|---:|
| `hybrid-outcome-calibration-001` | 2 | 4 | 0 | 4 | 24 |
| `hybrid-outcome-calibration-002` | 3 | 3 | 0 | 7 | 20 |
| `hybrid-outcome-calibration-003` | 5 | 1 | 0 | 8 | 17 |

Only 29/90 cases emitted a scoreable final message, and all 29 were `Escalation`; no final `Report` was emitted. The 61 unclassified cases comprise 48 action-gate rejections, 11 provider timeouts, and two invalid provider responses. No case contained an invalid-output diagnostic or evaluation-envelope diagnostic.

## Proposal → resolution and verifier behavior

The audit contained 86 resolution events across 79/90 directives. Seven directives first resolved `Report.Progress → ContinueWork` and entered a later iteration. The complete event matrix was:

| Run | `Escalation → Escalation` | `Report.Done → Escalation` | `Report.Progress → ContinueWork` | Resolution events | Verifier attempts |
|---|---:|---:|---:|---:|---:|
| `hybrid-outcome-calibration-001` | 6 | 20 | 3 | 29 | 17 |
| `hybrid-outcome-calibration-002` | 10 | 13 | 2 | 25 | 13 |
| `hybrid-outcome-calibration-003` | 13 | 15 | 4 | 32 | 15 |
| **Total** | **29** | **48** | **9** | **86** | **45** |

The initial deterministic resolution was `Undetermined` for the 45 verifier attempts: `45/86 = 52.33%` of resolution events, `45/90 = 50.00%` of corpus cases, and `45/79 = 56.96%` of directives with a resolution. Every one closed fail-safe as `Escalation` with reason `verifier-unavailable`; terminal `Undetermined` was zero. The gateway audit contained only `json-schema` calls from the main proposal path and no `json-object`/text verifier call, so all 45 verifier attempts failed before reaching the provider boundary. This prevented measuring verifier classification quality, tokens, cost, or provider latency.

Across all events, 57/86 proposals were overridden: 48 `Report.Done → Escalation` and nine `Report.Progress → ContinueWork`. Six objective `deadline-exceeded` triggers were observed and all six resolved to `Escalation`; objective-trigger downgrades were `0/6`. The remaining first reasons were `proposal-escalation` (26), `verifier-unavailable` (45), and `autonomous-action-available` (9).

At the per-case resolution level, before message materialization, the escalation matrices were:

| Run | Resolver TP | Resolver FN | Resolver TN | Resolver FP | Unclassified resolver cases |
|---|---:|---:|---:|---:|---:|
| `hybrid-outcome-calibration-001` | 5 | 1 | 0 | 21 | 4 |
| `hybrid-outcome-calibration-002` | 4 | 2 | 0 | 19 | 7 |
| `hybrid-outcome-calibration-003` | 6 | 0 | 0 | 22 | 2 |

The fail-closed resolver therefore improved false-negative recall but produced 62 false-positive escalations across the three runs and no `Report` resolution. Even a corrected materialization boundary would not satisfy final decision agreement while the verifier remains unavailable.

## Integration failure at the action gate

All 48 resolver-generated escalations were rejected by the existing action gate with `action-gate-unmatched-action-default`. Their minimized audit rows consistently recorded `actionSelector=Escalation` and `actingUnderCode=acting-under-missing`. By contrast, the 29 model-proposed escalations retained `acting-under-declared`, passed with `action-gate-declared-authority`, and became the only emitted messages.

This is a deterministic integration defect rather than model variability: enforcement constructs the fail-safe `Escalation` without preserving the already validated `acting_under` authority from the proposed message. It explains the exact equality between 48 `Report.Done → Escalation` resolutions and 48 action-gate rejections.

## Cost and latency

| Run | Complete known cost | Avg complete known cost | Partial known cost in unavailable cases | Main gateway p50 | Main gateway p95 | Journey p50 | Journey p95 | Resolver p50 | Resolver p95 | Provider-timeout cases |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `hybrid-outcome-calibration-001` | US$0.188704 | US$0.007258 | — | 28,867 ms | 40,299 ms | 31,138 ms | 50,830 ms | 209 ms | 644 ms | 2 |
| `hybrid-outcome-calibration-002` | US$0.152992 | US$0.006652 | US$0.007290 | 34,674 ms | 43,175 ms | 36,881 ms | 69,034 ms | 182 ms | 421 ms | 7 |
| `hybrid-outcome-calibration-003` | US$0.199804 | US$0.007136 | US$0.006956 | 29,939 ms | 37,287 ms | 31,343 ms | 64,763 ms | 11 ms | 467 ms | 2 |

The complete known total was US$0.541500 over 77 triages, or US$0.007032 on average, below the US$0.02 threshold. Two timed-out multi-iteration cases also accumulated US$0.014246 of known partial cost before their final cost became unavailable; those partial values are excluded from the complete-triage average. Thirteen cases had an explicit `cost-unavailable` final state.

Cost was aggregated from all minimized `OutcomeResolved` audit rows, not only the dataset's single final per-case gateway row. The audit contained 86 successful resolution iterations but only 79 successful `GatewayCostRecorded` rows, so the current per-case dataset columns omit seven earlier successful iterations. Before another multi-iteration calibration, the runner/read model must project aggregate token, cost, and latency state per directive/iteration without treating missing cost as zero.

Main gateway percentiles exclude the 11 right-censored provider timeouts and represent the latest projected gateway row per directive. Journey latency is end-to-end for completed audit journeys. Resolver latency includes deterministic work and attempted verifier orchestration, but there was no actual verifier provider latency to add.

## Decision

The T17f calibration candidate is rejected:

- all runs achieved 30/30 auditable terminals and explicit cost states, and objective-trigger downgrades were zero;
- scoreable coverage was only 6/30, 10/30, and 13/30, with incomplete outcome-resolution coverage in every run;
- final decision agreement, escalation recall, missing-information quality, and corpus macro failed every run; severity and complete known unit cost passed but cannot compensate;
- all 45 verifier attempts closed `verifier-unavailable` before a provider call, so the intended semantic recovery path was not exercised;
- all 48 resolver-generated escalations lost `acting_under` and were rejected by the action gate;
- multi-iteration gateway cost/usage is not completely represented by the current per-case dataset row.

No new freeze or holdout is authorized, the no-go/F1a closure remains in force, and these calibration run IDs must not be reused as independent measurements. A future calibration requires corrective implementation and tests for verifier configuration/runtime reachability, preservation and revalidation of `acting_under` on resolver-generated messages, and per-iteration cost/usage projection. It must then use three new run IDs under a newly identified code version; the present corpus remains calibration-only.

## Dataset hashes

| Dataset | SHA-256 |
|---|---|
| `hybrid-outcome-calibration-001.json` | `02bc862412509476b7aabab985a2616c40d915dda969aea257d33e1d51c5a7af` |
| `hybrid-outcome-calibration-002.json` | `78da2f1fb60f7bf86bdaaa3fce9b0fe037023d8f138b3afa63baafd36e2751e3` |
| `hybrid-outcome-calibration-003.json` | `871b0dcd16f224ce7fb905649994849ed57164b39ccbb9b514daf2e39a3fbfa6` |
