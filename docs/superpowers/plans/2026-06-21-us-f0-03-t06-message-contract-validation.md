# US-F0-03-T06 Message Contract Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Validate materialized organizational messages and processing-state transitions without using exceptions for functional rejection, returning all safely evaluable structured errors in deterministic order.

**Architecture:** `MessageContractValidator` consumes the declarative `MessageContractRules` catalog for local checks, then uses an asynchronous read-only context for directive-reference and lineage checks. `ValidationResult` owns normalization, deduplication, and ordering; lifecycle validation remains a separate method because state and rejection reason belong to the persisted processing record rather than `OrgMessage`. Parsing mechanics remain in T07, while a null/unmaterialized message and unsupported schema version establish the phase gate that the serializer will reuse.

**Tech Stack:** .NET 8, C# 12, immutable collections, xUnit

---

### Task 1: Structured validation result

**Files:**
- Create: `src/Hive.Domain/Messaging/ValidationError.cs`
- Create: `src/Hive.Domain/Messaging/ValidationResult.cs`
- Create: `tests/Hive.Tests/ValidationResultTests.cs`

- [x] **Step 1: Write failing result-contract tests**

Cover an empty valid result, defensive immutable snapshotting, duplicate removal, ordinal `Path`/`Code`/`Reason` ordering, and rejection of null errors. Use errors such as:

```csharp
new ValidationError("required-field", "organizationId", RejectionReason.InvalidContract)
new ValidationError("endpoint-not-allowed", "from", RejectionReason.InvalidRoute)
new ValidationError("unauthorized", "$", RejectionReason.Unauthorized)
```

Assert that the normalized order is `$`, `from`, `organizationId`, and that duplicate value-equal records occur once.

- [x] **Step 2: Run the result tests and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~ValidationResultTests -v minimal`

Expected: compilation fails because `ValidationError` and `ValidationResult` do not exist.

- [x] **Step 3: Implement immutable errors and normalized results**

Implement `ValidationError` as the bible contract:

```csharp
public sealed record ValidationError(
    string Code,
    string Path,
    RejectionReason Reason);
```

Implement `ValidationResult` with a public `Create(IEnumerable<ValidationError>)` factory and a cached `Valid` instance. `Create` must throw only for internal API misuse (`errors` null or containing null), then run:

```csharp
errors
    .Distinct()
    .OrderBy(error => error.Path, StringComparer.Ordinal)
    .ThenBy(error => error.Code, StringComparer.Ordinal)
    .ThenBy(error => error.Reason)
    .ToImmutableArray();
```

Expose the snapshot as `IReadOnlyList<ValidationError> Errors` and `bool IsValid => Errors.Count == 0`.

- [x] **Step 4: Run the result tests and verify GREEN**

Run the Step 2 command.

Expected: all `ValidationResultTests` pass.

### Task 2: Structural contract validation

**Files:**
- Create: `src/Hive.Domain/Messaging/IMessageValidationContext.cs`
- Create: `src/Hive.Domain/Messaging/MessageContractValidator.cs`
- Create: `tests/Hive.Tests/MessageContractValidatorTests.cs`

- [x] **Step 1: Write failing structural-validation tests**

Add focused tests proving:

- a canonical valid `Memo` returns `ValidationResult.Valid`;
- a null/unmaterialized message returns `materialization-failed` at `$` with `InvalidContract`;
- schema versions other than initial version `1` return `unsupported-schema-version` at `schemaVersion`;
- blank required strings aggregate independently (for example `Directive.Objective` and `Directive.Context`);
- an empty required collection (`Escalation.OptionsConsidered`) is rejected;
- `Memo` sent from `OrganizationOwnerEndpointRef` and `Pulse` sent by the wrong system producer return `endpoint-not-allowed` with `InvalidRoute`;
- every canonical type's derived channel matches the channel declared by the consumed catalog rule;
- undefined materialized `Priority`, `ReportKind`, and `SystemEndpointKind` values, plus undefined lifecycle `MessageState` and `RejectionReason` values, are converted to errors by the applicable validator method instead of escaping as enum-contract exceptions;
- a missing or lexically invalid `ApprovalPolicyRef` returns `required-field` at `policy` or `invalid-policy` at `policy.value`.

Use `RuntimeHelpers.GetUninitializedObject` plus a small test-only backing-field setter only for states ordinary constructors intentionally prevent (missing organization/policy, undefined constructor-validated enums, self-reference). This models a future deserializer that materializes an invalid object without weakening production constructors.

- [x] **Step 2: Run the structural tests and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~MessageContractValidatorTests -v minimal`

