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
| `HIVE__ORGANIZATIONS__ROOTPATH` | appsettings / deployment override | Root containing one directory per organization; defaults to `config/organizations` relative to the application directory. |
| `HIVE__CLUSTER__HOSTNAME` | compose (US-F0-02-T06) | Stable DNS name other nodes dial in multi-node topologies. |
| `HIVE__CLUSTER__SEEDNODES__0` | compose | Join target (`akka.tcp://hive@<host>:<port>`); self-seeds a single node when empty. |
| `HIVE__AGENTS__NUMBEROFSHARDS` | unset (extractor default `50`) | Number of Cluster Sharding shards for the position entity type, pinned by `agents`-role hosts (`Hive:Agents:NumberOfShards`, US-F0-06-T04b). A durable placement contract: keep it identical on every node and never change it while positions are persisted, since changing it reshuffles every position. Must be greater than zero when set. |
| `HIVE__AGENTS__REMEMBERENTITIES` | `true` | Whether the position shard region remembers its entities (`Hive:Agents:RememberEntities`, US-F0-06-T04c). On (default), positions kept warm by an active agenda/subscription survive rebalance and node restart; inactive positions that passivate are forgotten and reactivated on demand. A durable placement contract — keep it identical on every node and do not change it while positions are persisted. |
| `HIVE__AGENTS__PASSIVATEIDLEAFTER` | unset (workload default `00:02:00`) | Initial inactivity threshold after which an idle position is eligible for passivation (`Hive:Agents:PassivateIdleAfter`, US-F0-06-T04c), as a `hh:mm:ss` span. With remember-entities on (the default) Akka.NET region auto-idle is disabled, so this is the initial threshold the safe-passivation protocol (US-F0-06-T11) uses; with remember-entities off the region auto-passivates entities idle for longer than it. Must be greater than zero when set. |
| `HIVE__AGENTS__CLUSTERUPTIMEOUT` | unset (workload default `00:00:30`) | Maximum time the `agents` workload waits for the `ActorSystem` to reach cluster *Up* before initializing Cluster Sharding for the `PositionActor` (`Hive:Agents:ClusterUpTimeout`, US-F0-06-T04d), as a `hh:mm:ss` span. Sharding only starts once the node is a full cluster member; if *Up* is not reached within this window the workload fails the node startup observably (`ClusterStartupTimeoutException`) instead of starting sharding on a node that has not joined. Must be greater than zero when set. |

## Run with Docker Compose

The root `docker-compose.yml` (US-F0-02-T03) is the base local environment: PostgreSQL plus a single HIVE node (the `Hive.Api` host built from the root `Dockerfile`). The node self-seeds a one-node Akka cluster and is wired to the `postgres` service through `ConnectionStrings__PostgreSql`, so its readiness check is satisfied.

Compose requires `POSTGRES_USER`, `POSTGRES_PASSWORD`, and `POSTGRES_DB`. The tracked `.env.example` supplies safe local-only values; copy it to the ignored `.env` file before starting the stack. Compose reads `.env` automatically. Do not reuse the example credentials outside local development or commit real credentials in `.env.example`.

### Local stack lifecycle

Run all commands from the repository root with Docker Compose v2. Create the ignored local environment file once before starting either topology.

PowerShell:

```powershell
Copy-Item .env.example .env
```

Bash:

```bash
cp .env.example .env
```

Start, inspect, and stop the one-node topology with the base Compose file.

PowerShell:

```powershell
# Start PostgreSQL and one HIVE node.
docker compose up --build

# In another terminal, inspect container health and the API.
docker compose ps
Invoke-RestMethod http://localhost:8080/health/live
Invoke-RestMethod http://localhost:8080/health/ready
Invoke-RestMethod http://localhost:8080/diagnostics

# Stop containers while preserving PostgreSQL data.
docker compose down
```

Bash:

```bash
# Start PostgreSQL and one HIVE node.
docker compose up --build

# In another terminal, inspect container health and the API.
docker compose ps
curl -fsS http://localhost:8080/health/live
curl -fsS http://localhost:8080/health/ready
curl -fsS http://localhost:8080/diagnostics

# Stop containers while preserving PostgreSQL data.
docker compose down
```

Start, inspect, and stop the three-node topology by passing the base and cluster files to every Compose command.

PowerShell:

```powershell
docker compose -f docker-compose.yml -f docker-compose.cluster.yml up --build
docker compose -f docker-compose.yml -f docker-compose.cluster.yml ps
Invoke-RestMethod http://localhost:8080/diagnostics
docker compose -f docker-compose.yml -f docker-compose.cluster.yml down
```

Bash:

