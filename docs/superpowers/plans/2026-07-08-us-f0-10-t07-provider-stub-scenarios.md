# US-F0-10-T07 Provider Stub Scenarios Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add deterministic AI gateway stub scenarios for the F0 bug-triage vertical slice.

**Architecture:** Extend the infrastructure stub with a named `Scenario` option that overrides the generic `Outcome` path only when configured. Scenario outputs stay provider-neutral JSON consumed by the existing AI directive parser, and the provider failure scenario returns the existing structured gateway failure type.

**Tech Stack:** .NET 8, xUnit, Microsoft.Extensions.Configuration options binding, existing HIVE AI gateway contracts.

---

### Task 1: Add Scenario Coverage

**Files:**
- Modify: `tests/Hive.Tests/AiGatewayStubProviderTests.cs`
- Modify: `src/Hive.Infrastructure/Ai/StubAiGatewayProviderOptions.cs`
- Modify: `src/Hive.Infrastructure/Ai/StubAiGatewayProvider.cs`

- [ ] **Step 1: Write the failing report-scenario test**

Add a test that binds `Hive:AiGateway:Stub:Scenario=bug-triage-report`, calls `IAiGateway.CompleteAsync`, and verifies the returned text parses as a `Report` decision with `ReportKind.Done`.

- [ ] **Step 2: Run the focused test and verify red**

Run:

```powershell
dotnet test tests\Hive.Tests\Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~AiGatewayStubProviderTests" -v minimal
```

Expected: the new scenario test fails because the existing stub ignores `Scenario` and returns non-JSON default text.

- [ ] **Step 3: Implement the minimal report scenario**

Add `Scenario` to `StubAiGatewayProviderOptions`, route configured scenarios before the generic outcome switch, and return parser-valid report JSON for `bug-triage-report`.

- [ ] **Step 4: Add the remaining scenario tests**

Cover `bug-triage-missing-information`, `bug-triage-external-decision-blocked`, and `provider-controlled-failure`. The first two parse as `Escalation`; the failure returns `AiGatewayErrorCode.ProviderUnavailable` with a deterministic retryable structured error.

- [ ] **Step 5: Implement remaining scenarios and docs**

Add deterministic scenario outputs and update `docs/configuration.md` with the `Scenario` setting and stable scenario names.

- [ ] **Step 6: Verify**

Run the focused AI gateway/directive tests, then the full `Hive.Tests` project if focused verification is clean.
