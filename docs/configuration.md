# HIVE Configuration

The canonical product and architecture decisions remain in `docs/bible.html`. This document is the operational reference for the configuration contract implemented by US-F0-01-T04 and its common bootstrap implemented by US-F0-01-T05.

## Run locally without Docker Compose

Install the .NET 8 SDK, then restore and start the API host from the repository root:

```powershell
dotnet restore Hive.sln
dotnet run --project src/Hive.Api
```

The `Hive.Api` development profile starts a local all-in-one node with every role and listens on `http://localhost:53496`. In another PowerShell session, inspect it with:

```powershell
Invoke-RestMethod http://localhost:53496/health/live
Invoke-RestMethod http://localhost:53496/diagnostics
```

Stop the host with `Ctrl+C`. Readiness is expected to return `503` until `ConnectionStrings__PostgreSql` is set; at this stage the check validates that the setting is present but does not open a database connection. To exercise the readiness path locally, restart the host with the variable set in the same session:

```powershell
$env:ConnectionStrings__PostgreSql = "Host=localhost;Port=5432;Database=hive;Username=hive;Password=hive"
dotnet run --project src/Hive.Api
```

Then query readiness from another session:

```powershell
Invoke-RestMethod http://localhost:53496/health/ready
```

To run only the non-HTTP worker roles instead, use `dotnet run --project src/Hive.Worker`. The worker writes structured logs to stdout and has no diagnostic HTTP endpoints.

## Build the container image

The root `Dockerfile` (US-F0-02-T01) is a multi-stage build: a `sdk:8.0` stage restores and publishes a Release build, and a slim `aspnet:8.0` runtime stage runs the published output as the non-root `app` user. It builds the `Hive.Api` host by default and the `Hive.Worker` host on request. Runtime environment variables, exposed ports and the Akka/role wiring are added by later tasks (US-F0-02-T02+), so a bare `docker run` of this image still needs that configuration to form a cluster.

```powershell
# API host (default): serves /health and /diagnostics, can run any role.
docker build -t hive:api .

# Worker host: non-HTTP worker roles.
docker build `
  --build-arg APP_PROJECT=src/Hive.Worker/Hive.Worker.csproj `
  --build-arg APP_DLL=Hive.Worker.dll `
  -t hive:worker .
```

## Container runtime configuration

The runtime stage (US-F0-02-T02) declares the env-var contract the image runs with. Settings use the standard .NET hierarchical convention (`__` separates sections, `__0` indexes array entries), so they bind onto the same `Hive:*`/`appsettings` model below with no code change. Compose layers the per-deployment values on top (US-F0-02-T03+).

Image-level defaults baked into the image:

| Variable | Default | Purpose |
| --- | --- | --- |
| `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` | `Production` | Keeps containers off the local all-in-one `appsettings.Development.json`; each host falls back to its own per-executable roles. |
| `ASPNETCORE_HTTP_PORTS` | `8080` | Kestrel listen port for `/health` and `/diagnostics` on the `api` host. Inert on the non-HTTP worker. |
| `HIVE__CLUSTER__PORT` | `8081` | Akka remoting/cluster bind port (`Hive:Cluster:Port`). |

`EXPOSE 8080 8081` documents these ports; it is metadata only, so compose decides which are actually published (US-F0-02-T06).

Per-deployment overrides are intentionally not pinned in the image and are supplied per service:

| Variable | Supplied by | Notes |
| --- | --- | --- |
| `HIVE__NODE__ROLES__0` | compose, per service (US-F0-02-T05) | Active node role. Defaults to each host's `appsettings.json` when unset. |
| `ConnectionStrings__PostgreSql` | operator / compose env | Required dependency; left empty so readiness stays not-ready until provided. No baked-in credentials. |
| `HIVE__CLUSTER__HOSTNAME` | compose (US-F0-02-T06) | Stable DNS name other nodes dial in multi-node topologies. |
| `HIVE__CLUSTER__SEEDNODES__0` | compose | Join target (`akka.tcp://hive@<host>:<port>`); self-seeds a single node when empty. |

