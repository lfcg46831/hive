# US-F0-05-T10 PostgreSQL Registry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist the idempotent organization registry/read model in PostgreSQL with transactional convergence, monotonic configuration version, canonical fingerprint, import timestamp, and reloadable projections.

**Architecture:** Extract the pure T09 snapshot mutation from the in-memory store and invoke it through an asynchronous internal store seam. Add an `Npgsql` implementation that owns the `registry` schema, serializes imports per organization with a durable lock row, reloads snapshots from relational tables, and applies only planned upserts/deletes in one transaction. Keep queryable scalar fields relational and store identity-free leaf collections as JSONB.

**Tech Stack:** .NET 8, C# 12, Npgsql 8.0.3, PostgreSQL 16, Testcontainers.PostgreSql 3.10.0, xUnit 2.5.3

---

## File structure

- Create `src/Hive.Infrastructure/Organization/Registry/IOrganizationRegistryReader.cs`: public asynchronous snapshot read seam for T10/T11.
- Create `src/Hive.Infrastructure/Organization/Registry/IOrganizationRegistryStore.cs`: internal import-application seam shared by in-memory and PostgreSQL stores.
- Create `src/Hive.Infrastructure/Organization/Registry/OrganizationRegistryMutation.cs`: pure T09 plan/materialization logic extracted from the importer.
- Modify `src/Hive.Infrastructure/Organization/Registry/OrganizationRegistrySnapshot.cs`: name the snapshot-level timestamp `ImportedAt`; retain `UpdatedAt` only on individual entries.
- Modify `src/Hive.Infrastructure/Organization/Registry/OrganizationConfigurationImporter.cs`: validate/project, then asynchronously delegate atomic application to a store.
- Modify `src/Hive.Infrastructure/Organization/Registry/InMemoryOrganizationRegistry.cs`: implement the asynchronous store/reader seams without changing observable T09 semantics.
- Create `src/Hive.Infrastructure/Organization/Registry/PostgreSql/PostgreSqlOrganizationRegistry.cs`: transaction orchestration, public reader, and `IOrganizationRelations` implementation.
- Create `src/Hive.Infrastructure/Organization/Registry/PostgreSql/PostgreSqlOrganizationRegistryReader.cs`: reconstruct immutable snapshots from relational rows.
- Create `src/Hive.Infrastructure/Organization/Registry/PostgreSql/PostgreSqlOrganizationRegistryWriter.cs`: apply planned row-level upserts/deletes.
- Create `src/Hive.Infrastructure/Organization/Registry/PostgreSql/RegistryJson.cs`: stable System.Text.Json handling for JSONB leaf values.
- Create `src/Hive.Infrastructure/Organization/Registry/PostgreSql/PostgreSqlOrganizationRegistryMigrator.cs`: execute the embedded registry migration.
- Create `src/Hive.Infrastructure/Organization/Registry/PostgreSql/Migrations/001_registry.sql`: own and create the `registry` schema/tables.
- Modify `src/Hive.Infrastructure/Hive.Infrastructure.csproj`: reference Npgsql and embed the migration.
- Modify `tests/Hive.Tests/OrganizationConfigurationImporterTests.cs`: exercise the importer asynchronously against the in-memory implementation.
- Create `tests/Hive.Tests/PostgreSql/PostgreSqlFixture.cs`: disposable PostgreSQL 16 fixture and schema reset helper.
- Create `tests/Hive.Tests/PostgreSql/PostgreSqlOrganizationRegistryTests.cs`: real-database persistence, idempotency, convergence, concurrency, relation-query, and rollback tests.
- Modify `tests/Hive.Tests/Hive.Tests.csproj`: reference Testcontainers.PostgreSql.
- Modify `docs/configuration.md`: document registry schema ownership and migration execution.
- Modify `docs/bible.html`: record the completed T10 contract without duplicating operational instructions.

### Task 1: Make registry import asynchronous and store-agnostic

**Files:**
- Create: `src/Hive.Infrastructure/Organization/Registry/IOrganizationRegistryReader.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/IOrganizationRegistryStore.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/OrganizationRegistryMutation.cs`
- Modify: `src/Hive.Infrastructure/Organization/Registry/OrganizationRegistrySnapshot.cs`
- Modify: `src/Hive.Infrastructure/Organization/Registry/OrganizationConfigurationImporter.cs`
- Modify: `src/Hive.Infrastructure/Organization/Registry/InMemoryOrganizationRegistry.cs`
- Modify: `tests/Hive.Tests/OrganizationConfigurationImporterTests.cs`

- [ ] **Step 1: Convert the existing importer tests to the required asynchronous API**

