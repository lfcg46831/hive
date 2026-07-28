# Hybrid outcome 60-second exploratory report v1

Date: 2026-07-26  
Work items: `US-F0-13-T18c` / `US-F0-13-T18d`  
Protocol outcome: **aborted exploratory run — not a comparable calibration**

## Decision

The authorized three-run block was interrupted after the user observed repeated
`Undetermined` verifier outputs and explicitly requested an intermediate analysis. This
observation and inspection invalidate the no-inspection calibration protocol. The result does
not decide whether the 60-second main-model timeout is suitable.

Only `hybrid-outcome-timeout60-calibration-001` wrote a complete dataset. Run `002` had
started but wrote no dataset when the block was stopped, and `003` did not start. All three IDs
are burned. No holdout was run or inspected.

The exploratory evidence found two clear systemic defects:

1. The real verifier smoke accepted any closed classification, so `Undetermined` incorrectly
   passed preflight.
2. The verifier had to reconstruct the limited semantic-completion eligibility from several
   fields while operating under a 1024-token reasoning/output ceiling. All 16 successful
   verifier calls returned `Undetermined`; three further calls ended as
   `invalid-provider-response` and one as `provider-rejected`. Successful calls consumed up to
   926 output tokens, leaving little headroom.

The main proposal also missed the position-specific decision target. That is calibration
feedback for the user-controlled `triage-v2` prompt, not a systemic runtime bug.

## Preflight and frozen exploratory identity

Before the corpus block, the full solution passed with 2,037 tests (1,980 + 57), and the
general provider and then-current closed-classification verifier smokes passed against
`gpt-5-mini-2025-08-07`. The isolated API reported ready with
`identityPromptRef=triage-v2`, `maxTokens=4096`, four iterations,
`delivery.bug-triage` authority, `PT60S` position timeout, and
`Hive:Outcomes:Mode=enforcement`.

Git base: `8a115587968856a7e959b119e33f79420ea782ae`  
Built image: `sha256:d908b929e59c08cf44c96c64d2daa3748863ab84768279945ef2c7cacd6b25ca`
(`linux/amd64`)

| Frozen input | SHA-256 |
|---|---|
| `docker-compose.evaluation.outcome-resolution-timeout60.yml` | `d7e937f46d073c98a956241d69e095f07ca4dbb524929812a90e1c18a46a6d78` |
| `config/experiments/hybrid-outcome-resolution-timeout60-v1/organization.yaml` | `7f66fe90d6a90fa96685810ea87fc5120e6565925485795548f2f5bc88ec8f68` |
| `config/organizations/acme-delivery/prompts/triage-v2.md` | `e804a156c6e5099ff4ae297f01a8c4d896b9c5a5d4b733eecc6a8e0c6b556d0a` |
| `bug-triage-corpus.v1.json` | `329c133848897503227b460fbd5beec3550149bc8c51e8a0dfb6dc138d5580f7` |
| `bug-triage-rubric.v1.json` | `7e046acb0e4d8ee51e881646ba7fa0881e8dbebf43d4e51273a7d2cf3d907d43` |
| `OutcomeVerifierConstraint.cs` | `a8bc990081bcb3dc7148bd015dd4d17b32148d0ca88afa84454ad59e30e8aa13` |
| `AiGatewayOutcomeVerifier.cs` | `5ab45027de923e30182373fbbac12a377f932b8a8afe3f5617c926e208b3387d` |

## Coverage and quality

| Measure | Exploratory `001` |
|---|---:|
| Submitted / accepted | 30/30 |
| Auditable terminals | 30/30 |
| Result messages | 28/30 |
| Scoreable cases | 28/30 |
| Outcome-resolution projections | 28/30 |
| Main timeout | 1 |
| Main invalid provider response | 1 |
| Decision, full corpus | 0.200000 |
| Decision, scoreable | 6/28 = 0.214286 |
| Escalation recall | 6/6 = 1.000000 |
| Severity | 0.616667 |
| Missing information | 0.231230 |
| Corpus macro | 0.356764 |

Severity and average known cost passed their thresholds. Decision, complete scoreable
coverage, missing information, and corpus macro failed. The single inspected run is
insufficient for a timeout decision even without the protocol interruption.

The final-message decision matrix was:

| Expected | Final report | Final escalation | Unclassified |
|---|---:|---:|---:|
| Report | 0 | 22 | 2 |
| Escalation | 0 | 6 | 0 |

## Proposal and verifier behavior

The 28 projected cases had the following main-proposal matrix before hybrid resolution:

