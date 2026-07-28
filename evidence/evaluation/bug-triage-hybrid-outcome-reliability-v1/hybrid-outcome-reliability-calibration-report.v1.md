# Hybrid outcome-resolution reliability calibration report v1

Date: 2026-07-27  
Work item: `US-F0-13-T18h`  
Protocol outcome: **rejected — output reliability improved, semantic verification did not**

## Protocol and preflight

The authorized measurement used the isolated
`hybrid-outcome-resolution-reliability-v1` profile with
`gpt-5-mini-2025-08-07`, `triage-v2`, a 60-second main timeout, 8192 main
output tokens, the unchanged calibration corpus/baseline/rubric v1, and
outcome enforcement. The general real-provider smoke and the real
semantic `Report.Done` verifier smoke both passed before the corpus was
started.

The three runs were launched sequentially in one command without dataset
inspection or tuning between them:

- `hybrid-outcome-reliability-calibration-001`
- `hybrid-outcome-reliability-calibration-002`
- `hybrid-outcome-reliability-calibration-003`

No holdout was executed or inspected. Run `001` returned a non-zero runner
exit because one main call timed out, but still wrote its complete
30-case dataset. Runs `002` and `003` returned zero.

## Frozen identity

Git base: `8a115587968856a7e959b119e33f79420ea782ae`  
Built image: `sha256:006f3e38b86b162750cb335c3c3da9aabc74d2fbe71acad512de17fb9b22b7a0`
(`linux/amd64`)

| Input | SHA-256 |
|---|---|
| `docker-compose.evaluation.outcome-resolution-reliability.yml` | `10d9821c130604c13d80b9274cbee9a2122a7a38aede2ecf3c400fd2c85e82e6` |
| `config/experiments/hybrid-outcome-resolution-reliability-v1/organization.yaml` | `2d531b8fde0ee141d7c0e48562917a2c9ae9d2e29c2f01422204f0b62f4ac421` |
| `config/organizations/acme-delivery/prompts/triage-v2.md` | `7e96f79b53b05da339eceb6423986f85befefdf16427ececb543526ca203e0f7` |
| `bug-triage-corpus.v1.json` | `329c133848897503227b460fbd5beec3550149bc8c51e8a0dfb6dc138d5580f7` |
| `bug-triage-rubric.v1.json` | `7e046acb0e4d8ee51e881646ba7fa0881e8dbebf43d4e51273a7d2cf3d907d43` |
| `AiDirectiveOutcomeResolution.cs` | `b9f46519f8d9a4e8b087d2b22f664b5ab0cf5f34fb27605dba34d1aac6711c2d` |
| `OutcomeSemanticCompletionEligibility.cs` | `25e1c555ab6c0da39af4f86bfdcb13cdb43cc331557a7b67496570577e32629a` |
| `OutcomeVerifierConstraint.cs` | `9a3064963a158e5d98734378f2a45af9fc0ba35f485db69d8d475294f2799e40` |
| `OrganizationalOutcomeOrchestrator.cs` | `8e96e49eb585f377f037434e2d64fe6d7d2910f31fb922a484f22fb8775331eb` |
| `AiGatewayOutcomeVerifier.cs` | `8dca63df87a3d832f38a28a15a4f6fc873fdd310e9dad3e829e7c00b5e1eddb9` |
| `JourneyAuditAiGatewayPublisher.cs` | `0ae6014557e91a2a6d38b2bf2852e62371a4015213e30ccf3e0f9b7f989bf2c1` |
| `EvaluationAuditReader.cs` | `5411bfacf541d377947ce716a531839e4150cbcbaaa272f390b64d1f74377262` |

## Coverage and quality

| Run | Terminal | Scoreable | Corpus macro | Decision, full corpus | Escalation recall | Severity, full corpus | Missing information, full corpus |
|---|---:|---:|---:|---:|---:|---:|---:|
| `001` | 30/30 | 29/30 | 0.454346 | 0.366667 | 6/6 = 1.000000 | 0.700000 | 0.283846 |
| `002` | 30/30 | 30/30 | 0.453389 | 0.266667 | 6/6 = 1.000000 | 0.716667 | 0.350159 |
| `003` | 30/30 | 30/30 | 0.460530 | 0.366667 | 5/6 = 0.833333 | 0.700000 | 0.301514 |

The final-message matrices were:

| Run | Escalation → Escalation | Escalation → Report | Report → Report | Report → Escalation | Unclassified |
|---|---:|---:|---:|---:|---:|
| `001` | 6 | 0 | 5 | 18 | 1 |
| `002` | 6 | 0 | 2 | 22 | 0 |
| `003` | 5 | 1 | 6 | 18 | 0 |

Severity and known average cost passed in every run. Decision and corpus
macro failed by a large margin in every run, missing information passed
only `002`, escalation recall failed `003`, and mandatory 30/30
scoreable coverage failed `001`.

The main model's proposals were materially better than the enforced final
decisions but were not independently gate-ready:

