# HIVE Configuration

The canonical product and architecture decisions remain in `docs/bible.html`. This document is the operational reference for the configuration contract implemented by US-F0-01-T04 and its common bootstrap implemented by US-F0-01-T05.

## Sources and precedence

Both executable projects use the standard .NET configuration hierarchy. Base `appsettings.json` values are overridden by `appsettings.{Environment}.json`, environment variables, and command-line values according to the default host builders.

`Hive.Api` and `Hive.Worker` call the same bootstrap from `Hive.Infrastructure.Configuration`. It binds the `Hive` section to `HiveOptions`, registers it in dependency injection, validates node roles when the host starts, and configures the common structured logging described below.

## Logging

Both executables get one common, structured logging configuration through the shared bootstrap (US-F0-01-T07). `AddHiveBootstrap` calls `AddHiveStructuredLogging` from `Hive.Infrastructure.Logging`, which clears the default providers and registers the built-in JSON console formatter as the single sink. Output is machine-readable JSON with scopes included and UTC timestamps, so both hosts emit an identical structured stream to stdout — the collection point under Docker Compose.

The standard `Logging` section in each `appsettings.json` keeps driving log levels and category filters through the normal options pipeline; the bootstrap fixes the provider set and output format, not the level filters. Adjust verbosity per category as usual:

```jsonc
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "Microsoft.Hosting.Lifetime": "Information"
  }
}
```

The Akka actor system (US-F0-01-T06) routes its own logs through the host's `ILoggerFactory` via `ConfigureLoggers(... AddLoggerFactory())`, so actor-system messages share the same structured format and level filtering instead of Akka's default unstructured stdout logger. Richer observability — OpenTelemetry, metrics, and distributed tracing correlated by `ThreadId`/`DirectiveId` (§11) — is reserved for later phases.

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

## Cluster

The `IRoleWorkload` seam and the `Hive:Cluster`/`ActorSystem` contract are defined in `docs/bible.html` (§5.10). This section is the operational reference for configuring the cluster binding.

The `ActorSystem` is named `hive`; seed-node URIs depend on this name. The `Hive:Cluster` section configures the cluster connection:

```jsonc
"Hive": {
  "Cluster": {
    "Hostname": "",
    "Port": 0,
    "SeedNodes": []
  }
}
```

When `SeedNodes` is empty the node self-seeds and forms a single-node cluster, which lets either executable start standalone. Multi-node topologies (US-F0-02) supply hostname, port, and seed nodes through the standard .NET hierarchical environment variables:

```text
HIVE__CLUSTER__HOSTNAME=node-a
HIVE__CLUSTER__PORT=8081
HIVE__CLUSTER__SEEDNODES__0=akka.tcp://hive@node-a:8081
```

The cluster roles mirror `Hive:Node:Roles`; the host starts only the `IRoleWorkload` implementations whose role the node declares and stops them in reverse order.

## Health checks

Both executables register the same minimal health checks through the shared bootstrap (US-F0-01-T08). `AddHiveBootstrap` calls `AddHiveHealthChecks` from `Hive.Infrastructure.Diagnostics`, so the two hosts cannot drift and every check is resolved from dependency injection. The checks are split into liveness and readiness by tag:

| Check | Tag | Healthy when |
| --- | --- | --- |
| `process` | `live` | The host can run the check at all — the process is alive and responsive. |
| `configuration` | `ready` | The typed `Hive` options are loaded with at least one active node role. |
| `dependencies` | `ready` | Every mandatory external dependency is configured. In F0 that is the `ConnectionStrings:PostgreSql` value. |

Liveness (`live`) answers "is the process up?" and stays healthy while the host runs. Readiness (`ready`) answers "can this node serve work?" and is intentionally unhealthy until mandatory configuration is supplied: because `ConnectionStrings:PostgreSql` is empty in tracked source files (see PostgreSQL above), a node reports not-ready until the connection string is provided per environment. This is the readiness contract that US-F0-02-T09 relies on under Docker Compose.

US-F0-01-T08 registers the checks only; the HTTP endpoints that expose them filtered by tag are the diagnostic endpoint below (US-F0-01-T09). Later mandatory dependencies extend the `dependencies` check without changing this seam.

## Diagnostic endpoint

The `Hive.Api` host exposes a minimal diagnostic surface (US-F0-01-T09). It is mapped by `MapHiveDiagnostics` and reuses the health checks above selected by the `live`/`ready` tags. `Hive.Worker` has no HTTP server, so it exposes no endpoints; probing backend nodes under Docker Compose is US-F0-02-T08/T09.

| Route | Purpose | Response |
| --- | --- | --- |
| `/health/live` | Liveness probe (`live`-tagged checks). | `200` healthy, `503` unhealthy. |
| `/health/ready` | Readiness probe (`ready`-tagged checks). | `200` healthy, `503` until mandatory configuration is present. |
| `/diagnostics` | Version, active roles, and startup state. | `200` with JSON. |

`/diagnostics` returns the running version, the canonical active roles, and the startup state expressed as the same `live`/`ready` roll-up:

```jsonc
{
  "version": "1.0.0",
  "roles": ["api"],
  "live": true,
  "ready": false
}
```

`ready` stays `false` until `ConnectionStrings:PostgreSql` is supplied, matching the readiness contract in §11.1. The probe routes return the standard `200`/`503` status codes so orchestration (US-F0-02 Docker health checks) can consume them directly.
