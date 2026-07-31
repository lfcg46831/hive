# US-F0-17 post-F0.8 local qualification v1

Date: 2026-07-30  
Scope: `US-F0-17-T01`  
Reviewed commit: `ada96f8efa355e50dd75cbdd95b4785c56c58fb6`<br>
Reviewed tree: `5323586bf2e191dd1854acd82c88443bdb2264e1`

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
| Complete solution suite on the reviewed commit | PASS — 2069/2069 |
| Solution build (`dotnet build Hive.sln --no-restore -v minimal`) | PASS — 0 warnings, 0 errors |
| Architecture/boundary guards | PASS — 15/15 |
| Directive execution characterization | PASS — 12/12 |
| Evaluation tooling tests | PASS — 74/74 |
| DemoClient tests | PASS — 4/4 |
| Manifest preparation and hash validation | PASS |
| Generated env credential-key check | PASS |
| Generic Compose configuration (`config --quiet`) | PASS |
| Functional worktree diff | PASS — empty |

## Commands

```powershell
dotnet clean Hive.sln -v minimal
dotnet build Hive.sln --no-restore -v minimal
dotnet test Hive.sln --no-build --no-restore -v minimal
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-build --no-restore -v minimal `
  --filter "FullyQualifiedName~ApplicationBoundaryTests|FullyQualifiedName~AuditExportContractTests"
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-build --no-restore -v minimal `
  --filter "Category=DirectiveExecutionCharacterization"

$manifest='config/experiments/bug-triage-lab-v1/experiment.v1.json'
dotnet run --project src/Hive.Evaluation.Tooling --no-restore -- `
  experiment prepare --manifest $manifest
$experimentEnv='artifacts/evaluation/experiments/bug-triage-lab-v1/compose.env'
$env:OPENAI_API_KEY='preflight-compose-validation-only'
try {
  docker compose --env-file .env.example --env-file $experimentEnv `
    -f docker-compose.yml -f docker-compose.demo.yml `
    -f docker-compose.experiment.yml config --quiet
} finally {
  Remove-Item Env:OPENAI_API_KEY -ErrorAction SilentlyContinue
}
```

## Qualification diagnostics

- With Docker Desktop available, the complete suite produced a terminal green
  result: `Hive.Tests` passed 1991/1991, Evaluation Tooling passed 74/74, and
  DemoClient passed 4/4.
- The suite created only its local PostgreSQL Testcontainers. A post-run
  `docker ps` returned no running containers, so no Testcontainer, application,
  or evaluation container remained.
- Compose rendering still exited successfully without contacting the daemon.
  The Docker client reported that its user-level `config.json` was
  inaccessible in this execution environment; no credential or file content
  was read or recorded.

## Verdict

`ready-for-real-smoke`

This verdict permits only a separate request to authorize `US-F0-17-T02`. It
does not authorize or represent a real-provider smoke, corpus execution,
calibration, freeze, holdout, go/no-go decision, or reopening of F1a.