Change every import call to the following shape while retaining all current assertions:

```csharp
var result = await importer.ImportAsync(configuration);
```

Mark affected facts `async Task`. Add a reader assertion through the intended public seam:

```csharp
IOrganizationRegistryReader reader = registry;
var published = await reader.FindSnapshotAsync(configuration.Organization.Id);
Assert.Same(result.Snapshot, published);
```

Change snapshot-level assertions from `snapshot.UpdatedAt` to `snapshot.ImportedAt`; entry-level assertions remain on `RegistryEntry<T>.UpdatedAt`.

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~OrganizationConfigurationImporterTests`

Expected: compilation fails because `ImportAsync` and `IOrganizationRegistryReader` do not exist.

- [ ] **Step 3: Add the reader/store seams and pure mutation result**

Create the public reader:

```csharp
public interface IOrganizationRegistryReader
{
    ValueTask<OrganizationRegistrySnapshot?> FindSnapshotAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default);
}
```

Create an internal store seam that accepts the already validated projection:

```csharp
internal interface IOrganizationRegistryStore
{
    ValueTask<OrganizationImportResult> ApplyAsync(
        OrganizationRegistryProjection target,
        DateTimeOffset importedAt,
        CancellationToken cancellationToken);
}
```

Move `Changes`, `AddSingleChange`, `AddDictionaryChanges`, and both `Materialize` overloads out of the importer into:

```csharp
internal static class OrganizationRegistryMutation
{
    public static OrganizationImportResult Apply(
        OrganizationRegistrySnapshot? current,
        OrganizationRegistryProjection target,
        DateTimeOffset importedAt);
}
```

The method must return the current instance with `NoChanges` when fingerprints match; otherwise it must create version `(current?.Version ?? 0) + 1`, set snapshot `ImportedAt` to the supplied instant, preserve unchanged entry instances/`UpdatedAt` values, and return the same deterministic plan as T09.

- [ ] **Step 4: Implement asynchronous importer and in-memory store**

Give `OrganizationConfigurationImporter` its existing public in-memory constructor backed by a private `IOrganizationRegistryStore` constructor. Task 3 adds the PostgreSQL overload once that concrete type exists. Expose only:

```csharp
public async ValueTask<OrganizationImportResult> ImportAsync(
    OrganizationConfiguration configuration,
    CancellationToken cancellationToken = default)
```

Validation and projection remain before store access. Pass `_timeProvider.GetUtcNow()` to `ApplyAsync`. `InMemoryOrganizationRegistry.ApplyAsync` checks cancellation before locking, calls the pure mutation under `_gate`, and publishes `result.Snapshot` only for `Applied`. `FindSnapshotAsync` checks cancellation and returns the current snapshot or `null`.

- [ ] **Step 5: Run focused and full unit tests and verify GREEN**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~OrganizationConfigurationImporterTests`

