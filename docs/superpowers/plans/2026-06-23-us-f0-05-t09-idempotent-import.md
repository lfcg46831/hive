# US-F0-05-T09 Idempotent Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Materialize validated organization configuration into an atomic in-memory registry with a canonical fingerprint, deterministic convergence plan, command-relations read model, and idempotent timestamps.

**Architecture:** Keep the persistence-neutral registry contract and immutable projections in `Hive.Infrastructure.Organization.Registry`, while reusing domain configuration types and `OrganizationRelationsSnapshot`. `OrganizationConfigurationImporter` validates and projects the target before entering the registry lock; `InMemoryOrganizationRegistry` publishes a complete candidate snapshot atomically and implements `IOrganizationRelations`. PostgreSQL persistence remains outside this task.

**Tech Stack:** .NET 8, C# records/read-only dictionaries, `System.Text.Json`, SHA-256, xUnit.

---

### Task 1: Specify initial materialization and deterministic planning

**Files:**
- Create: `tests/Hive.Tests/OrganizationConfigurationImporterTests.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/RegistryEntityKind.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/RegistryChangeKind.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/OrganizationRegistryChange.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/OrganizationImportPlan.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/OrganizationImportStatus.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/OrganizationImportResult.cs`

- [x] **Step 1: Write a failing test that imports the tracked example**

The test parses `config/organizations/acme-delivery/organization.yaml`, imports it at a fixed UTC instant, and asserts `Applied`, version `1`, a `sha256:` fingerprint, two units/positions/occupants/authorities, one schedule, and an ordered all-`Added` change plan.

- [x] **Step 2: Run the focused test and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~OrganizationConfigurationImporterTests.Import_materializes -v minimal`

Expected: FAIL to compile because the registry/import types do not exist.

- [x] **Step 3: Add the public result and plan contracts**

Define `OrganizationImportStatus` (`Applied`, `NoChanges`, `Invalid`), `RegistryEntityKind` (`Organization`, `Unit`, `Position`, `Occupant`, `Authority`, `Schedule`, `CommandRelations`), `RegistryChangeKind` (`Added`, `Updated`, `Removed`), `OrganizationRegistryChange`, `OrganizationImportPlan`, and `OrganizationImportResult`. Invalid results carry validation errors and no plan/snapshot.

### Task 2: Add immutable registry projections and first import

**Files:**
- Create: `src/Hive.Infrastructure/Organization/Registry/RegistryEntry.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/RegistryOrganization.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/RegistryUnit.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/RegistryPosition.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/RegistryOccupant.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/RegistryAuthority.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/RegistryScheduleKey.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/RegistrySchedule.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/OrganizationRegistrySnapshot.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/OrganizationRegistryProjection.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/InMemoryOrganizationRegistry.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/OrganizationConfigurationImporter.cs`

- [x] **Step 1: Implement minimal immutable projections**

Represent organization/prompts, units, structural position fields, occupant runtime fields, authority action sets, and schedules separately. Wrap each projection in `RegistryEntry<T>` containing its semantic fingerprint and `UpdatedAt`. Expose read-only dictionaries from `OrganizationRegistrySnapshot`.

- [x] **Step 2: Implement canonical projection and hashing**

Sort entity collections and set-like fields by ordinal stable keys, preserve ordered AI fallback chains, serialize each projection with `System.Text.Json`, and calculate lowercase `sha256:<hex>`. Calculate the configuration fingerprint from ordered `(entity-kind, key, entity-fingerprint)` tuples.

- [x] **Step 3: Materialize command relations**

Build `OrganizationRelationsSnapshot` from the projected positions, verify that its root matches the configured root-unit leadership, and return a structured validation error instead of publishing when the relation graph cannot be materialized.

- [x] **Step 4: Apply the initial snapshot atomically**

Under the registry lock, build an all-`Added` deterministic plan, materialize all entries at the supplied `TimeProvider` instant, publish one complete snapshot, and return it in an `Applied` result.

- [x] **Step 5: Run the focused test and verify GREEN**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~OrganizationConfigurationImporterTests.Import_materializes -v minimal`

Expected: PASS.

### Task 3: Specify and implement no-op idempotency

