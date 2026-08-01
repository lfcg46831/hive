# US-F0-20 post-F0.10 local qualification v1

Date: 2026-08-01  
Scope: `US-F0-20-T01`  
Reviewed commit: `b484551bf3f8dbf2f59a13b94638e4a2dea72f41`  
Reviewed tree: `f121af94be0f76cab82712a6658fd13c7c300f7a`

## Isolation

- No AI provider endpoint, API credential, evaluation host, runner, corpus case,
  scoring, calibration, freeze, or holdout was used.
- The functional worktree under `src/`, `tests/`, `config/`, `Dockerfile`, and
  the active Compose adapters was unchanged from the reviewed commit.
- The only tracked worktree changes during qualification were the post-F0.10
  preflight specification in `docs/bible.html` and this evidence.
- `experiment prepare` wrote only ignored files under
  `artifacts/evaluation/experiments/bug-triage-lab-v2/`.
- Compose validation used `.env.example` plus a non-secret interpolation
  placeholder and `config --quiet`; it did not read `.env` or create
  application or evaluation containers.

## Reproducibility identity

| Input | Identity |
| --- | --- |
| Experiment manifest | `config/experiments/bug-triage-lab-v2/experiment.v1.json` |
| Manifest SHA-256 | `a51e80a8492cfa27413c9d6ee5d334f88c695aace7dbe45c1864b2f4a90c19f0` |
| Generated manifest SHA-256 | `a51e80a8492cfa27413c9d6ee5d334f88c695aace7dbe45c1864b2f4a90c19f0` |
| Manifest status | `prepared` |
| Effective configuration SHA-256 | `cabdca7c16a693f707d1406932587947b2ae55897a71d084105e506f4507ea2f` |
| Provider/model declared by manifest | `openai` / `gpt-5-mini-2025-08-07` |

The generated manifest identity matched the exact manifest file hash. The
generated Compose env contained no credential-like key.

## Gates

| Gate | Result |
| --- | --- |
| Clean solution build after `dotnet clean` | PASS — 0 warnings, 0 errors |
| Complete solution suite | PASS — 2138/2138, no skips |
| Main suite, including PostgreSQL Testcontainers | PASS — 2057/2057 |
| Evaluation tooling suite | PASS — 77/77 |
| DemoClient suite | PASS — 4/4 |
| Architecture/boundary guards | PASS — 16/16 |
| Directive execution characterization | PASS — 13/13 |
| Checkpoint/audit/idempotency focused gate | PASS — 29/29 |
| Manifest preparation and hash validation | PASS |
| Generated env credential-key check | PASS — 0 matching keys |
| Generic Compose configuration (`config --quiet`) | PASS |
| Functional worktree diff | PASS — empty |
| Post-suite running container check | PASS — 0 containers |

## Commands

```powershell
dotnet clean Hive.sln -v minimal
dotnet build Hive.sln --no-restore -v minimal
dotnet test Hive.sln --no-build --no-restore -v minimal

dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-build --no-restore -v minimal `
  --filter "FullyQualifiedName~ApplicationBoundaryTests|FullyQualifiedName~AuditExportContractTests"
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-build --no-restore -v minimal `
  --filter "Category=DirectiveExecutionCharacterization"
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-build --no-restore -v minimal `
  --filter "FullyQualifiedName~Checkpoint|FullyQualifiedName~JourneyAuditPositionProjectionPublisherTests|FullyQualifiedName~PositionActorIdempotencyTests"

$manifest='config/experiments/bug-triage-lab-v2/experiment.v1.json'
dotnet run --project src/Hive.Evaluation.Tooling --no-restore -- `
  experiment prepare --manifest $manifest
$experimentEnv='artifacts/evaluation/experiments/bug-triage-lab-v2/compose.env'
$env:OPENAI_API_KEY='preflight-compose-validation-only'
try {
  docker compose --env-file .env.example --env-file $experimentEnv `
    -f docker-compose.yml -f docker-compose.demo.yml `
    -f docker-compose.experiment.yml config --quiet
} finally {
  Remove-Item Env:OPENAI_API_KEY -ErrorAction SilentlyContinue
}
docker ps --format '{{.Names}}'
```

## Qualification diagnostics

- Docker Desktop server `29.5.3` was available and the complete main suite
  executed all 67 PostgreSQL/Testcontainers fixtures omitted during
  `US-F0-19-T05`.
- The complete run finished with 2057 main tests, 77 tooling tests, and 4 demo
  client tests, all passing with no skips.
- Compose rendering emitted warnings because the sandbox could not read the
  user-level Docker `config.json`; rendering still exited successfully and no
  credential or file content was read or recorded.
- A post-run Docker query returned zero running containers.

## Verdict

`ready-for-real-smoke`

This verdict permits only the explicitly authorized `US-F0-20-T02` smokes. It
does not authorize or represent a corpus execution, calibration, freeze,
holdout, go/no-go decision, or reopening of F1a.