Expected: all importer tests pass.

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore`

Expected: all existing tests pass.

- [ ] **Step 6: Commit**

```text
refactor(registry): add asynchronous registry store seam
```

### Task 2: Add the owned PostgreSQL schema and migration runner

**Files:**
- Modify: `src/Hive.Infrastructure/Hive.Infrastructure.csproj`
- Modify: `tests/Hive.Tests/Hive.Tests.csproj`
- Create: `src/Hive.Infrastructure/Organization/Registry/PostgreSql/Migrations/001_registry.sql`
- Create: `src/Hive.Infrastructure/Organization/Registry/PostgreSql/PostgreSqlOrganizationRegistryMigrator.cs`
- Create: `tests/Hive.Tests/PostgreSql/PostgreSqlFixture.cs`
- Create: `tests/Hive.Tests/PostgreSql/PostgreSqlOrganizationRegistryMigrationTests.cs`

- [ ] **Step 1: Add package references required to compile database tests**

Add `Npgsql` 8.0.3 to `Hive.Infrastructure.csproj`, embed `PostgreSql/Migrations/*.sql`, and add `Testcontainers.PostgreSql` 3.10.0 to `Hive.Tests.csproj`.

- [ ] **Step 2: Write the failing migration integration test**

Use one collection fixture backed by `postgres:16-alpine`. The test calls `MigrateAsync` twice and asserts these relations exist exactly once:

```csharp
Assert.Equal(
    ["authorities", "command_relations", "organization_import_locks", "organizations",
     "occupants", "positions", "schedules", "schema_migrations", "units"],
    tableNames);
Assert.Equal([1], appliedVersions);
```

- [ ] **Step 3: Run the migration test and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter FullyQualifiedName~PostgreSqlOrganizationRegistryMigrationTests`

Expected: compilation fails because the migrator does not exist.

- [ ] **Step 4: Create migration 001**

The runner creates `registry.schema_migrations`; migration 001 must create `registry.organization_import_locks` and these owned tables with foreign keys using `ON DELETE CASCADE`:

```sql
CREATE SCHEMA IF NOT EXISTS registry;

CREATE TABLE IF NOT EXISTS registry.organizations (
    organization_id text PRIMARY KEY,
    configuration_version bigint NOT NULL CHECK (configuration_version > 0),
    configuration_fingerprint text NOT NULL,
    imported_at timestamptz NOT NULL,
    name text NULL,
    root_unit_id text NOT NULL,
    owner_type text NOT NULL,
    owner_ref text NOT NULL,
    prompts jsonb NOT NULL,
    entry_fingerprint text NOT NULL,
    updated_at timestamptz NOT NULL
);
```

`units`, `positions`, `occupants`, `authorities`, `schedules`, and `command_relations` use `(organization_id, entity key...)` primary keys. Every projection table has `entry_fingerprint` and `updated_at`; schedules use `(organization_id, position_id, schedule_id)`. `organization_import_locks` is independent of the organization foreign key so it can serialize the first insert. The runner, not the SQL resource, records version `1` after the DDL succeeds.

- [ ] **Step 5: Implement the migration runner**

`PostgreSqlOrganizationRegistryMigrator` takes `NpgsqlDataSource`, bootstraps `registry.schema_migrations`, discovers embedded resources matching the exact `NNN_name.sql` convention, and applies unapplied versions in numeric order inside one transaction each. After a migration succeeds it inserts the version/name row; missing, duplicate, or malformed versions throw `InvalidOperationException`; cancellation is propagated. Migration `001_registry.sql` creates only the operational lock/projection tables because the runner owns the migration ledger.

- [ ] **Step 6: Run the migration test and verify GREEN**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter FullyQualifiedName~PostgreSqlOrganizationRegistryMigrationTests`

Expected: one passing migration test; the PostgreSQL container is removed by fixture disposal.

- [ ] **Step 7: Commit**

```text
feat(registry): add PostgreSQL registry schema migration
```

### Task 3: Persist and reload complete snapshots transactionally

**Files:**
- Create: `src/Hive.Infrastructure/Organization/Registry/PostgreSql/RegistryJson.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/PostgreSql/PostgreSqlOrganizationRegistry.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/PostgreSql/PostgreSqlOrganizationRegistryReader.cs`
- Create: `src/Hive.Infrastructure/Organization/Registry/PostgreSql/PostgreSqlOrganizationRegistryWriter.cs`
- Create: `tests/Hive.Tests/PostgreSql/PostgreSqlOrganizationRegistryTests.cs`

- [ ] **Step 1: Write the failing first-import/reload test**

Reset and migrate the fixture schema, import the example at a fixed instant, dispose the first registry/data source, open a new data source, and assert:

```csharp
Assert.Equal(OrganizationImportStatus.Applied, imported.Status);
Assert.Equal(1, reloaded!.Version);
Assert.Equal(imported.Snapshot!.Fingerprint, reloaded.Fingerprint);
Assert.Equal(FirstImportAt, reloaded.ImportedAt);
Assert.Equal(2, reloaded.Units.Count);
Assert.Equal(2, reloaded.Positions.Count);
Assert.Equal(2, reloaded.Occupants.Count);
Assert.Equal(2, reloaded.Authorities.Count);
Assert.Single(reloaded.Schedules);
```

Also query `registry.organizations` directly and assert configuration version, fingerprint, and `imported_at`.

- [ ] **Step 2: Run the focused test and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter FullyQualifiedName~PostgreSqlOrganizationRegistryTests.First_import_survives_a_new_connection`

Expected: compilation fails because `PostgreSqlOrganizationRegistry` does not exist.

- [ ] **Step 3: Implement JSONB serialization and snapshot reads**

`RegistryJson` uses one private `JsonSerializerOptions` with `JsonStringEnumConverter`, writes JSON strings with Npgsql type `Jsonb`, and rejects JSON `null` for required collections. The reader loads the organization header first and returns `null` if absent; it then loads every child table ordered by stable keys, reconstructs identity value objects with their `From` factories, and rebuilds `OrganizationRelationsSnapshot` from persisted positions plus the stored root leadership. Construct all exposed dictionaries as read-only dictionaries.

- [ ] **Step 4: Implement transaction orchestration and planned writes**

`PostgreSqlOrganizationRegistry` takes a caller-owned `NpgsqlDataSource` and implements `IOrganizationRegistryStore`, `IOrganizationRegistryReader`, and `IOrganizationRelations`; the DI scope or test fixture disposes the data source. Add the matching public importer constructor at this point. For each import it must:

```sql
INSERT INTO registry.organization_import_locks (organization_id)
VALUES (@organization_id)
ON CONFLICT DO NOTHING;

SELECT organization_id
FROM registry.organization_import_locks
WHERE organization_id = @organization_id
FOR UPDATE;
```

Then load the current snapshot in the same transaction, call `OrganizationRegistryMutation.Apply`, return without functional writes for `NoChanges`, or apply each deterministic plan change and update the organization header metadata before commit. Every command receives the active transaction and cancellation token. Any exception/cancellation leaves disposal to roll back.

The writer maps `Added`/`Updated` to parameterized `INSERT ... ON CONFLICT ... DO UPDATE` and `Removed` to key-specific `DELETE`. It never interpolates identifiers or values. Unchanged projection rows receive no command, so their fingerprints and timestamps remain untouched.

- [ ] **Step 5: Run the first-import/reload test and verify GREEN**

Run the focused test from Step 2.

Expected: one passing test with version `1` and matching persisted metadata.

- [ ] **Step 6: Add idempotency, convergence, concurrency, relations, and rollback tests one at a time**

Add and run each test RED before implementing/fixing its behavior:

1. `Same_fingerprint_is_a_write_free_no_op`: second import returns `NoChanges`; version/imported_at/all functional timestamps remain unchanged.
2. `Changed_configuration_updates_and_removes_only_planned_rows`: version becomes `2`, renamed position timestamp changes, unchanged CEO timestamp remains, removed schedule is absent after reload.
3. `Concurrent_first_import_is_serialized`: two equal imports produce one `Applied`, one `NoChanges`, and persisted version `1`.
4. `Reloaded_registry_serves_organization_relations`: all five `IOrganizationRelations` queries match the in-memory behavior.
5. `Failed_write_rolls_back_the_complete_import`: install a test-only PostgreSQL trigger that raises while deleting the schedule, attempt the changed import, then assert version `1`, old position name/timestamps, and the schedule still exist.

- [ ] **Step 7: Run all PostgreSQL registry tests and verify GREEN**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter FullyQualifiedName~Hive.Tests.PostgreSql`

Expected: all migration and persistence integration tests pass against PostgreSQL 16.

- [ ] **Step 8: Commit**

```text
feat(registry): persist organization read model in PostgreSQL
```

### Task 4: Document, validate, and close US-F0-05-T10

**Files:**
- Modify: `docs/configuration.md`
- Modify: `docs/bible.html`

- [ ] **Step 1: Add operational registry migration documentation**

Under the PostgreSQL section in `docs/configuration.md`, state that the registry owns schema `registry`, uses the existing `ConnectionStrings:PostgreSql`, and automatically applies embedded migration `001_registry.sql` through the common host bootstrap before workloads start, requiring no additional setting or credential.

- [ ] **Step 2: Record implementation completion in the bible**

Add one history row after `0.77` summarizing the implemented asynchronous seam, Npgsql relational store, owned migration, per-organization locking, rollback, and real-PostgreSQL tests. Do not add step-by-step implementation narrative elsewhere.

- [ ] **Step 3: Verify bible integrity**

Run:

```powershell
git diff --check -- docs/bible.html
git diff -- docs/bible.html
Get-Content docs/bible.html -Tail 1
(Get-Content docs/bible.html).Count
```

Expected: only intended additions, final line `</html>`, and line count not unexpectedly reduced from the pre-task count of 2,855.

- [ ] **Step 4: Run fresh full verification**

Run:

```powershell
dotnet test Hive.sln --no-restore
dotnet build Hive.sln --no-restore
git diff --check
git status --short
```

Expected: all tests pass (including PostgreSQL integration tests), build exits `0`, no whitespace errors, and the unrelated untracked `docs/analise-arquitetura-2026-06.md` remains untouched.

- [ ] **Step 5: Review requirements against evidence**

Confirm from tests/database assertions: configuration version starts at `1` and increments only on fingerprint change; fingerprint and import timestamp survive restart/new connection; every registry projection is persisted; same-fingerprint import is write-free; changed import converges rows; first-import concurrency is serialized; technical failure rolls back everything; relations remain queryable.

- [ ] **Step 6: Commit**

```text
docs(registry): document PostgreSQL persistence operations
```

Final commit-summary suggestion required by `AGENTS.md`:

```text
feat(registry): persist the idempotent organization read model in PostgreSQL
```
