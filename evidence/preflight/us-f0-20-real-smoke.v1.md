# US-F0-20 real-provider smoke v1

Date: 2026-08-01  
Scope: `US-F0-20-T02`  
Reviewed commit: `b484551bf3f8dbf2f59a13b94638e4a2dea72f41`  
Reviewed tree: `f121af94be0f76cab82712a6658fd13c7c300f7a`  
Smoke test source SHA-256:
`47bb992d5359d380a87d989e7a038e05d3cd68c428949ea5fef840936b87db21`

## Authorization and isolation

- After the assistant described the complete post-F0.10 preflight, including
  separate real smokes, the user explicitly instructed it to perform that
  work. This authorized `US-F0-20-T02`; it did not authorize calibration.
- The ignored local `.env` supplied the credential only to the process-scoped
  `HIVE_AI_GATEWAY_REAL_TEST_API_KEY`; it was not printed or recorded.
- `HIVE_AI_GATEWAY_REAL_TEST_MODEL_ID` was read from the v2 manifest and fixed
  to `gpt-5-mini-2025-08-07`.
- No endpoint override was supplied.
- The two tests ran exactly once each. No retry was performed.
- No evaluation host, runner, corpus case, run id, scoring, reporting,
  calibration, freeze, holdout, or container was started.

## Reproducibility identity

| Input | Identity |
| --- | --- |
| Local qualification | `evidence/preflight/us-f0-20-local-qualification.v1.md` |
| Local qualification SHA-256 | `e49a56bb7a80a6d89432aea38c70b517e11da4846eac8abccae87b4282280f02` |
| Experiment manifest | `config/experiments/bug-triage-lab-v2/experiment.v1.json` |
| Manifest SHA-256 | `a51e80a8492cfa27413c9d6ee5d334f88c695aace7dbe45c1864b2f4a90c19f0` |
| Effective configuration SHA-256 | `cabdca7c16a693f707d1406932587947b2ae55897a71d084105e506f4507ea2f` |
| Provider/model | `openai` / `gpt-5-mini-2025-08-07` |
| Smoke test source SHA-256 | `47bb992d5359d380a87d989e7a038e05d3cd68c428949ea5fef840936b87db21` |

The test source hash is contained in the reviewed commit and the successful
local qualification covers that same commit and tree.

## Gates

| Gate | Result |
| --- | --- |
| General real-provider smoke | PASS — 1/1, no skips, 4 s |
| Semantic `Report.Done` verifier smoke | PASS — 1/1, no skips, 5 s |
| Provider/model identity enforced by tests | PASS |
| Functional worktree diff after smokes | PASS — empty |
| Ephemeral-variable post-process check | PASS — credential, model, and endpoint absent |

Both test processes returned passing VSTest results. The outer PowerShell
wrapper reported exit code 1 after the passes because its final best-effort
cleanup attempted to remove the already-absent endpoint variable. This occurred
after both test commands and did not change either VSTest result. The variables
were process-scoped, the process ended, and a separate post-process check found
all three variables absent. The smokes were not repeated.

## Command

```powershell
$keyLine = Get-Content -LiteralPath '.env' |
  Where-Object { $_ -match '^\s*OPENAI_API_KEY\s*=' } |
  Select-Object -First 1
$manifestData = Get-Content -Raw `
  'config/experiments/bug-triage-lab-v2/experiment.v1.json' |
  ConvertFrom-Json
$env:HIVE_AI_GATEWAY_REAL_TEST_API_KEY =
  $keyLine.Substring($keyLine.IndexOf('=') + 1).Trim().Trim('"')
$env:HIVE_AI_GATEWAY_REAL_TEST_MODEL_ID = $manifestData.model.model_id
Remove-Item Env:HIVE_AI_GATEWAY_REAL_TEST_ENDPOINT -ErrorAction SilentlyContinue
try {
  dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-build --no-restore `
    --filter "FullyQualifiedName~AiGatewayIntegrationTests.Optional_real_provider_smoke_test_runs_only_with_local_secret_and_model" `
    -v minimal
  dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-build --no-restore `
    --filter "FullyQualifiedName~AiGatewayIntegrationTests.Optional_real_outcome_verifier_smoke_confirms_semantic_done" `
    -v minimal
} finally {
  Remove-Item Env:HIVE_AI_GATEWAY_REAL_TEST_API_KEY -ErrorAction SilentlyContinue
  Remove-Item Env:HIVE_AI_GATEWAY_REAL_TEST_MODEL_ID -ErrorAction SilentlyContinue
  Remove-Item Env:HIVE_AI_GATEWAY_REAL_TEST_ENDPOINT -ErrorAction SilentlyContinue
}
```

## Verdict

`passed`

Both real smokes passed on the same commit, manifest, and effective
configuration as `US-F0-20-T01`. This result is only an input to the separate
`US-F0-20-T03` decision; it does not authorize corpus execution, calibration,
freeze, holdout, or reopening F1a.