## Run with Docker Compose

The root `docker-compose.yml` (US-F0-02-T03) is the base local environment: PostgreSQL plus a single HIVE node (the `Hive.Api` host built from the root `Dockerfile`). The node self-seeds a one-node Akka cluster and is wired to the `postgres` service through `ConnectionStrings__PostgreSql`, so its readiness check is satisfied. Local-dev credentials (`hive`/`hive`/`hive`) are inlined for a self-contained base; US-F0-02-T10 extracts them into `.env.example`.

```powershell
# Build the node image and start PostgreSQL + one HIVE node.
docker compose up --build

# Once up, the node serves the diagnostics surface on 8080:
Invoke-RestMethod http://localhost:8080/health/live
Invoke-RestMethod http://localhost:8080/health/ready
Invoke-RestMethod http://localhost:8080/diagnostics

# Stop the stack.
docker compose down
```

The base file declares an explicit internal network and port policy (US-F0-02-T06, see below), a named persistent PostgreSQL volume (US-F0-02-T07, see below), and Docker health checks for PostgreSQL and the HIVE node (US-F0-02-T08, see below); it intentionally leaves later concerns to their own tasks: per-service roles via the `docker-compose.roles.yml` override (US-F0-02-T05), readiness gating (US-F0-02-T09), and `.env.example` (US-F0-02-T10).

### Persistent storage

PostgreSQL stores its data directory in the named volume `hive-pgdata` (mounted at `/var/lib/postgresql/data`), declared in the base `docker-compose.yml` (US-F0-02-T07). This replaces the image's implicit anonymous volume so the backing store survives container recreation and `docker compose down`, and is a named, inspectable target (`docker volume inspect <project>_hive-pgdata`). The data is removed only by an explicit `docker compose down -v` (or `docker volume rm`). The same volume backs every F0 subsystem (journal/snapshots, registry, audit log, read models, budgets, scheduler idempotency), since they share one database.

A second named volume for local logs is defined but kept optional and disabled by default: both hosts emit structured JSON to stdout (collected by Compose, see Logging above), so there is no on-disk log path to persist. The `hive-logs` volume and its mount on the `api` service are left commented in the base file, ready to enable if a file log sink is ever added.

### Health checks

Every container in the base `docker-compose.yml` declares a Docker health check (US-F0-02-T08) so the orchestrator can distinguish a live container from a broken one and surface it as `healthy`/`unhealthy` in `docker compose ps`.

| Service | Check | How |
| --- | --- | --- |
| `postgres` | Server accepts connections | `pg_isready` (ships in the image), run against `127.0.0.1` with the container's `POSTGRES_USER`/`POSTGRES_DB`. |
| `api` (and `api2`/`api3`) | Node process is alive and serving | `curl -fsS http://127.0.0.1:8080/health/live`, the `live` endpoint from §11.1. `-f` makes curl fail on the 503 the endpoint returns when unhealthy. |

All checks use the same cadence: `interval: 10s`, `timeout: 5s`, `retries: 5`, with a `start_period` grace (30s for PostgreSQL, 40s for the node to cover .NET cold start) during which failures don't count against the container. The runtime image installs `curl` (the aspnet base ships no HTTP client) precisely so the node check can probe its own HTTP endpoint. The three-node override gives the added nodes (`api2`, `api3`) the identical node check; the seed `api` inherits the base one by compose merge.

This task adds liveness-level checks only. Gating a node's reported health on mandatory configuration being loaded — probing `/health/ready` and wiring `depends_on` start-up ordering on these checks — is US-F0-02-T09.

```powershell
# After `docker compose up`, watch health status resolve to healthy:
docker compose ps
```

### Three-node cluster

