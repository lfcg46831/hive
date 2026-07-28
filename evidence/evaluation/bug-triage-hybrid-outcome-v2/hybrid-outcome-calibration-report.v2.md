# Hybrid outcome-resolution corrected calibration report v2

Date: 2026-07-20  
Work item: `US-F0-13-T17f`  
Protocol outcome: **rejected — integration corrections passed, calibration thresholds did not**

## Protocol

This corrective calibration evaluated ADR-009 in `enforcement` mode after fixing the three integration defects identified by v1. It preserved `triage-v2`, the provider-resolved `gpt-5-mini-2025-08-07` snapshot, calibration corpus and baseline v1, rubric v1, main JSON Schema and 4096-token limit, 45-second provider timeout, 120-second polling timeout, one-second polling interval, effective four-iteration position limit, policy, pricing, and all quality gates. The verifier remained separate and bounded, with its closed two-field contract and a 1024-token output ceiling suitable for reasoning-token accounting. No holdout was executed or inspected.

The preflight reported a healthy API, `Hive:Outcomes:Mode=enforcement`, `identityPromptRef=triage-v2`, `model=gpt-5-mini-2025-08-07`, `maxTokens=4096`, `maxIterations=4`, and `delivery.bug-triage` authority. Both the general provider smoke and the closed verifier-contract smoke passed against the fixed snapshot.

The three new run IDs were launched sequentially in one command without result inspection or tuning:

- `hybrid-outcome-corrected-calibration-001`
- `hybrid-outcome-corrected-calibration-002`
- `hybrid-outcome-corrected-calibration-003`

The execution host's one-hour command window ended after `001` and `002` had written their datasets while `003` was still running. No dataset was inspected. The same deterministic `003` run ID was resumed immediately with unchanged code and configuration; message/directive idempotency reused already persisted work rather than creating a fourth measurement. This makes the `003` end-to-end journey p95 unsuitable for comparison because the pause is included in wall-clock duration. Provider, verifier, resolver, cost, usage, quality, and terminal observations remain directly measured.

## Frozen inputs and corrected code identity

Git base: `8a1155879688`  
Built image: `sha256:72a5448c489b59aff4a88e6121e1e2c178adb2d26dcaac01e318a3af92c36b35` (`linux/amd64`)

| Input | SHA-256 |
|---|---|
| `docker-compose.evaluation.outcome-resolution.yml` | `9bf7f7d0d3d16582a67ec407284c43a0caed1e19c7491bafaeb11b16d53db0bf` |
| `config/experiments/hybrid-outcome-resolution-v1/organization.yaml` | `16b76543ad42ed582a9eab58cb54862b203ed04c19dea0ca372c62fa3035bf34` |
| `config/organizations/acme-delivery/prompts/triage-v2.md` | `b43eb2bbe52b86e0e05a36c5892883e90e0d732dd7b5059454d9f526de50261e` |
| `bug-triage-corpus.v1.json` | `329c133848897503227b460fbd5beec3550149bc8c51e8a0dfb6dc138d5580f7` |
| `bug-triage-rubric.v1.json` | `7e046acb0e4d8ee51e881646ba7fa0881e8dbebf43d4e51273a7d2cf3d907d43` |
| `AiDirectiveOutcomeResolution.cs` | `21a4724c7c26162d3344d0363f834d1c2971a32ce0a13d0431e92b593f9df2a4` |
| `AiDirectiveResultMessage.cs` | `59bc4aa6c5b3d823f3b5444e40000d268eb881054c7cd8de38d9793d584a224d` |
| `AiGatewayOutcomeVerifier.cs` | `b59b58ea2399dbc446e1ec8b646480f89010e20448141c6a6ed0ce3c9f5ddf3e` |
| `AiGatewayCostAuditEvent.cs` | `584090fe8e5c9686876be2972297ad06c519a1ad8e3f7fe0f8048e56ceb4950a` |
| `JourneyAuditAiGatewayPublisher.cs` | `cb41c396d961a972b87d4794f0c79b0d4f694ca2fc76121fd8fc2e3d91cbf8bf` |
| `EvaluationAuditReader.cs` | `170b1048d7076e45e85006a7b79586550e2e6506603c7ac6721d7a6636422379` |

## Coverage and quality

| Run | Terminal | Explicit cost state | Complete known cost | Resolution projection | Scoreable | Corpus macro | Decision, scoreable | Decision, full corpus | Escalation recall | Severity | Missing information |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `001` | 30/30 | 30/30 | 27/30 | 27/30 | 27/30 | 0.316152 | 0.185185 | 0.166667 | 5/6 = 0.833333 | 0.722222 | 0.122703 |
| `002` | 30/30 | 30/30 | 25/30 | 25/30 | 25/30 | 0.291207 | 0.200000 | 0.166667 | 5/6 = 0.833333 | 0.700000 | 0.126996 |
| `003` | 30/30 | 30/30 | 28/30 | 29/30 | 28/30 | 0.337599 | 0.214286 | 0.200000 | 6/6 = 1.000000 | 0.714286 | 0.135506 |

All 90 cases reached an auditable terminal with an explicit cost state. Eighty emitted a scoreable message; the other ten were provider timeouts. There were no invalid provider responses, evaluation-envelope failures, action-gate rejections, or missing-authority terminals. Mandatory 30/30 scoreable and resolution coverage failed in every run.

The final-message escalation matrices were:

| Run | Escalation TP | Escalation FN | Report TN | Report FP | Unclassified |
|---|---:|---:|---:|---:|---:|
| `001` | 5 | 1 | 0 | 22 | 3 |
| `002` | 5 | 1 | 0 | 20 | 5 |
| `003` | 6 | 0 | 0 | 22 | 2 |