Expected: compilation fails because `MessageContractValidator` does not exist.

- [x] **Step 3: Implement catalog-driven structural validation**

Create the read-only context seam and a sealed validator whose materialized-message entry point is:

```csharp
public interface IMessageValidationContext
{
    ValueTask<Directive?> FindDirectiveAsync(
        DirectiveId directiveId,
        CancellationToken cancellationToken = default);
}

public ValueTask<ValidationResult> ValidateAsync(
    OrgMessage? message,
    IMessageValidationContext context,
    CancellationToken cancellationToken = default);
```

The method must:

1. Return `materialization-failed` when `message` is null.
2. Return `unsupported-schema-version` when `SchemaVersion != 1`, without contextual validation.
3. Resolve `MessageContractRules.For(message.GetType())`; convert an unknown concrete type to `unknown-message-type` rather than exposing `ArgumentException`.
4. Inspect every `RequiredFields` property. Treat null, blank strings, empty immutable/enumerable collections, default `DateTimeOffset`, invalid enum values, and invalid identity value internals as structural errors.
5. Check `message.Channel == rule.Channel`.
6. Match `From` and `To` against exact endpoint variant and exact `SystemEndpointKind` rules.
7. Validate `ApprovalRequest.Policy.Value` using the existing structural lexical rules: non-null, non-whitespace, and no exterior whitespace.
8. Normalize all structural errors through `ValidationResult.Create`.
9. Return immediately when structural errors exist; do not invoke context.

Keep helpers private and focused: `ValidateRequiredFields`, `ValidateClosedValues`, `ValidateEndpoint`, `ValidatePolicy`, `MatchesEndpointRule`, and `PathFor` (lowercase the first property character for canonical dotted paths).

- [x] **Step 4: Run the structural tests and verify GREEN**

Run the Step 2 command.

Expected: all structural tests pass.

### Task 3: Contextual directive reference and lineage validation

**Files:**
- Modify: `src/Hive.Domain/Messaging/IMessageValidationContext.cs`
- Modify: `src/Hive.Domain/Messaging/MessageContractValidator.cs`
- Modify: `tests/Hive.Tests/MessageContractValidatorTests.cs`

- [x] **Step 1: Write failing contextual tests**

Define a test fake backed by `Dictionary<DirectiveId, Directive>` and a call counter. Add tests for:

- structural failure gates context completely;
- a root `Directive` performs no lookup;
- a valid parent and a valid report target in the same organization/thread pass;
- a missing directive returns `reference-not-found` at `parentDirectiveId` or `aboutDirectiveId`;
- an existing directive in another organization returns `reference-organization-mismatch`;
- an existing directive in another thread returns `reference-thread-mismatch`;
- self-parenting returns `self-reference` without context lookup;
- an ancestor chain that reaches the incoming directive or repeats an existing ancestor returns `reference-cycle`;
- a thrown dependency exception and `OperationCanceledException` propagate and are not converted into `InvalidContract`.

- [x] **Step 2: Run the contextual tests and verify RED**

Run the Step 2 command from Task 2.

Expected: tests fail because contextual reference validation does not exist.

- [x] **Step 3: Implement the read-only context seam and lineage traversal**

Use the existing seam:

