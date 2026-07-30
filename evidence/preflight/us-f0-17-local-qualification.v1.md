# US-F0-17 post-F0.8 local qualification v1

Date: 2026-07-30  
Scope: `US-F0-17-T01`  
Reviewed commit: `eee0b21fd782043b06e1dfb26d8ee4d0c2321472`  
Reviewed tree: `1a5aedbfc96293128b1d8b43202e5841feb6fdb1`

## Isolation

- No AI provider endpoint, API credential, evaluation host, runner, corpus case,
  scoring, calibration, freeze, or holdout was used.
- The functional worktree under `src/`, `tests/`, `config/`, and the active
  Compose adapters was unchanged from the reviewed commit.
- `experiment prepare` wrote only ignored files under
  `artifacts/evaluation/experiments/bug-triage-lab-v1/`.
- Compose validation used `.env.example` plus a non-secret interpolation
  placeholder and `config --quiet`; it did not read `.env` or create containers.

## Reproducibility identity

| Input | Identity |
| --- | --- |
| Experiment manifest | `config/experiments/bug-triage-lab-v1/experiment.v1.json` |
| Manifest SHA-256 | `e3ee9c5911129395b34fd68611088206eaa33bfa56c94e9a6264793c6357697d` |
| Manifest status | `prepared` |
| Effective configuration SHA-256 | `e0e0389bbb391d13fb8f8f5bfb47f80baf417b1bb02e3310bb857eeccd5dfe0d` |
| Provider/model declared by manifest | `openai` / `gpt-5-mini-2025-08-07` |

The generated manifest identity matched the exact manifest file hash. The
generated Compose env contained only experiment selector/configuration keys and
no credential-like key.

## Gates

| Gate | Result |
| --- | --- |
| Complete solution suite on the reviewed commit | PASS — operator-confirmed green |
| Solution build (`dotnet build Hive.sln --no-restore -v minimal`) | PASS — 0 warnings, 0 errors |
| Architecture/boundary guards | PASS — 43/43 |
| Directive execution characterization | PASS — 12/12 |
| Evaluation tooling tests | PASS — 74/74 |
| DemoClient tests | PASS — 4/4 |
| Manifest preparation and hash validation | PASS |
| Generated env credential-key check | PASS |
| Generic Compose configuration (`config --quiet`) | PASS |
| Functional worktree diff | PASS — empty |

Two independent attempts to replay the unfiltered solution suite from this
agent environment reached the runner timeout without a terminal test summary
or assertion failure and left no Testcontainers running. Their orphaned
`dotnet`/`testhost` processes were stopped. These attempts are recorded as
inconclusive diagnostics, not as failures or as substitutes for the
operator-confirmed complete green run.

## Verdict

`ready-for-real-smoke`

This verdict permits only a request to authorize `US-F0-17-T02`. It does not
authorize or represent a real-provider smoke, corpus execution, calibration,
freeze, holdout, go/no-go decision, or reopening of F1a.
