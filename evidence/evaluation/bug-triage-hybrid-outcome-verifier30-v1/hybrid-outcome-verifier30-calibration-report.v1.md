# Hybrid outcome-resolution verifier30 calibration report v1

Date: 2026-07-27  
Work item: `US-F0-13-T18k`  
Protocol outcome: **rejected for freeze — verifier corrections validated, end-to-end quality still below gate**

## Protocol and preflight

The authorized measurement used the isolated
`docker-compose.evaluation.outcome-resolution-verifier30.yml` profile with
`gpt-5-mini-2025-08-07`, `triage-v2`, a 60-second/8192-token main call,
a 30-second/2048-token verifier call, the unchanged calibration
corpus/baseline/rubric v1, and outcome enforcement. The profile reused the
frozen T18h reliability organization; it changed only the systemic verifier
deadline relative to that profile.

The general real-provider smoke and the real semantic `Report.Done` verifier
smoke both passed before the corpus was started. The effective runtime
configuration reported `triage-v2`, `gpt-5-mini-2025-08-07`, 8192 main output
tokens, outcome enforcement, and a 30-second verifier deadline.

The following three new runs were launched sequentially in one process. No
dataset or log was inspected and no code, prompt, corpus, rubric, model,
policy, timeout, or configuration was changed between runs:

- `hybrid-outcome-verifier30-calibration-001`
- `hybrid-outcome-verifier30-calibration-002`
- `hybrid-outcome-verifier30-calibration-003`

All three runner invocations returned zero and wrote complete datasets. No
holdout was executed or inspected.

## Frozen identity

Git base: `8a115587968856a7e959b119e33f79420ea782ae`  
Pre-run tracked diff identity: `c6d4682a96e533bf75b2fe9ccc93a668d6008c65`  
Built image: `sha256:cda7df4f93f6537be4e1dc7a6a236d750345b4a5fcdacda7509ae2a40d99de32`
(`linux/amd64`)

| Input | SHA-256 |
|---|---|
| `docker-compose.evaluation.outcome-resolution-verifier30.yml` | `7fb9b8a969130ee3d7d3724ca107602fa0b9f64620744e55865392505574b1f0` |
| `config/experiments/hybrid-outcome-resolution-reliability-v1/organization.yaml` | `2d531b8fde0ee141d7c0e48562917a2c9ae9d2e29c2f01422204f0b62f4ac421` |
| `config/organizations/acme-delivery/prompts/triage-v2.md` | `7e96f79b53b05da339eceb6423986f85befefdf16427ececb543526ca203e0f7` |
| `bug-triage-corpus.v1.json` | `329c133848897503227b460fbd5beec3550149bc8c51e8a0dfb6dc138d5580f7` |
| `bug-triage-rubric.v1.json` | `7e046acb0e4d8ee51e881646ba7fa0881e8dbebf43d4e51273a7d2cf3d907d43` |
| `AiDirectiveOutcomeResolution.cs` | `14b555d79e213a5f9b25a299ff24e0639228c210b3ffe480fa0a310ac45e61fa` |
| `OutcomeVerificationContracts.cs` | `d41122b85d42650dbd3450d0fd93bdc4d055ba0808fba211429026357cba4dfc` |
| `OutcomeVerifierConstraint.cs` | `68b731dc319e669a9188cbcd2ec314f26f86c89d682e70a517871c62a31635a9` |
| `OrganizationalOutcomeOrchestrator.cs` | `50a0cb5e112b5f3c7998a9d99adef3eaac28bb3ab3e12c62194e8900e843e34d` |
| `AiGatewayOutcomeVerifier.cs` | `424a23302f3f3e947b04e1471c9fbd3210b2a3f19a91bfcd042603a2cfba4cc2` |
| `EvaluationAuditReader.cs` | `40e851ebc22a4f92d306632c2609afd3accceb8ae1d08de60c14113e2fe8963c` |
| `EvaluationRunAnalysis.cs` | `c1490c37b04ea60adeb2d234dc83b811879735bc21f28184de141d345ef4b477` |

## Coverage and quality

| Run | Terminal | Scoreable | Corpus macro | Decision | Escalation recall | Severity | Missing information | Avg known case cost |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `001` | 30/30 | 30/30 | 0.557211 | 0.600000 | 5/6 = 0.833333 | 0.633333 | 0.444413 | US$0.010326 |
| `002` | 30/30 | 30/30 | 0.482660 | 0.500000 | 5/6 = 0.833333 | 0.700000 | 0.250457 | US$0.010330 |
| `003` | 30/30 | 30/30 | 0.508626 | 0.466667 | 4/6 = 0.666667 | 0.716667 | 0.336550 | US$0.009218 |

The final-message matrices were:

| Run | Escalation → Escalation | Escalation → Report | Report → Report | Report → Escalation |
|---|---:|---:|---:|---:|
| `001` | 5 | 1 | 13 | 11 |
| `002` | 5 | 1 | 10 | 14 |
| `003` | 4 | 2 | 10 | 14 |

Every run achieved full terminal, explicit-cost and scoreable coverage.
Severity and average known cost passed in every run. Missing information
passed only run `001`. Decision agreement, escalation recall and corpus macro
failed in every run; therefore no run satisfied the frozen gate.

The main proposal matrices, before deterministic resolution and semantic
verification, were:

