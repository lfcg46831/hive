# T18l minimized diagnostic report v1

Date: 2026-07-27  
Work item: `US-F0-13-T18l`  
Source measurement: `US-F0-13-T18k`  
Protocol outcome: **two independent upstream causes identified; no calibration or holdout executed**

## Scope and evidence

This diagnosis uses the three immutable T18k datasets and the provider-neutral
runtime contracts that produced them. It does not reinterpret verifier text,
inspect provider reasoning, modify `triage-v2`, rerun a burned id, or start a
new corpus.

| Dataset | SHA-256 |
|---|---|
| `hybrid-outcome-verifier30-calibration-001.json` | `151ccf3ad62acb6343de742aeaf849c2a3fa16d20d173a4018f30f576581fa37` |
| `hybrid-outcome-verifier30-calibration-002.json` | `a9bd57c8329457aad1c1a7f767e5fc4c19be9eb6caf701aa77bf4820f18c88fd` |
| `hybrid-outcome-verifier30-calibration-003.json` | `2df7c606548b3f76c651f00e3bdc7ac96ee879b3a364adcb8605eeaab9cf14c8` |

## Finding 1 — the 24 `Undetermined` classifications are upstream proposal-grounding failures

All 24 relevant rows have the same observable closed shape:

- proposed intent `Report.Done`;
- work state `Completed`;
- required intervention `None`;
- `semantic_completion_candidate=false`;
- verifier classification `Undetermined`;
- final reason `verifier-disagreement`.

Counts and cases:

| Run | Count | Cases |
|---|---:|---|
| `001` | 8 | `triage-006`, `007`, `012`, `013`, `020`, `024`, `028`, `029` |
| `002` | 6 | `triage-004`, `005`, `016`, `019`, `020`, `029` |
| `003` | 10 | `triage-006`, `007`, `011`, `013`, `015`, `016`, `019`, `026`, `027`, `029` |

The code-version invariants narrow the failed predicate deterministically:

1. `OutcomeProposalRules` accepts `Report.Done` only with `Completed`, no
   intervention, no blockers, no next action, and at least one evidence
   reference.
2. The current AI integration creates an empty structured directive contract,
   so no structured completion criterion can block this limited path.
3. `ExecutionFactsMaterializer` therefore emits `completion_state=NotDeclared`.
4. The only remaining eligibility checks are that every evidence item has
   source `DirectiveInput` and references a key present in the bounded
   verification context.

Consequently, every one of these 24 cases failed one or both of:

- `evidence-source-not-directive-input`;
- `evidence-reference-not-in-context`.

The frozen datasets do not contain the concrete evidence source/reference, by
design, so they cannot distinguish the two codes per historical case. They do
contain enough information to exclude the other eligibility predicates. T18l
adds only the two applicable closed reason codes to future audit rows; it does
not persist the source or reference value.

Four additional `Report.Done` proposals were ineligible but were pre-empted by
the authoritative deadline before a verifier result: `001/triage-005`,
`001/triage-015`, `002/triage-009`, and `002/triage-015`.

### Ownership assessment

This is not a verifier-artifact or verifier-timeout defect: every eligible
proposal that reached the corrected verifier was accepted as `Report.Done`.
The failure is in the system-owned `outcome_proposal` grounding contract
produced by the main inference. The generic system instruction already tells a
`Report.Done` proposal to cite bounded `DirectiveInput` references. Nothing in
the T18k evidence justifies changing the user-controlled `triage-v2` business
prompt. A future correction should evaluate a transversal contract-enforcement
option separately from any user prompt calibration.

## Finding 2 — continuation inference is not capped by the remaining directive deadline

Nine final resolutions contain `deadline-exceeded`. Every case made two or
three successful main-model calls, made no verifier call, and gave every main
call the original 60,000 ms timeout:

| Run | Case | Human decision | Final proposal | Calls (latency / applied timeout) | Journey ms |
|---|---|---|---|---|---:|
| `001` | `triage-005` | escalation | `Report.Done` | `39406/60000`; `35386/60000` | 74862 |
| `001` | `triage-014` | report | `Report.Done` | `46612/60000`; `42169/60000` | 88911 |
| `001` | `triage-015` | escalation | `Report.Done` | `37568/60000`; `44863/60000` | 82501 |
| `001` | `triage-021` | report | `Report.Done` | `45695/60000`; `55608/60000` | 101463 |
| `001` | `triage-030` | escalation | `Report.Progress` | `43059/60000`; `48210/60000` | 91369 |
| `002` | `triage-009` | report | `Report.Done` | `40623/60000`; `27816/60000` | 68507 |
| `002` | `triage-012` | report | `Escalation` | `33850/60000`; `36938/60000` | 70846 |
| `002` | `triage-015` | escalation | `Report.Done` | `22987/60000`; `34372/60000`; `32795/60000` | 90266 |
| `003` | `triage-017` | report | `Report.Done` | `33866/60000`; `34094/60000` | 68086 |

The main-call latency total ranges from 67,960 to 101,303 ms and the journey
duration from 68,086 to 101,463 ms. Five of the nine reference decisions were
reports and became false escalations; four were reference escalations.

The runtime source confirms the mechanism:

- `AiDirectiveIterationState.EvaluateInference` checks the overall deadline
  before starting a continuation;
- `AiDirectiveIterationExecutor.CreateInferenceRequest` then copies the
  original `request.Timeout` unchanged;
- a continuation that begins just before the overall deadline can therefore
  consume another full provider timeout and is classified as
  `deadline-exceeded` only after the successful call returns.

This is a transversal deadline-enforcement gap. It is independent of
`triage-v2`, the verifier, severity, missing-information labels, and business
function semantics. The safe corrective option is to cap every continuation
inference request by the remaining iteration/directive deadline, following the
same minimum-bound principle already applied to the verifier. That behavior
change is outside T18l and requires a separately specified task.

## T18l observability delivered

Without changing outcome behavior, T18l adds:

- closed, ordered semantic-completion ineligibility reason codes;
- reason count/codes in minimized audit data, without evidence values;
- `deadline_remaining_ms`, clamped to zero and omitted when no deadline exists;
- all per-iteration `outcome_resolution_steps`, while preserving the existing
  final `outcome_resolution` field;
- historical compatibility when the new fields are absent;
- aggregate ineligibility reason counts in run analysis.

## Decision

Do not run another calibration yet.

1. The verifier correction from T18i/T18j remains valid.
2. Treat the continuation-timeout behavior as a confirmed transversal runtime
   bug and specify its fix separately.
3. Decide separately whether proposal evidence grounding should be enforced by
   a dynamic provider-neutral constraint/parser contract or measured as model
   compliance; do not place that wire-contract responsibility in `triage-v2`.
4. Only a post-fix, newly authorized measurement with fresh ids can determine
   whether the end-to-end gates improve.