`docker-compose.cluster.yml` (US-F0-02-T04) is an override that turns the single-node base into a real 3-node Akka cluster, layered on top without editing the base file. Adding or omitting the override is how a developer switches between 1 and 3 nodes.

The base `api` node is promoted into the cluster seed: it is pinned to its compose DNS name (`api`) and its seed list points at itself, so the two added nodes (`api2`, `api3`) join via the shared seed `akka.tcp://hive@api:8081` and all three converge into one cluster. The added nodes join the same `hive-net` network with matching DNS aliases. Every node keeps its image-default role (interchangeable `api` nodes); distinct per-service roles are layered by the `docker-compose.roles.yml` override (US-F0-02-T05, see below). Per the port policy below, only the base `api` publishes 8080; the extra nodes and the Akka cluster port stay internal to `hive-net`.

```powershell
# Start PostgreSQL + three HIVE nodes.
docker compose -f docker-compose.yml -f docker-compose.cluster.yml up --build

# The seed node still serves the diagnostics surface on 8080:
Invoke-RestMethod http://localhost:8080/diagnostics

# Stop the three-node stack (pass the same files used to start it).
docker compose -f docker-compose.yml -f docker-compose.cluster.yml down
```

Plain `docker compose up` (without `-f docker-compose.cluster.yml`) still starts the 1-node base.

### Per-service roles

`docker-compose.roles.yml` (US-F0-02-T05) is an override that assigns a distinct role set to each cluster node so the four canonical roles are spread across services instead of every node running its image-default role. It layers on top of both the base and the cluster override without editing either; bring it up with all three files, in order:

```powershell
docker compose -f docker-compose.yml -f docker-compose.cluster.yml -f docker-compose.roles.yml up --build
```

Each node is the same `Hive.Api` image (serves HTTP and can run any role); the role set only selects which `IRoleWorkload` implementations the node activates. The split covers every role: `api` keeps `api`, `api2` runs `agents`, and `api3` runs `gateway` and `connectors`. Roles are supplied per service through the §5.10 env-var contract: `Hive:Node:Roles` is an array, so `HIVE__NODE__ROLES__0` sets the first entry and `__1` the second, replacing the host's `appsettings.json` default by array index.

Omitting the file leaves the nodes on their image-default roles, so adding or removing it is how a developer switches between uniform and per-service roles. The same env-var override sets the role on the single-node base too — e.g. `HIVE__NODE__ROLES__0=agents` on the base `api` service runs that one node as `agents`.

### Internal network and ports

The base `docker-compose.yml` (US-F0-02-T06) puts every service on one explicit user-defined bridge network, `hive-net`, instead of the implicit compose default. The network is declared once in the base and the cluster/roles overrides only attach nodes to it. A user-defined network gives automatic DNS resolution, and each service publishes a stable alias (`postgres`, `api`, `api2`, `api3`) so cluster hostnames and the `ConnectionStrings__PostgreSql` host stay valid regardless of the compose project name or a service rename.

Ports are published to the host only where a developer needs them. The single port mapping in the whole stack is the api node's HTTP/diagnostics port:

| Port | Service | Host-published? | Reason |
| --- | --- | --- | --- |
| 8080 | `api` (seed) | Yes (`8080:8080`) | HTTP `/health` and `/diagnostics`; the surface a developer hits. |
| 8081 | every HIVE node | No (internal) | Akka remoting/cluster port; reached node-to-node by DNS only. |
| 5432 | `postgres` | No (internal) | Backing store; reached by nodes as `postgres:5432`. |
| 8080 | `api2`, `api3` | No (internal) | Sibling cluster nodes; not individually addressed from the host. |

The internal ports are declared with `expose` (documentation/metadata; on a bridge network all container ports are already reachable between services) and are never bound on the host. PostgreSQL is therefore not reachable from the host by default; for ad-hoc local inspection use `docker compose exec postgres psql -U hive`, or temporarily add a `ports: ["5432:5432"]` mapping in a personal override.

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