Every emitted result was `Escalation`; no `Report` was emitted. Consequently, decision agreement, missing-information quality, and corpus macro failed all runs. Escalation recall failed `001` and `002`; `003` recall and all three severity scores passed. This does not compensate for the zero true negatives and the failed critical dimensions.

## Proposal → resolution and verifier behavior

The complete event-level matrix from minimized `OutcomeResolved` audit rows was:

| Run | `Escalation → Escalation` | `Report.Done → Escalation` | `Report.Progress → ContinueWork` | `Report.Progress → Escalation` | Events | Verifier calls |
|---|---:|---:|---:|---:|---:|---:|
| `001` | 7 | 20 | 1 | 0 | 28 | 19 |
| `002` | 14 | 11 | 4 | 0 | 29 | 11 |
| `003` | 9 | 18 | 4 | 1 | 32 | 17 |
| **Total** | **30** | **49** | **9** | **1** | **89** | **47** |

All 47 verifier calls reached `gpt-5-mini-2025-08-07`, negotiated JSON Schema, and succeeded at the gateway boundary. There were no verifier timeouts, provider rejections, invalid provider responses, or cost-unavailable verifier calls. The verifier added US$0.058995 across 47 calls.

The resolver recorded 43 `verifier-disagreement` closures and four verifier-confirmed escalations. It never obtained enough authoritative positive completion proof to open a `Report`; free-text directives do not supply structured completion criteria or runtime evidence that can satisfy the deterministic `Report.Done` invariant. Fail-closed behavior therefore converted every initially report-shaped path to escalation. Across all 89 resolution events, 59 proposals were overridden.

Eight objective `deadline-exceeded` events were observed and all eight resolved to `Escalation`; objective-trigger downgrades were `0/8`. Nine intermediate `autonomous-action-available` events resolved to `ContinueWork`, and one later deadline converted a progress proposal to escalation.

## Authority, audit identity, and aggregation corrections

Every audited action gate in the three runs succeeded with `acting-under-declared` and `action-gate-declared-authority`, including both the original proposed `Report` and resolver-materialized `Escalation` messages. The v1 loss of `acting_under` is fixed.

The datasets project 50, 45, and 51 gateway calls respectively. Main inference and outcome verification have distinct `operation` identities at the same iteration; no case contains a duplicate `(operation, iteration)` key. All multi-call cases retain their individual rows: 20 in `001`, 15 in `002`, and 21 in `003`.

For every case with complete usage/cost, the aggregate exactly equals the sum of its gateway calls. Cost aggregate mismatches, usage aggregate mismatches, and duplicate operation/iteration keys were all zero in all runs. Missing provider cost remains `cost-unavailable`; it is never converted to zero. One interrupted multi-iteration `003` case preserves US$0.006721 of known partial cost while its aggregate remains unavailable.

## Cost and latency

| Run | Complete known cost | Avg complete known cost | Partial known cost | Combined gateway p50 | Combined gateway p95 | Main p50 | Main p95 | Verifier p50 | Verifier p95 | Journey p50 | Journey p95 | Resolver p50 | Resolver p95 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `001` | US$0.201699 | US$0.007470 | — | 41,425 ms | 54,443 ms | 35,114 ms | 44,551 ms | 7,262 ms | 12,730 ms | 41,663 ms | 54,513 ms | 6,475 ms | 11,824 ms |
| `002` | US$0.205135 | US$0.008205 | — | 39,599 ms | 69,993 ms | 34,104 ms | 41,627 ms | 6,866 ms | 10,721 ms | 40,947 ms | 70,072 ms | 16 ms | 7,377 ms |
| `003` | US$0.224552 | US$0.008020 | US$0.006721 | 40,131 ms | 64,963 ms | 33,350 ms | 41,429 ms | 7,095 ms | 9,978 ms | 41,143 ms | 944,582 ms* | 6,394 ms | 9,860 ms |

`*` The `003` journey p95 includes the external execution-window interruption and is not comparable. Provider and resolver latency values exclude that pause.

Complete known cost was US$0.631386 over 80 cases, averaging US$0.007892. Each run remained below the US$0.02 average-cost gate even after verifier costs were aggregated.

## Decision

The corrected T17f candidate is rejected for freeze:

- the verifier reachability/configuration defect is fixed: 47/47 calls reached and completed at the provider;
- authority preservation is fixed: every action gate retained declared `delivery.bug-triage` authority and no generated escalation was rejected;
- per-call audit identity and cost/usage aggregation are fixed with zero observed mismatches and no zero-filling of unavailable cost;
- nevertheless, scoreable and resolution coverage were below 30/30 in all runs because of ten main-provider timeouts;
- all 80 emitted messages were escalations, producing zero true-negative reports;
- decision agreement, missing-information quality, and corpus macro failed all three runs, while recall failed two runs;
- severity and average known cost passed but cannot compensate for the failed critical gates.

No new freeze or holdout is authorized, F1a remains closed, and the v2 run IDs must not be reused as independent measurements. The evidence shows that the remaining failure is no longer verifier connectivity, authority materialization, or accounting. The current deterministic positive-proof contract cannot validate `Report.Done` for this free-text directive surface, so further work requires a new product/contract decision about authoritative structured completion evidence rather than another unchanged calibration.

## Dataset hashes

| Dataset | SHA-256 |
|---|---|
| `hybrid-outcome-corrected-calibration-001.json` | `d240663b368898461fae92fbc1192eeca828e1e8ab87ec44520388002276947c` |
| `hybrid-outcome-corrected-calibration-002.json` | `1eb42512aa5a2bd387dd1bea4f7d549ff85c37f1226c39ffc46d5f403c88a726` |
| `hybrid-outcome-corrected-calibration-003.json` | `e7a13744cfa00c7b0d694b262782038940d987defd115b270c4e34de0ee29075` |
