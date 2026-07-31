# US-F0-17 real-provider smoke v1

Date: 2026-07-31<br>
Scope: `US-F0-17-T02`<br>
Integrated code commit: `ba634f7037c2f9fe767175452bcddacf4d4246dc`<br>
Corrective test source SHA-256:
`47bb992d5359d380a87d989e7a038e05d3cd68c428949ea5fef840936b87db21`

## Authorization and isolation

- The user explicitly authorized work on `US-F0-17-T02`.
- After `BUG-001` was corrected, the user explicitly authorized one corrective
  rerun of both real smokes in a network-enabled execution environment.
- The ignored local `.env` supplied the credential only to ephemeral
  `HIVE_AI_GATEWAY_REAL_TEST_API_KEY`; it was not recorded.
- `HIVE_AI_GATEWAY_REAL_TEST_MODEL_ID` was fixed to the manifest value
  `gpt-5-mini-2025-08-07`.
- No endpoint override was supplied.
- The credential, model, and endpoint variables were removed in `finally`;
  the post-run removal check passed.
- No evaluation host, runner, corpus case, run id, scoring, reporting,
  calibration, freeze, holdout, or container was started.

The initial attempt preceded `BUG-001`. The corrective rerun used the exact
functional source later integrated in commit `ba634f7`; the recorded test
source hash matches that commit. T01 was subsequently repeated and passed on
the same integrated commit.

## Reproducibility identity

| Input | Identity |
| --- | --- |
| Local qualification | `evidence/preflight/us-f0-17-local-qualification.v1.md` |
| Experiment manifest | `config/experiments/bug-triage-lab-v1/experiment.v1.json` |
| Manifest SHA-256 | `e3ee9c5911129395b34fd68611088206eaa33bfa56c94e9a6264793c6357697d` |
| Effective configuration SHA-256 | `e0e0389bbb391d13fb8f8f5bfb47f80baf417b1bb02e3310bb857eeccd5dfe0d` |
| Provider/model | `openai` / `gpt-5-mini-2025-08-07` |
| Corrective test source SHA-256 | `47bb992d5359d380a87d989e7a038e05d3cd68c428949ea5fef840936b87db21` |

## Initial smoke gates

| Gate | Result |
| --- | --- |
| General real-provider smoke | BLOCKED — test process passed 1/1, but the test accepts both provider success and provider failure; no successful provider response was established |
| Semantic `Report.Done` verifier smoke | FAIL — 0/1, no skips |
| Ephemeral-variable cleanup | PASS |

Sanitized verifier diagnostic:
`status=Unavailable; gatewaySuccess=false; responseText=unavailable`.
No provider call was visible in the OpenAI project logs. Both commands ran in a
network-restricted execution environment, while the gateway maps transport
exceptions to sanitized failures and the tests do not expose the underlying
error code. The evidence therefore cannot distinguish a pre-provider transport
failure from another sanitized gateway failure, and must not represent the
general test-process pass as successful provider access.
No credential, prompt, provider output, reasoning, corpus, or organizational
content was captured.

## Corrective validation and rerun

| Gate | Result |
| --- | --- |
| Solution build | PASS — 0 warnings, 0 errors |
| Focused `AiGatewayIntegrationTests` | PASS — 9/9, no skips |
| General real-provider smoke | PASS — 1/1, no skips, 5 s |
| Semantic `Report.Done` verifier smoke | PASS — 1/1, no skips, 9 s |
| Ephemeral-variable cleanup | PASS |

The corrected general gate requires gateway success, matching
`openai`/`gpt-5-mini-2025-08-07`, and absence of the credential in returned
text. Both gates fail closed with diagnostics limited to the HIVE error code,
validated HTTP status when available, provider, and model. The corrective
network-enabled rerun produced no failure diagnostic and captured no
credential, prompt, provider output, reasoning, corpus, or organizational
content.

## Commands

```powershell
dotnet build Hive.sln --no-restore -v minimal
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-build --no-restore `
  --filter "FullyQualifiedName~AiGatewayIntegrationTests" -v minimal

$keyLine = Get-Content -LiteralPath '.env' |
  Where-Object { $_ -match '^\s*OPENAI_API_KEY\s*=' } |
  Select-Object -First 1
$env:HIVE_AI_GATEWAY_REAL_TEST_API_KEY =
  $keyLine.Substring($keyLine.IndexOf('=') + 1).Trim().Trim('"')
$env:HIVE_AI_GATEWAY_REAL_TEST_MODEL_ID='gpt-5-mini-2025-08-07'
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

T02 corrective real-smoke result: `passed`

`BUG-001` is completed, both real smokes passed, and the corrected test source
is now covered by the successful local qualification on `ba634f7`. This result
is an input to the separate T03 decision; by itself it does not authorize
corpus execution, calibration, freeze, holdout, or reopening F1a.
