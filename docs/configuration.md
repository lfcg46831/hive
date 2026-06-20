# HIVE Configuration

The canonical product and architecture decisions remain in `docs/bible.html`. This document is the operational reference for the configuration contract implemented by US-F0-01-T04 and its common bootstrap implemented by US-F0-01-T05.

## Sources and precedence

Both executable projects use the standard .NET configuration hierarchy. Base `appsettings.json` values are overridden by `appsettings.{Environment}.json`, environment variables, and command-line values according to the default host builders.

`Hive.Api` and `Hive.Worker` call the same bootstrap from `Hive.Infrastructure.Configuration`. It binds the `Hive` section to `HiveOptions`, registers it in dependency injection, and validates node roles when the host starts.

## PostgreSQL

Both executables declare an empty `ConnectionStrings:PostgreSql` value. Supply an operational value outside tracked source files:

```text
ConnectionStrings__PostgreSql=Host=localhost;Port=5432;Database=hive;Username={user};Password={secret}
```

The same F0 database serves journal/snapshots, registry, audit log, read models, budgets, and scheduler idempotency. Each subsystem retains ownership of its schemas, tables, and migrations. T05 does not validate or open this connection; PostgreSQL consumers are introduced with their owning subsystems.

## Node roles

The canonical values are `agents`, `gateway`, `connectors`, and `api`.

Base defaults:

- `Hive.Api`: `api`
- `Hive.Worker`: `agents`, `gateway`, `connectors`

`Hive.Api/appsettings.Development.json` is the explicit local all-in-one override and declares all four roles. Do not start `Hive.Worker` in that profile.

Override role array entries with standard .NET hierarchical environment variables:

```text
HIVE__NODE__ROLES__0=api
HIVE__NODE__ROLES__1=agents
HIVE__NODE__ROLES__2=gateway
HIVE__NODE__ROLES__3=connectors
```

At least one role is required. Values are recognized after `Trim` with case-insensitive comparison, but the bound values are not rewritten. Empty entries, unknown values, and duplicates after trimming and case-insensitive comparison stop host startup with an error that identifies `Hive:Node:Roles` and the offending values.

T05 binds and validates roles only. Applying roles to Akka.Cluster and activating matching workloads belongs to US-F0-01-T06.