**Files:**
- Modify: `tests/Hive.Tests/OrganizationConfigurationImporterTests.cs`
- Modify: `src/Hive.Infrastructure/Organization/Registry/OrganizationConfigurationImporter.cs`

- [x] **Step 1: Write a failing test for equivalent reordered input**

Import the example, advance the clock, then reimport a configuration with reversed prompt/unit/position declaration order and reversed authority sets. Assert `NoChanges`, the same fingerprint/version/snapshot instance, an empty plan, and unchanged timestamps.

- [x] **Step 2: Run the test and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~OrganizationConfigurationImporterTests.Reordered -v minimal`

Expected: FAIL because reimport currently republishes the snapshot.

- [x] **Step 3: Return the existing snapshot for an equal fingerprint**

Short-circuit inside the atomic registry operation when the current fingerprint matches the target fingerprint. Return `NoChanges` and an empty plan at the current version without invoking the clock or changing stored state.

- [x] **Step 4: Run the focused test and verify GREEN**

Run the same command; expected: PASS.

### Task 4: Specify and implement deterministic update/removal convergence

**Files:**
- Modify: `tests/Hive.Tests/OrganizationConfigurationImporterTests.cs`
- Modify: `src/Hive.Infrastructure/Organization/Registry/OrganizationConfigurationImporter.cs`

- [x] **Step 1: Write a failing test for a changed position and removed schedule**

Import once, then import version two with a renamed Delivery Lead and no schedule. Assert version `2`, an ordered `Position/Updated` plus `Schedule/Removed` plan, the unchanged CEO entry timestamp, a refreshed Delivery Lead timestamp, and no stale schedule.

- [x] **Step 2: Run the test and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~OrganizationConfigurationImporterTests.Changed_configuration -v minimal`

Expected: FAIL until per-entry fingerprint reuse and removal planning are implemented.

- [x] **Step 3: Implement convergence**

Diff every entity dictionary by stable key, emit `Added`/`Updated`/`Removed`, preserve existing `RegistryEntry<T>` instances when their fingerprints match, timestamp only new/updated entries, remove missing keys, and order changes by entity kind then ordinal key.

- [x] **Step 4: Run the focused test and verify GREEN**

Run the same command; expected: PASS.

### Task 5: Specify validation gating, transactionality, and live relation queries

**Files:**
- Modify: `tests/Hive.Tests/OrganizationConfigurationImporterTests.cs`
- Modify: `src/Hive.Infrastructure/Organization/Registry/InMemoryOrganizationRegistry.cs`
- Modify: `src/Hive.Infrastructure/Organization/Registry/OrganizationConfigurationImporter.cs`

- [x] **Step 1: Write failing tests**

Add tests proving that duplicate/invalid configuration and an invalid command tree return `Invalid` without mutating an existing snapshot. Add a test using the registry through `IOrganizationRelations` to resolve the CEO/Delivery Lead hierarchy after import.

- [x] **Step 2: Run the importer test class and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~OrganizationConfigurationImporterTests -v minimal`

Expected: FAIL for validation gating and/or relation queries.

- [x] **Step 3: Aggregate validators before writing**

Combine the three existing validator results through `OrganizationConfigurationValidationResult.Create`. Project the relation graph before entering the mutation callback. Return `Invalid` with deterministic errors for either phase.

- [x] **Step 4: Implement `IOrganizationRelations` over published snapshots**

Resolve each query against one captured immutable snapshot, preserve the existing unknown organization/position semantics, and honor cancellation before lookup.

- [x] **Step 5: Run the importer test class and verify GREEN**

Run the same command; expected: all importer tests PASS.

### Task 6: Verify the complete change

**Files:**
- Modify: `docs/bible.html`

- [x] **Step 1: Run the complete test suite**

Run: `dotnet test Hive.sln --no-restore -v minimal`

Expected: all tests PASS with zero failures.

- [x] **Step 2: Check formatting and bible integrity**

Run: `dotnet format Hive.sln --no-restore --verify-no-changes`, `git diff --check`, confirm `docs/bible.html` ends in `</html>`, confirm its line count has not dropped unexpectedly, and inspect `git diff -- docs/bible.html` for only the approved contract changes.

- [x] **Step 3: Review the final diff without committing**

Run: `git status --short` and `git diff --stat`. Do not stage or commit. Prepare the required short English commit message for the user.
