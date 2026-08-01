# US-F0-20 preflight decision v1

Date: 2026-08-01  
Scope: `US-F0-20-T03`  
Reviewed commit: `b484551bf3f8dbf2f59a13b94638e4a2dea72f41`  
Reviewed tree: `f121af94be0f76cab82712a6658fd13c7c300f7a`

## Inputs

| Input | Identity |
| --- | --- |
| Local qualification | `evidence/preflight/us-f0-20-local-qualification.v1.md` |
| Local qualification SHA-256 | `e49a56bb7a80a6d89432aea38c70b517e11da4846eac8abccae87b4282280f02` |
| Real-smoke evidence | `evidence/preflight/us-f0-20-real-smoke.v1.md` |
| Real-smoke evidence SHA-256 | `47fa31051244f14c18407fb6bf6d340507974d9d041bf18c43893376dc61bdcd` |
| Experiment manifest | `config/experiments/bug-triage-lab-v2/experiment.v1.json` |
| Manifest SHA-256 | `a51e80a8492cfa27413c9d6ee5d334f88c695aace7dbe45c1864b2f4a90c19f0` |
| Effective configuration SHA-256 | `cabdca7c16a693f707d1406932587947b2ae55897a71d084105e506f4507ea2f` |
| Provider/model | `openai` / `gpt-5-mini-2025-08-07` |

## Closed gates

| Gate | Result |
| --- | --- |
| Local qualification on integrated commit | PASS — `ready-for-real-smoke` |
| Build | PASS — 0 warnings, 0 errors |
| Complete solution suite | PASS — 2138/2138, no skips |
| PostgreSQL Testcontainers coverage omitted at T05 | PASS — all 67 fixtures executed |
| Architecture, characterization, and checkpoint guards | PASS — 16/16, 13/13, and 29/29 |
| Manifest preparation, hash validation, and Compose rendering | PASS |
| General real-provider smoke | PASS — 1/1 |
| Semantic `Report.Done` verifier smoke | PASS — 1/1 |
| Commit/tree, provider/model, manifest, and effective configuration identity | PASS |
| Secret/output/corpus minimization | PASS |
| Proposed calibration run-id absence | PASS |

The functional source, tests, configuration, and active Compose adapters were
unchanged from the reviewed commit throughout both gates. The two evidence
inputs match the same commit, tree, manifest, effective configuration, and
provider/model. No gate is missing, skipped, divergent, or partial.

## Decision

`eligible-to-request-calibration`

This state permits only a separate request for calibration authorization. It
does not start a runner, submit a corpus case, promote configuration, compare
quality, alter thresholds, freeze a candidate, consume holdout, or reopen F1a.

## Prepared calibration authorization request

Status: `prepared-not-authorized`

Requested future scope:

- execute exactly three sequential calibration runs without intermediate
  inspection, retry, or tuning;
- use proposed fresh run ids `post-f0-10-calibration-001`,
  `post-f0-10-calibration-002`, and `post-f0-10-calibration-003`;
- preserve commit/tree, manifest, organization, prompt, provider/model, limits,
  policy, corpus, baseline, rubric, and thresholds identified above;
- publish raw datasets through the existing content-addressed policy and create
  a separate aggregate report linked to all frozen identities;
- execute no freeze or holdout.

No artifact or evidence contained any proposed id before this decision. The
ids have not been launched or consumed. Calibration requires explicit user
authorization after this decision.

## Evidence minimization

This report contains no credential, prompt, provider output, reasoning, corpus
content, organizational content, or provider error body.