```bash
docker compose -f docker-compose.yml -f docker-compose.cluster.yml up --build
docker compose -f docker-compose.yml -f docker-compose.cluster.yml ps
curl -fsS http://localhost:8080/diagnostics
docker compose -f docker-compose.yml -f docker-compose.cluster.yml down
```

To switch topologies, stop the active topology with the same file set used to start it, then start the target topology. Do not add or remove the cluster override from an already-running stack.

PowerShell:

```powershell
# Switch from one node to three nodes.
docker compose down
docker compose -f docker-compose.yml -f docker-compose.cluster.yml up --build

# Switch from three nodes to one node.
docker compose -f docker-compose.yml -f docker-compose.cluster.yml down
docker compose up --build
```

Bash:

```bash
# Switch from one node to three nodes.
docker compose down
docker compose -f docker-compose.yml -f docker-compose.cluster.yml up --build

# Switch from three nodes to one node.
docker compose -f docker-compose.yml -f docker-compose.cluster.yml down
docker compose up --build
```

**Destructive cleanup:** the following commands remove containers, orphaned containers, and named volumes, including all PostgreSQL data in `hive-pgdata`. Use the command matching the active topology.

PowerShell:

```powershell
# Clean a one-node stack and its volumes.
docker compose down --volumes --remove-orphans

# Clean a three-node stack and its volumes.
docker compose -f docker-compose.yml -f docker-compose.cluster.yml down --volumes --remove-orphans
```

Bash:

```bash
# Clean a one-node stack and its volumes.
docker compose down --volumes --remove-orphans

# Clean a three-node stack and its volumes.
docker compose -f docker-compose.yml -f docker-compose.cluster.yml down --volumes --remove-orphans
```

### Local stack smoke check

After the selected topology is running and its containers have become healthy, run the non-mutating smoke check from the repository root. The command validates the resolved node count, Docker health for PostgreSQL and every expected HIVE node, PostgreSQL connectivity through `pg_isready`, and API readiness over HTTP. It does not start, stop, rebuild, or clean the stack.

One-node topology:

```powershell
./scripts/Test-LocalStack.ps1 -ExpectedNodes 1
```

Three-node topology:

```powershell
./scripts/Test-LocalStack.ps1 -ExpectedNodes 3
```

The command exits `0` only when every check passes and exits non-zero with the failed invariant otherwise. Pass the node count matching the Compose file set used to start the environment.

The base file declares an explicit internal network and port policy (US-F0-02-T06, see below), a named persistent PostgreSQL volume (US-F0-02-T07, see below), Docker health/readiness checks for PostgreSQL and the HIVE node (US-F0-02-T08/T09, see below), and the local environment-variable contract from `.env.example` (US-F0-02-T10). Per-service roles remain optional through the `docker-compose.roles.yml` override (US-F0-02-T05).

### Persistent storage

PostgreSQL stores its data directory in the named volume `hive-pgdata` (mounted at `/var/lib/postgresql/data`), declared in the base `docker-compose.yml` (US-F0-02-T07). This replaces the image's implicit anonymous volume so the backing store survives container recreation and `docker compose down`, and is a named, inspectable target (`docker volume inspect <project>_hive-pgdata`). The data is removed only by the explicit destructive cleanup documented in the lifecycle section above (or by `docker volume rm`). The same volume backs every F0 subsystem (journal/snapshots, registry, audit log, read models, budgets, scheduler idempotency), since they share one database.

A second named volume for local logs is defined but kept optional and disabled by default: both hosts emit structured JSON to stdout (collected by Compose, see Logging above), so there is no on-disk log path to persist. The `hive-logs` volume and its mount on the `api` service are left commented in the base file, ready to enable if a file log sink is ever added.

### Health checks

Every container in the base `docker-compose.yml` declares a Docker health check (US-F0-02-T08/T09) so the orchestrator can distinguish a ready service from one that cannot yet serve work and surface it as `healthy`/`unhealthy` in `docker compose ps`.

| Service | Check | How |
| --- | --- | --- |
| `postgres` | Server accepts connections | `pg_isready` (ships in the image), run against `127.0.0.1` with the container's `POSTGRES_USER`/`POSTGRES_DB`. |
| `api` (and `api2`/`api3`) | Mandatory node configuration is loaded | `curl -fsS http://127.0.0.1:8080/health/ready`, the `ready` endpoint from §11.1. It checks that at least one valid role and `ConnectionStrings:PostgreSql` are configured; `-f` makes curl fail on the 503 returned while not ready. |