```csharp
public interface IMessageValidationContext
{
    ValueTask<Directive?> FindDirectiveAsync(
        DirectiveId directiveId,
        CancellationToken cancellationToken = default);
}
```

In `MessageContractValidator`, iterate `rule.References` after structural success. Read the source property, skip a null optional reference, and load the referenced directive. Enforce the catalog flags for shared organization/thread and self-reference. For `DisallowCycles`, follow `ParentDirectiveId` until root while keeping a `HashSet<DirectiveId>` seeded with the incoming directive id; any repeated id produces one `reference-cycle` error. Preserve cancellation and dependency failures.

- [x] **Step 4: Run the contextual tests and verify GREEN**

Run the Step 2 command from Task 2.

Expected: all `MessageContractValidatorTests` pass.

### Task 4: Lifecycle and rejection-reason validation

**Files:**
- Modify: `src/Hive.Domain/Messaging/MessageContractValidator.cs`
- Create: `tests/Hive.Tests/MessageLifecycleValidationTests.cs`

- [x] **Step 1: Write failing lifecycle tests**

Exercise every pair of defined states. Allowed transitions must return valid except `Received -> Rejected`, which requires a defined reason. Add explicit tests for:

- invalid transition -> `invalid-state-transition` at `state`;
- undefined `from` and `to` values aggregate as `invalid-state` at `state.from` and `state.to` without throwing;
- missing reason for `Rejected` -> `rejection-reason-required` at `rejectionReason`;
- a reason on any non-rejected target -> `rejection-reason-not-allowed`;
- an undefined reason -> `invalid-rejection-reason`.

- [x] **Step 2: Run lifecycle tests and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~MessageLifecycleValidationTests -v minimal`

Expected: compilation fails because `ValidateTransition` does not exist.

- [x] **Step 3: Implement lifecycle validation without enum-contract exceptions**

Add:

```csharp
public ValidationResult ValidateTransition(
    MessageState from,
    MessageState to,
    RejectionReason? rejectionReason);
```

Check `Enum.IsDefined` before calling `MessageStateContract.CanTransition`. Only evaluate the transition matrix when both states are defined. Require a defined reason exactly when `to == MessageState.Rejected`; reject any reason for all other targets. Aggregate and normalize every independently evaluable error.

- [x] **Step 4: Run lifecycle and existing enum tests and verify GREEN**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~MessageLifecycleValidationTests|FullyQualifiedName~MessageStateTests|FullyQualifiedName~RejectionReasonTests" -v minimal`

Expected: all selected tests pass.

### Task 5: Contract coverage, documentation alignment, and full verification

**Files:**
- Modify: `tests/Hive.Tests/MessageContractValidatorTests.cs`
- Modify: `docs/bible.html`

- [x] **Step 1: Add a taxonomy-wide contract test**

Create one valid instance of every canonical message type and verify the validator accepts its structural contract with a context containing any referenced directives. This guards the reflection/catalog mapping for all ten message types.

- [x] **Step 2: Run all domain validation tests**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~ValidationResultTests|FullyQualifiedName~MessageContractValidatorTests|FullyQualifiedName~MessageLifecycleValidationTests|FullyQualifiedName~MessageContractRulesTests" -v minimal`

Expected: all selected tests pass with zero failures.

- [x] **Step 3: Record the implemented contract in the bible history**

Add a concise history row stating that T06 now implements catalog-driven structural/contextual validation, deterministic aggregated errors, directive lineage checks, lifecycle/rejection-reason checks, and exception propagation for technical failures. Do not add implementation narrative elsewhere.

- [x] **Step 4: Run the full solution verification**

Run: `dotnet test Hive.sln --no-restore -v minimal`

Expected: all tests pass with zero failures, skips, warnings, or build errors.

- [x] **Step 5: Review the final diff and prepare the commit message**

Run: `git diff --check` and `git status --short`.

Expected: `git diff --check` exits successfully and the status contains only the T06 plan, domain implementation, tests, and bible update.

Suggested commit message: `feat(domain): validate organizational message contracts`