| Expected | Proposed report | Proposed escalation |
|---|---:|---:|
| Report | 18 | 4 |
| Escalation | 2 | 4 |

Main-proposal agreement was therefore 22/28 = 0.785714, with proposal escalation recall
4/6 = 0.666667. Because direct escalation proposals resolve without verifier review, the four
false-positive escalation proposals already made the 0.90 decision threshold unreachable in
this run. This is position-prompt calibration evidence rather than a generic resolver defect.

The event-level proposal-to-resolution matrix was:

| Path | Count |
|---|---:|
| `Escalation → Escalation` (`proposal-escalation`) | 8 |
| `Report.Done → Escalation` (`verifier-disagreement`) | 16 |
| `Report.Done → Escalation` (`verifier-unavailable`) | 4 |

There were 20 verifier calls: 16 succeeded at the gateway boundary, three failed as
`invalid-provider-response`, and one as `provider-rejected`. The user-observed output for the
successful calls was repeatedly the valid closed object with
`classification: "Undetermined"`. The resolver correctly applied ADR-009 fail-safe behavior,
but the resulting 20 report-to-escalation conversions made the final output unusable.

## Cost and latency

| Measure | Exploratory `001` |
|---|---:|
| Cases with complete known aggregate cost | 24/30 |
| Complete known aggregate cost | US$0.219016 |
| Average complete known cost | US$0.009126 |
| Known partial cost in unavailable aggregates | US$0.031162 |
| All known gateway-call cost | US$0.250178 |
| Verifier known cost | US$0.025559 |
| Main gateway p50 / p95 | 28,834 ms / 55,206 ms |
| Main gateway max | 117,943 ms |
| Verifier p50 / p95 | 6,744 ms / 11,214 ms |
| Verifier max | 14,091 ms |
| Journey p50 / p95 | 35,976 ms / 68,235 ms |
| Journey max | 118,117 ms |

The run observed one remaining main timeout. The main p95 stayed below 60 seconds, but the
timeout/retry path reached the runner's 120-second boundary. These values are descriptive only;
the interrupted single-run sample cannot accept or reject the 60-second profile.

## Systemic correction and validation

`US-F0-13-T18d` keeps ADR-009 fail-safe semantics and makes the limited attestation path
explicit and testable:

- `OutcomeSemanticCompletionEligibility` is the single domain rule for the closed
  `NotDeclared` + grounded `DirectiveInput` eligibility conditions.
- The verifier payload receives only the derived
  `proposal.semantic_completion_candidate` boolean; the verifier still makes the bounded
  semantic classification, and the orchestrator reapplies the same domain rule before
  materializing `SemanticallyVerified`.
- The verifier-specific ceiling is 2048 tokens, still bounded below the 4096-token main output.
- The real smoke now exercises the exact semantic-completion path and requires
  `Report.Done`; `Undetermined` fails preflight.

Post-fix code identity:

| Corrected input | SHA-256 |
|---|---|
| `OutcomeVerifierConstraint.cs` | `9a3064963a158e5d98734378f2a45af9fc0ba35f485db69d8d475294f2799e40` |
| `OutcomeSemanticCompletionEligibility.cs` | `25e1c555ab6c0da39af4f86bfdcb13cdb43cc331557a7b67496570577e32629a` |
| `OrganizationalOutcomeOrchestrator.cs` | `8e96e49eb585f377f037434e2d64fe6d7d2910f31fb922a484f22fb8775331eb` |
| `AiGatewayOutcomeVerifier.cs` | `8dca63df87a3d832f38a28a15a4f6fc873fdd310e9dad3e829e7c00b5e1eddb9` |
| `AiGatewayIntegrationTests.cs` | `bbc58407d26f5c9cd85e7d917ec1640df8ba2c2fb2cd0173551cfe3b61d2b6bc` |

Validation after the correction:

- 61 focused verifier/orchestrator/prompt tests passed.
- The corrected semantic verifier smoke passed three consecutive real calls and returned the
  required `Report.Done`.
- The full solution passed with 2,039 tests (1,982 + 57), zero failures.
- The normal Docker profile was restored after preserving the exploratory dataset.

No new corpus calibration or holdout is authorized by this report. A future `T18e` measurement
requires explicit authorization, new run IDs and evidence directory, a rebuild from the
post-fix identity, both real smokes, and three runs without intermediate inspection or tuning.

## Dataset hash

| Dataset | SHA-256 |
|---|---|
| `hybrid-outcome-timeout60-calibration-001.json` | `e704f6a29235c1fb737873ca1c021304a55121b43c7cc0d8d55b5f9a58addce6` |
