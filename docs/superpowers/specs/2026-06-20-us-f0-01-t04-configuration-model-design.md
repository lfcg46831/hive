# US-F0-01-T04 Configuration Model Design

**Date:** 2026-06-20  
**Status:** Approved

## Context

US-F0-01-T04 defines the initial configuration model for the HIVE executables. The project bible, version 0.33, establishes the canonical node roles, the role defaults for each executable, the local all-in-one override, and the shared PostgreSQL connection string contract.

This task defines configuration data and its documented sources only. Binding, startup validation, dependency injection, PostgreSQL consumers, Akka role application, and workload activation belong to later tasks.

## Goals

- Provide one strongly typed configuration model in `Hive.Infrastructure` for HIVE node settings.
- Define constants for configuration section names, the PostgreSQL connection string, and the four canonical node roles.
- Add safe base configuration for `Hive.Api` and `Hive.Worker`.
- Add the explicit `Hive.Api` development override for the all-in-one local profile.
- Document the equivalent environment-variable contract.
- Protect the contract with automated tests.
- Update `docs/bible.html` at completion with the concrete implementation decisions.

## Non-goals

- Binding configuration into the options pipeline.
- Validating configuration during startup.
- Registering configuration or services through dependency injection.
- Starting Akka.Cluster or applying node roles to Akka.
- Configuring PostgreSQL clients, Akka.Persistence, schemas, tables, or migrations.
- Adding real credentials or secrets to tracked configuration.
- Creating Docker Compose or Kubernetes configuration.

## Selected Approach

The configuration model lives in `Hive.Infrastructure`, which already owns infrastructure and deployment-facing concerns. `Hive.Domain` remains independent from configuration providers and hosting details, while both executable projects can consume the same model when the common bootstrap is implemented in US-F0-01-T05.

The alternatives were rejected for the following reasons:

- JSON-only configuration would not provide the requested reusable model or compile-time names.
- Separate models in `Hive.Api` and `Hive.Worker` would duplicate the contract and allow the executables to drift.
- A new configuration project would add a project boundary that the current scope does not justify.

## Configuration Types

Create focused types under `src/Hive.Infrastructure/Configuration`:

- `HiveOptions` owns the `Hive` section name and exposes `Node`.
- `NodeOptions` exposes the configured `Roles` collection.
- `NodeRoleNames` defines `agents`, `gateway`, `connectors`, and `api`, plus a stable collection containing all canonical values.
- `ConnectionStringNames` defines the canonical `PostgreSql` name used by `GetConnectionString("PostgreSql")` in later tasks.

The options properties remain mutable and have non-null defaults so they are compatible with the standard .NET configuration binder introduced in US-F0-01-T05. This task does not reference or invoke that binder.

## Configuration Files

Both base `appsettings.json` files define `ConnectionStrings:PostgreSql` with an empty value. This records the key without committing credentials; environment variables, user secrets, Compose, or Kubernetes must provide an operational value.

Role defaults follow bible §5.10 exactly:

| File | Roles |
| --- | --- |
| `src/Hive.Api/appsettings.json` | `api` |
| `src/Hive.Worker/appsettings.json` | `agents`, `gateway`, `connectors` |
| `src/Hive.Api/appsettings.Development.json` | `api`, `agents`, `gateway`, `connectors` |

`src/Hive.Worker/appsettings.Development.json` does not override roles. It inherits the worker base roles. The all-in-one profile starts only `Hive.Api`; orchestration that prevents `Hive.Worker` from starting belongs to later hosting work.

## Environment-Variable Contract

The repository documentation records the standard .NET hierarchical environment-variable equivalents:

```text
ConnectionStrings__PostgreSql=<connection string>
HIVE__NODE__ROLES__0=api
HIVE__NODE__ROLES__1=agents
```

Environment variables replace the corresponding JSON values according to the standard .NET configuration precedence once the bootstrap is implemented. Secrets are never documented with real values.

## Error Handling

T04 does not perform runtime validation. The model uses empty non-null defaults so absence is represented explicitly and can be rejected by US-F0-01-T05. That later task must reject an empty PostgreSQL connection string, an empty role list, and unknown role names. T04 only makes those states representable and testable.

## Testing

Tests in `Hive.Tests` will:

- verify the exact section and connection-string names;
- verify the four canonical role values and their uniqueness;
- verify non-null empty defaults on the options model;
- parse each relevant JSON file and verify the connection-string key;
- verify the base role defaults for both executables;
- verify the `Hive.Api` development all-in-one override;
- verify that the worker development file does not redefine roles;
- verify that no credentials are committed in `ConnectionStrings:PostgreSql`.

The tests may add a direct `ProjectReference` from `Hive.Tests` to `Hive.Infrastructure` because the tests exercise public configuration types. No production dependency direction changes are required by T04.

## Bible Update

At completion, `docs/bible.html` will be advanced with an implementation note recording that:

- the shared model is owned by `Hive.Infrastructure`;
- base tracked connection-string values are intentionally empty;
- `Hive.Api` development configuration is the explicit all-in-one override;
- `Hive.Worker` development configuration inherits its base roles;
- runtime binding and validation remain assigned to US-F0-01-T05.

## Verification

Implementation follows test-driven development:

1. Add focused tests and observe the expected failures.
2. Add the minimal model, constants, JSON settings, and documentation required for the tests to pass.
3. Run the focused tests.
4. Run the complete test project and build the solution.
5. Review the final diff against US-F0-01-T04 and bible §5.10.