| Run | Proposal agreement | Proposal escalation recall | Human escalation → proposal escalation | Human report → proposal report |
|---|---:|---:|---:|---:|
| `001` | 21/29 = 0.724138 | 4/6 = 0.666667 | 4 | 17 |
| `002` | 25/30 = 0.833333 | 2/6 = 0.333333 | 2 | 23 |
| `003` | 21/30 = 0.700000 | 3/6 = 0.500000 | 3 | 18 |

## Verifier behavior and the observed `Undetermined` results

The three datasets contain 60 verifier calls:

- 58 completed successfully at the gateway boundary with `finish_reason=Stop`;
- two timed out in run `002`;
- 44 resolutions closed as `verifier-disagreement`;
- 14 semantic candidates were verifier-confirmed and emitted as reports;
- no verifier call was provider-rejected or classified as invalid output.

The raw verifier responses observed during execution included repeated
valid `{"classification":"Undetermined"}` objects. This is not an empty
response and not a parser failure. The closed verifier instruction
explicitly requires `Undetermined` when the bounded evidence cannot
support either completion or a compatible intervention.

The semantic request is under-specified. It asks the verifier to decide
whether an assessment is complete, but the payload contains only:

- the original directive objective/context;
- authoritative execution facts and directive metadata;
- coarse proposal metadata such as `Report.Done`, `Completed`, evidence
  references, and `semantic_completion_candidate=true`.

It does **not** contain the proposed report or another bounded
representation of the assessment whose completion must be verified. A
model that follows the closed contract can therefore abstain: structural
eligibility proves that the semantic path may be used, but it does not
show that severity, impact, missing information, and next action were
actually assessed in the proposed artifact.

`OrganizationalOutcomeOrchestrator` correctly maps an `Undetermined`
classification to the fail-safe `Escalation` reason
`verifier-disagreement`. This safety behavior explains the high false
escalation count; it is not the defect.

The exact total of raw `Undetermined` classifications cannot be recovered
from the stored datasets. `OutcomeResolved` records only
`verifierInvoked` and the coarse `verifier-disagreement` reason. The same
reason is also used for a non-`Undetermined` classification that cannot be
reconciled with the authoritative resolver. The audit does not persist
the verifier's closed classification or the semantic-candidate flag.
Consequently, the four raw responses observed by the operator are
confirmed examples, but the 44 disagreements cannot all be labelled
`Undetermined` from persisted evidence. This is an observability gap.

## Reliability, cost, and latency

The reliability profile fixed the previous output-ceiling failure mode:
all main calls used the effective 60,000 ms / 8192-token limits, all
verifier calls used 15,000 ms / 2048 tokens, and no call ended as
`invalid-provider-response`.

| Run | Main failures | Verifier failures | Main p50 / p95 | Verifier p50 / p95 | Journey p50 / p95 | Avg complete known case cost |
|---|---:|---:|---:|---:|---:|---:|
| `001` | 1 timeout | 0 | 28,207 / 53,427 ms | 5,358 / 11,913 ms | 33,980 / 58,565 ms | US$0.009119 |
| `002` | 0 | 2 timeouts | 39,374 / 49,071 ms | 6,901 / 14,999 ms | 47,741 / 79,607 ms | US$0.009527 |
| `003` | 0 | 0 | 30,080 / 41,362 ms | 5,460 / 13,109 ms | 33,536 / 66,173 ms | US$0.009436 |

The single main timeout closed after 60,010 ms, so the earlier 117,943 ms
anomaly did not repeat and the effective deadline is now observable. The
verifier's fixed 15-second ceiling does censor its tail: run `002` reached
a 14,999 ms p95 and recorded two timeouts. Across the 90 cases, 87 had
complete known cost totalling US$0.814287 and averaging US$0.009360.
Known gateway-call cost was US$0.829307, including US$0.085880 for the
verifier.

## Decision

The T18h candidate is rejected for freeze:

- the 8192-token main-output reliability change is supported;
- the 60-second main timeout is enforced and observable, with one
  remaining timeout in 101 main calls;
- the semantic verifier still converts many report-shaped proposals to
  fail-safe escalation because it cannot inspect the assessment artifact;
- the audit cannot distinguish raw `Undetermined` from other semantic
  disagreements;
- the verifier's fixed 15-second timeout is too close to its observed
  latency tail;
- critical decision quality and corpus macro fail every run.

No holdout is authorized, no run ID may be reused, and F1a remains closed.
The next correction must be transversal to organizational positions: give
the verifier a bounded, minimized view of the proposed organizational
artifact, persist its closed classification and semantic-candidate state,
then calibrate a separately bounded verifier deadline before another
measurement. It must not add Bug Triage semantics to the runtime or move
responsibility from the user-controlled position prompt.

## Dataset hashes

| Dataset | SHA-256 |
|---|---|
| `hybrid-outcome-reliability-calibration-001.json` | `cb451f1bce7430178135a50433be9a9d2d3fee08febc712a26a0c95d1b86ac45` |
| `hybrid-outcome-reliability-calibration-002.json` | `183690e05419e28ad7fc76faa10fe3c6b7d56f6c4ef9105e19ef135e92f06d81` |
| `hybrid-outcome-reliability-calibration-003.json` | `4adc6cb94b4f28410d6204c1e76d0baef7126ec4e99affff8b1d50a39d37d278` |