All checks use the same cadence: `interval: 10s`, `timeout: 5s`, `retries: 5`, with a `start_period` grace (30s for PostgreSQL, 40s for the node to cover .NET cold start) during which failures don't count against the container. The runtime image installs `curl` (the aspnet base ships no HTTP client) precisely so the node check can probe its own HTTP endpoint. The three-node override gives the added nodes (`api2`, `api3`) the identical readiness check; the seed `api` inherits the base one by compose merge.

Compose also gates start-up on health rather than container creation. The base `api` waits for `postgres` with `condition: service_healthy`; in the three-node topology, `api2` and `api3` wait for both PostgreSQL and the seed `api` to report healthy. This ordering complements the current readiness contract: `/health/ready` validates that mandatory dependency configuration is present, while PostgreSQL's own health check confirms that the configured local service is accepting connections.

Use the topology-matching `docker compose ... ps` command from the lifecycle section to watch every container resolve to `healthy`.

### Three-node cluster

`docker-compose.cluster.yml` (US-F0-02-T04) is an override that turns the single-node base into a real 3-node Akka cluster, layered on top without editing the base file. Adding or omitting the override is how a developer switches between 1 and 3 nodes.

The base `api` node is promoted into the cluster seed: it is pinned to its compose DNS name (`api`) and its seed list points at itself, so the two added nodes (`api2`, `api3`) join via the shared seed `akka.tcp://hive@api:8081` and all three converge into one cluster. The added nodes join the same `hive-net` network with matching DNS aliases. Every node keeps its image-default role (interchangeable `api` nodes); distinct per-service roles are layered by the `docker-compose.roles.yml` override (US-F0-02-T05, see below). Per the port policy below, only the base `api` publishes 8080; the extra nodes and the Akka cluster port stay internal to `hive-net`.

Use the three-node commands in the lifecycle section to operate this topology. Plain `docker compose up` (without `-f docker-compose.cluster.yml`) still starts the one-node base.

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

The organization registry owns the `registry` schema and uses this same connection string. The common host bootstrap automatically applies its embedded, versioned SQL migrations and then imports every organization under `Hive:Organizations:RootPath` before role workloads start; concurrent cluster nodes serialize migration/import writers and unchanged YAML becomes a no-op. Migration `001_registry.sql` creates the organization header and the unit, position, occupant, authority, schedule, command-relation, and per-organization import-lock tables. No additional credential is required beyond `ConnectionStrings:PostgreSql`; the configured database user must be allowed to create and modify the owned `registry` schema. A host with no configured connection string skips migration/import and remains not-ready under the existing readiness contract; a configured database that cannot connect/migrate or an absent/invalid organization tree fails host startup.

### PositionActor persistence (journal and snapshots)

The `PositionActor` journal and snapshot store use `Akka.Persistence.Sql` (Linq2Db, PostgreSQL provider — ADR-003, replacing the deprecated `Akka.Persistence.PostgreSql`) over the same `ConnectionStrings:PostgreSql` value, in a dedicated `persistence` schema isolated from the registry and the other subsystems that share the database (US-F0-06-T05a). The common bootstrap applies its embedded, versioned migration `001_persistence.sql` — serialized across nodes by an advisory lock and recorded in `persistence.schema_migrations` — before the `agents` workload starts the persistent entity. The migration owns and versions the dedicated `persistence` schema; the journal/snapshot/tag/metadata table DDL is owned by the plugin and created by auto-initialization inside that schema on first use, so the schema must exist first. The configured database user must be allowed to create and modify the owned `persistence` schema; no credential beyond `ConnectionStrings:PostgreSql` is required.

When the connection string is absent the persistence plugins are not wired into the actor system, the migration is skipped, and the node stays not-ready under the existing readiness contract; when it is present, a failed connection or migration aborts host startup, exactly like the registry. The schema name is a durable contract and must not change while journals exist. Binding the versionable serializers for commands, events, and snapshots (no default .NET serialization) is US-F0-06-T05b.

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
| `persistence` | `ready` | The `PositionActor` journal/snapshot persistence is configured (`ConnectionStrings:PostgreSql` present). The dedicated `persistence` schema is guaranteed by the migration that aborts startup if it cannot apply; the journal/snapshot tables are auto-initialized by the Akka.Persistence.Sql plugin inside that schema (US-F0-06-T05a). |

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

## Organization GitOps configuration

The organization definition is GitOps source of truth (bible §4.7): organizations, units, positions, occupants, prompts, schedules, subscriptions and authority live in versioned files that are validated and imported into the registry/read model at startup/deploy. The canonical YAML schema — the four top blocks (`organization`, `prompts`, `units`, `positions`) and their fields — is fixed in bible §4.8 and is not repeated here. This section fixes only the operational layout: where the files live, how they are named, and how the paths inside the document resolve (US-F0-05-T02). The typed model is US-F0-05-T03, the parser is US-F0-05-T04, the worked Engineering/Delivery example is US-F0-05-T08, and the idempotent import is US-F0-05-T09.

