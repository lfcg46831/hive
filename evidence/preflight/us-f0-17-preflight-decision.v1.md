# US-F0-17 preflight decision v1

Date: 2026-07-31
Scope: `US-F0-17-T03`
Reviewed commit: `ba634f7037c2f9fe767175452bcddacf4d4246dc`
Reviewed tree: `1e80f53b8acac8ac6c231fc77eb91e499b674ef8`

## Inputs

| Input | Identity |
| --- | --- |
| Local qualification | `evidence/preflight/us-f0-17-local-qualification.v1.md` |
| Local qualification SHA-256 | `4488bbe62d02d458c475c174ed81e156108c78b852b428530e3d8bbe2bb1e62f` |
| Real-smoke evidence | `evidence/preflight/us-f0-17-real-smoke.v1.md` |
| Real-smoke evidence SHA-256 | `a5e9e1a72579e216979731deb637932f61089898485368c227a094b267a7bc6d` |
| Experiment manifest | `config/experiments/bug-triage-lab-v1/experiment.v1.json` |
| Manifest SHA-256 | `e3ee9c5911129395b34fd68611088206eaa33bfa56c94e9a6264793c6357697d` |
| Effective configuration SHA-256 | `e0e0389bbb391d13fb8f8f5bfb47f80baf417b1bb02e3310bb857eeccd5dfe0d` |
| Provider/model | `openai` / `gpt-5-mini-2025-08-07` |

## Closed gates

| Gate | Result |
| --- | --- |
| Local qualification on integrated commit | PASS — `ready-for-real-smoke` |
| Build | PASS — 0 warnings, 0 errors |
| Complete test suite | PASS — 2073/2073 |
| Architecture and characterization guards | PASS — 15/15 and 12/12 |
| Manifest preparation, hash validation and Compose rendering | PASS |
| General real-provider smoke | PASS — 1/1 |
| Semantic `Report.Done` verifier smoke | PASS — 1/1 |
| Provider/model and source identity | PASS |
| Secret/output/corpus minimization | PASS |

The corrective real-smoke source hash is contained in the reviewed commit and
the repeated local qualification covers that commit. The manifest and effective
configuration fingerprints match across both evidence inputs. No gate is
missing, skipped, divergent, or partial.

## Decision

`eligible-to-request-calibration`

This state permits only a separate request for calibration authorization. It
does not start a runner, submit a corpus case, promote configuration, compare
quality, alter thresholds, freeze a candidate, consume holdout, or reopen F1a.

## Prepared calibration authorization request

Status: `prepared-not-authorized`

Requested future scope:

- execute exactly three sequential calibration runs without intermediate
  inspection or tuning;
- use proposed fresh run ids `post-f0-8-calibration-001`,
  `post-f0-8-calibration-002`, and `post-f0-8-calibration-003`;
- preserve the manifest, organization, prompt, provider/model, limits, policy,
  corpus, baseline and rubric identified above;
- publish a separate content-addressed calibration report;
- execute no holdout.

The proposed ids have not been launched or consumed. A future calibration
requires explicit authorization after this decision.

## Evidence minimization

This report contains no credential, prompt, provider output, reasoning, corpus
content, organizational content, or provider error body.
