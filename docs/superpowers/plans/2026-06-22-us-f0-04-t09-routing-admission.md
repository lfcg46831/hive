# US-F0-04-T09 Routing Admission at the Inbox Entry Point Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Integrate vertical and governance routing validation at the position's inbox entry point so that a message is validated — and either admitted or rejected — before it is accepted into the inbox (US-F0-04-T09).

**Architecture:** Add a thin dispatching seam `RoutingAdmissionValidator` (in `Hive.Domain.Messaging`) that composes the focused validators built in `US-F0-04-T04`–`T07` (directive, report, escalation, approval) without re-implementing their rules. It dispatches an incoming `OrgMessage` by concrete type to the matching validator, returns a `RoutingAdmission` outcome, and on an invalid `ValidationResult` pairs the detail with `RoutingValidationContext.ForMessage` into a `RoutingRejection`. Message types with no vertical/governance routing rule pass the gate unchanged (horizontal/system gating is later work). The `PositionActor` (US-F0-06) consumes this seam at its entry point without changing it; the audit event for rejected routing is `US-F0-04-T10`.

**Tech Stack:** .NET 8, C#, xUnit, `Hive.Domain` messaging/organization/governance contracts.

---

### Task 1: Specify inbox admission behavior

**Files:**
- Create: `tests/Hive.Tests/RoutingAdmissionValidatorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `RoutingAdmissionValidatorTests.cs` covering:
- a valid `Directive`, `Report`, `Escalation`, `ApprovalRequest` and `ApprovalDecision` are admitted (`IsAdmitted`, `Rejection is null`);
- a directive with a level jump is rejected, the audit result keeps the fine-grained `direct-subordinate-required` and the public result is the sanitized `invalid-route`/`$`;
- the rejection context carries the original message identity (id, organization, sender, recipient, thread);
- an `ApprovalDecision` from an unauthorized approver is rejected with the sanitized `unauthorized` public reason;
- a `Memo` (no vertical/governance routing rule) is admitted without querying any validator (validators built over failing dependencies);
- cancellation propagates before dispatch and an unexpected validator failure propagates unchanged;
- null constructor dependencies and a null message are API misuse (`ArgumentNullException`).

Reuse the materialized-relations fixture (`ceo → delivery-lead → engineer`) and the approval authority/request-log stubs already used by `ApprovalRoutingValidatorTests`, plus `FailingRelations`/`FailingAuthority`/`FailingLog` fakes for the no-query and propagation cases.

- [ ] **Step 2: Run the focused tests and verify RED**

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~RoutingAdmissionValidatorTests -v minimal
```

Expected: compilation fails because `RoutingAdmissionValidator`/`RoutingAdmission` do not exist.

### Task 2: Implement the admission seam

**Files:**
- Create: `src/Hive.Domain/Messaging/RoutingAdmission.cs`
- Create: `src/Hive.Domain/Messaging/RoutingAdmissionValidator.cs`

- [ ] **Step 1: Add the outcome type**

`RoutingAdmission` is an immutable record with a shared `Admitted` instance (no rejection), a `Reject(RoutingRejection)` factory (null-guarded), a `Rejection` property and an `IsAdmitted => Rejection is null` predicate.

- [ ] **Step 2: Add the dispatching validator**

`RoutingAdmissionValidator` takes the four focused validators (null-guarded). `AdmitAsync(OrgMessage, CancellationToken)` guards the message, throws on cancellation, then dispatches by concrete type: `Directive`/`Report`/`Escalation` to the vertical validators, `ApprovalRequest`/`ApprovalDecision` to the governance validator, and any other type to `ValidationResult.Valid`. A valid result returns `RoutingAdmission.Admitted`; an invalid result returns `RoutingAdmission.Reject(RoutingRejection.Create(RoutingValidationContext.ForMessage(message), result))`.

- [ ] **Step 3: Run focused tests and verify GREEN**

Run the focused command from Task 1. Expected: all `RoutingAdmissionValidatorTests` cases pass.

- [ ] **Step 4: Run related routing tests**

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~RoutingValidationCatalogTests|FullyQualifiedName~RoutingRejectionTests|FullyQualifiedName~DirectiveRoutingValidatorTests|FullyQualifiedName~ReportRoutingValidatorTests|FullyQualifiedName~EscalationRoutingValidatorTests|FullyQualifiedName~ApprovalRoutingValidatorTests|FullyQualifiedName~RoutingAdmissionValidatorTests" -v minimal
```

Expected: all selected routing tests pass.

### Task 3: Finalize the durable contract

**Files:**
- Modify: `docs/bible.html`

- [ ] **Step 1: Record the contract**

Add the `US-F0-04-T09` admission contract paragraph after the `US-F0-04-T08` paragraph and a `v0.69` history entry, only after tests pass. Point the next-iteration marker at `US-F0-04-T10`.

- [ ] **Step 2: Verify the documentation contract**

```powershell
rg -n "RoutingAdmission|US-F0-04-T09|v0.69|US-F0-04-T10" docs/bible.html
```

### Task 4: Verify the completed task

**Files:**
- Test: `tests/Hive.Tests/RoutingAdmissionValidatorTests.cs`
- Build: `Hive.sln`

- [ ] **Step 1: Run focused tests**

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~RoutingAdmissionValidatorTests -v minimal
```

- [ ] **Step 2: Run the full test project**

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore -v minimal
```

- [ ] **Step 3: Build the solution**

```powershell
dotnet build Hive.sln --no-restore -v minimal
```

- [ ] **Step 4: Inspect the final diff**

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors; only the admission seam, its tests, the Bible contract and this plan are changed.

- [ ] **Step 5: Prepare the commit message**

```text
feat(domain): validate routing at the inbox entry point (US-F0-04-T09)
```