| Run | Proposal agreement | Proposal escalation recall | Human escalation → proposal escalation | Human report → proposal report |
|---|---:|---:|---:|---:|
| `001` | 25/30 = 0.833333 | 2/6 = 0.333333 | 2 | 23 |
| `002` | 19/30 = 0.633333 | 3/6 = 0.500000 | 3 | 16 |
| `003` | 21/30 = 0.700000 | 2/6 = 0.333333 | 2 | 19 |

Proposal quality remains unstable and is not independently gate-ready.

## Exact verifier behavior

The new minimized audit fields make the verifier outcome fully observable.
Across the 90 cases:

- 61 verifier calls completed successfully with `finish_reason=Stop`;
- zero verifier calls timed out, failed, or produced invalid output;
- 37 calls classified `Report.Done`;
- 24 calls classified `Undetermined`;
- no other verifier classification occurred.

| Run | Calls | `Report.Done` | `Undetermined` | Failures/timeouts | Verifier p50 / p95 |
|---|---:|---:|---:|---:|---:|
| `001` | 22 | 14 | 8 | 0 | 11,042 / 13,476 ms |
| `002` | 17 | 11 | 6 | 0 | 6,821 / 11,246 ms |
| `003` | 22 | 12 | 10 | 0 | 7,081 / 10,891 ms |

The aggregate verifier p50/p95/p99/max was
7,894/13,056/13,708/13,708 ms. Every call carried the effective
30,000 ms deadline and 2048-token ceiling. The T18j deadline change
eliminated the two 15-second boundary timeouts seen in T18h and left
measurable headroom in this sample.

The artifact correction also behaved deterministically:

- 40 final resolutions were structurally eligible semantic-completion
  candidates;
- 37 reached the verifier and every one classified `Report.Done`;
- the remaining three were pre-empted by an authoritative deadline gate
  before verification;
- all 24 `Undetermined` classifications occurred with
  `semantic_completion_candidate=false`;
- no structurally eligible verifier call returned `Undetermined`.

This validates the T18i hypothesis: once the closed eligibility path is true,
the bounded proposed artifact supplies enough evidence for the verifier to
confirm completion. The remaining `Undetermined` results are not caused by a
missing artifact, empty output, parser failure, or verifier timeout. They are
the contractually safe response to a main proposal that did not satisfy the
closed semantic-completion eligibility conditions.

The minimized dataset does not persist proposal evidence values or a
per-predicate eligibility diagnostic, so it cannot distinguish which closed
eligibility predicate failed in each of those 24 cases without weakening the
existing data-minimization boundary. That residual cause must be diagnosed
before deciding whether the correction belongs to systemic proposal
construction/validation or to the user-controlled position prompt.

## Reliability, deadlines, cost and latency

All 102 main-model calls and all 61 verifier calls completed successfully
with `finish_reason=Stop`. Main calls used 60,000 ms/8192 tokens; verifier
calls used 30,000 ms/2048 tokens. No provider rejection, empty response,
invalid output or gateway timeout occurred.

| Run | Main calls | Verifier calls | Main p50 / p95 | Verifier p50 / p95 | Journey p50 / p95 | Known case cost |
|---|---:|---:|---:|---:|---:|---:|
| `001` | 35 | 22 | 41,696 / 55,608 ms | 11,042 / 13,476 ms | 51,996 / 91,369 ms | US$0.309778 |
| `002` | 36 | 17 | 27,615 / 41,127 ms | 6,821 / 11,246 ms | 34,648 / 70,846 ms | US$0.309915 |
| `003` | 31 | 22 | 30,143 / 41,730 ms | 7,081 / 10,891 ms | 36,039 / 48,752 ms | US$0.276545 |

Nine final resolutions carried the authoritative `deadline-exceeded` reason
after multi-iteration main-model work, despite every individual gateway call
succeeding. Five of those nine cases were human-reference reports and became
false escalations. This is separate from the verifier deadline and explains
part of the remaining decision error and journey tail.

All 90 cases had known complete cost. Total known cost was US$0.896238,
averaging US$0.009958 per case; verifier cost was US$0.109100. The frozen
US$0.02 average-case gate passed.

## Decision

The T18k candidate is rejected for freeze:

- T18i is technically validated: every eligible artifact-backed semantic
  verification completed as `Report.Done`;
- T18j is technically validated: no verifier timeout occurred and the
  effective 30-second limit was observed on every call;
- output reliability, coverage and cost are healthy;
- end-to-end decision agreement, escalation recall and corpus macro fail in
  all three runs;
- 24 ineligible `Report.Done` proposals safely become `Undetermined` and then
  fail-safe escalation;
- nine multi-iteration cases end at the authoritative directive deadline.

No holdout is authorized, the three run ids are burned, F1a remains closed,
and this measurement must not be rerun. The next change requires a separate
decision after a minimized diagnosis of why `semantic_completion_candidate`
is false and why triage proposals enter unnecessary extra iterations. It must
preserve the separation between generic HIVE proposal/outcome contracts and
the user-controlled organizational prompt.

## Dataset hashes

| Dataset | SHA-256 |
|---|---|
| `hybrid-outcome-verifier30-calibration-001.json` | `151ccf3ad62acb6343de742aeaf849c2a3fa16d20d173a4018f30f576581fa37` |
| `hybrid-outcome-verifier30-calibration-002.json` | `a9bd57c8329457aad1c1a7f767e5fc4c19be9eb6caf701aa77bf4820f18c88fd` |
| `hybrid-outcome-verifier30-calibration-003.json` | `2df7c606548b3f76c651f00e3bdc7ac96ee879b3a364adcb8605eeaab9cf14c8` |