### Repository layout

All organization configuration lives under a single tracked root, `config/organizations/`, with one directory per organization named exactly by its `organization.id`:

```text
config/
  organizations/
    <organization-id>/
      organization.yaml      # the organization document (§4.8 top blocks)
      prompts/               # identity prompts referenced by prompts[].path
        <prompt-id>.md
```

For the example schema in §4.8 (`organization.id: acme-delivery`):

```text
config/
  organizations/
    acme-delivery/
      organization.yaml
      prompts/
        ceo-v1.md
        engineer-v1.md
```

One directory holds exactly one organization. The directory name must equal the `organization.id` declared inside `organization.yaml`; the import treats a mismatch as a configuration error so the on-disk path and the logical identifier never diverge. Multiple organizations are multiple sibling directories under `config/organizations/`, each self-contained.

### File naming and path resolution

The organization document is always named `organization.yaml` (lowercase, `.yaml` extension, not `.yml`). It is the only file the parser loads directly; every other file is reached through a reference inside it.

Paths in the document — `prompts[].path` today, and any future file-valued field — are **relative to the organization directory** (the directory that contains `organization.yaml`), use forward slashes, and must stay inside that directory. Absolute paths, drive letters, and `..` segments that escape the organization directory are rejected so a document can never reference files outside its own tree. This keeps each organization directory portable: it can be copied, reviewed, or imported as a unit without rewriting paths.

Identity prompts live under `prompts/` and are named by their catalogue `id` with a `.md` extension (`prompts/ceo-v1.md` for `id: ceo-v1`). Versioning is expressed in the `id`/filename (e.g. `ceo-v1`, `ceo-v2`) rather than through Git history alone, so a position's `identity_prompt_ref` pins an explicit, reviewable prompt revision.

### GitOps workflow

Because the files are the source of truth, every structural change — adding a unit, moving a position, editing a prompt, changing authority — is a Git commit reviewed through the normal pull-request flow; the F0 console is read-only and does not edit structure (bible §4.7). The import is validated and idempotent (US-F0-05-T05–T07, T09): committing the same configuration twice produces no duplicates and no unnecessary functional-timestamp churn, so re-running a deploy on an unchanged tree is a safe no-op. Secrets and operational connection values are never placed in these files; they stay in environment variables as described above under PostgreSQL and the role/cluster sections.

Both executable outputs and the Docker image include the tracked `config/organizations` tree. At startup the relative default is resolved from the application directory; deployments that mount configuration elsewhere override it with `HIVE__ORGANIZATIONS__ROOTPATH`. Every immediate child directory must contain `organization.yaml` and its directory name must equal `organization.id`; any parse or semantic validation error aborts startup before workloads run.

## Message serialization

The organizational message protocol is serialized with `System.Text.Json` (ADR-007, §9.9). `AddHiveActorSystem` binds the `OrgMessage` base type to a custom Akka serializer (US-F0-03-T08), so every canonical subtype is delivered as JSON over remoting/cluster instead of Akka's default serializer. There is nothing to configure per environment; the binding is code-defined and the format is the same one intended for persisted events and snapshots.

| Property | Value |
| --- | --- |
| Serializer | `Hive.Actors.Serialization.OrgMessageJsonSerializer` (a `SerializerWithStringManifest`) |
| HOCON serializer name | `hive-org-message` |
| Numeric identifier | `0x48495645` (decimal `1213486149`, spells "HIVE") — stable, never reused |
| Bound type | `Hive.Domain.Messaging.OrgMessage` (covers all subtypes by hierarchy) |
| Manifest per message | canonical kebab-case token: `directive`, `report`, `memo`, `escalation`, `peer-request`, `peer-response`, `approval-request`, `approval-decision`, `pulse`, `event-trigger` |

Payloads are UTF-8 JSON and human-readable, which keeps remote messages and persisted entries inspectable in F0. Identity value objects serialize as their raw textual/Guid value, the `EndpointRef` union uses an explicit `kind` discriminator (`position`, `organization-owner`, `system`), and the protocol enums use the canonical lowercase/kebab-case wire values from §9.5. Unknown JSON properties are ignored on read, but missing required fields are rejected by the domain constructors — there are no silent defaults. The numeric identifier and the per-type manifests are part of the on-the-wire/persisted contract and must not change while journals exist; the evolution rules live in §9.9 of the bible.
