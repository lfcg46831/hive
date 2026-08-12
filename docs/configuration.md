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
| `HIVE__PUBLICAPI__AUTHORIZATION__CREDENTIALS__<credentialIndex>__TOKEN` / `...__ORGANIZATIONIDS__<organizationIndex>` / `...__PERSONID` / `...__POSITIONS__<positionIndex>__ORGANIZATIONID|POSITIONID` | operator secret store / deployment environment | Static bearer credential, organization scope and optional F1 person-to-occupied-position binding for the public API. Tokens are secrets and must never be committed. |
| `HIVE__CLUSTER__HOSTNAME` | compose (US-F0-02-T06) | Stable DNS name other nodes dial in multi-node topologies. |
| `HIVE__CLUSTER__SEEDNODES__0` | compose | Join target (`akka.tcp://hive@<host>:<port>`); self-seeds a single node when empty. |
| `HIVE__AGENTS__NUMBEROFSHARDS` | unset (extractor default `50`) | Number of Cluster Sharding shards for the position entity type, pinned by `agents`-role hosts (`Hive:Agents:NumberOfShards`, US-F0-06-T04b). A durable placement contract: keep it identical on every node and never change it while positions are persisted, since changing it reshuffles every position. Must be greater than zero when set. |
| `HIVE__AGENTS__REMEMBERENTITIES` | `true` | Whether the position shard region remembers its entities (`Hive:Agents:RememberEntities`, US-F0-06-T04c). On (default), positions kept warm by an active agenda/subscription survive rebalance and node restart; inactive positions that passivate are forgotten and reactivated on demand. A durable placement contract — keep it identical on every node and do not change it while positions are persisted. |
| `HIVE__AGENTS__PASSIVATEIDLEAFTER` | unset (workload default `00:02:00`) | Initial inactivity threshold after which an idle position is eligible for passivation (`Hive:Agents:PassivateIdleAfter`, US-F0-06-T04c), as a `hh:mm:ss` span. With remember-entities on (the default) Akka.NET region auto-idle is disabled, so this is the initial threshold the safe-passivation protocol (US-F0-06-T11) uses; with remember-entities off the region auto-passivates entities idle for longer than it. Must be greater than zero when set. |
| `HIVE__AGENTS__CLUSTERUPTIMEOUT` | unset (workload default `00:00:30`) | Maximum time the `agents` workload waits for the `ActorSystem` to reach cluster *Up* before initializing Cluster Sharding for the `PositionActor` (`Hive:Agents:ClusterUpTimeout`, US-F0-06-T04d), as a `hh:mm:ss` span. Sharding only starts once the node is a full cluster member; if *Up* is not reached within this window the workload fails the node startup observably (`ClusterStartupTimeoutException`) instead of starting sharding on a node that has not joined. Must be greater than zero when set. |
| `HIVE__OUTCOMES__MODE` | `shadow` | Hybrid outcome rollout mode (`Hive:Outcomes:Mode`). `shadow` calculates and audits proposal→resolution but preserves the proposed message; `enforcement` applies the closed resolution before the existing action/routing gates. Only these exact lowercase values are valid. |
| `HIVE__OUTCOMES__VERIFIERTIMEOUT` | `00:00:15` | Maximum verifier call duration (`Hive:Outcomes:VerifierTimeout`) as a positive `hh:mm:ss` span. The effective request timeout is the minimum of this value, the position AI timeout and the remaining directive deadline; zero or a negative value fails startup validation. |
| `HIVE__OCCUPANTCHANNELS__EMAIL__SMTP__*` | operator secret store / deployment environment | Outbound occupant-email transport on `connectors` nodes. It is disabled by default; the complete setting contract and secret-handling rules are in [Outbound occupant email (SMTP)](#outbound-occupant-email-smtp). |

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

### F0 bug-triage vertical slice demo

The F0 demo uses the tracked ACME Delivery organization, the real OpenAI gateway adapter, and the reproducible demo seed `us-f0-10-t12-demo`. The demo profile selects the real provider by configuration only; the base hosts and the default test suite remain offline and use no external credentials.

Copy `.env.example` to the ignored `.env` file, then set `OPENAI_API_KEY` there. `OPENAI_MODEL_ID` is optional and defaults to `gpt-5-mini` when it is empty or absent. Never commit the populated `.env` file. Compose stops during configuration resolution when the key is missing, before starting a misconfigured demo.

Start the one-node demo topology:

```powershell
docker compose -f docker-compose.yml -f docker-compose.demo.yml up --build
```

Start the three-node demo topology with the role split from `docker-compose.roles.yml`:

```powershell
docker compose -f docker-compose.yml -f docker-compose.cluster.yml -f docker-compose.roles.yml -f docker-compose.demo.cluster.yml up --build
```

After the selected topology is healthy, submit the reproducible directive from another terminal:

```powershell
dotnet run --project src/Hive.DemoClient -- --submit --base-url http://localhost:8080 --seed us-f0-10-t12-demo
```

The command posts the canonical root `Directive` for the ACME bug-triage example to `/api/v1/organizations/acme-delivery/directives` with deterministic `MessageId`, `ThreadId`, `DirectiveId`, and `SentAt`. Reusing the same seed is intentional for restart/retry demonstrations because the vertical slice idempotency guards can recognize the same logical submission. Unlike the identifiers, the provider response is not deterministic when this real-provider demo profile is active.

#### Evaluation Lab (experimental)

The Evaluation Lab is disabled by default and runs as separate tooling against the bounded audit/export API. The commands below are operational reference for isolated experiments and historical evidence; their presence is not authorization to start a calibration, freeze, or holdout. The reference v1 manifest was used by the completed, separately authorized `US-F0-18-T01` calibration; its three `post-f0-8-calibration-001`/`002`/`003` ids are burned. The v2 manifest was used by the completed, separately authorized `US-F0-21-T01` calibration; its three `post-f0-10-calibration-001`/`002`/`003` ids are also burned. Both candidates are rejected for a freeze request. Historical profiles, run ids, and evidence must not be rewritten or rerun unless a new task explicitly authorizes a distinct experiment.

##### Post-F0.8 local qualification

`US-F0-17-T01` qualifies a reviewed commit without calling an AI provider or consuming a corpus. The complete default suite may use its local PostgreSQL Testcontainers; the experiment and Compose steps do not create application/evaluation containers. Run from a functionally clean worktree:

```powershell
dotnet build Hive.sln --no-restore -v minimal
dotnet test Hive.sln --no-restore -v minimal

$manifest='config/experiments/bug-triage-lab-v1/experiment.v1.json'
dotnet run --project src/Hive.Evaluation.Tooling --no-restore -- experiment prepare --manifest $manifest
$experimentEnv='artifacts/evaluation/experiments/bug-triage-lab-v1/compose.env'
$env:OPENAI_API_KEY='preflight-compose-validation-only'
try {
  docker compose --env-file .env.example --env-file $experimentEnv -f docker-compose.yml -f docker-compose.demo.yml -f docker-compose.experiment.yml config --quiet
} finally {
  Remove-Item Env:OPENAI_API_KEY -ErrorAction SilentlyContinue
}
```

The placeholder is not a provider credential and Compose is rendered only for validation. Do not pass `.env`, run `docker compose up`, execute either opt-in real-provider smoke, or invoke `evaluate`. A successful local report has verdict `ready-for-real-smoke`, which permits only a separate authorization request for `US-F0-17-T02`.

Historical bug-triage calibration and holdout runs used the evaluation-only audit/export profile below:

```powershell
docker compose -f docker-compose.yml -f docker-compose.demo.yml -f docker-compose.evaluation.yml -f docker-compose.postgres-external.yml up --build
```

`docker-compose.evaluation.yml` activates the `Hive:Evaluation:Profiles:BUGTRIAGE` audit/export adapter for the exact `acme-delivery` / `bug-triage` scope. Evaluation profiles are absent or disabled by default. Each enabled runtime profile requires only `Enabled=true`, `OrganizationId`, and `PositionId`; two enabled profiles cannot target the same scope, and `ConnectionStrings:PostgreSql` must be available for result capture. Historical `RubricPath` and `RubricVersion` keys may remain in frozen Compose profiles, but the runtime deliberately ignores them. The runtime does not load rubrics, scorers, evaluation instructions, run ids, or projection schemas and does not alter the normal actor prompt. Rubric loading, observation-instruction construction, projection, scoring, and reporting belong exclusively to `Hive.Evaluation.Tooling`.

To activate the tracked follow-up coordination fixture for an existing organizational position, add a separate scoped profile to that host configuration (replace the scope values with the position that will receive the evaluation workload):

```text
HIVE__EVALUATION__PROFILES__FOLLOWUP__ENABLED=true
HIVE__EVALUATION__PROFILES__FOLLOWUP__ORGANIZATIONID=<organization-id>
HIVE__EVALUATION__PROFILES__FOLLOWUP__POSITIONID=<follow-up-position-id>
```

Use `config/organizations/acme-delivery/examples/evaluation/follow-up-coordination-corpus.v1.json` and its tracked rubric as tooling inputs. The bundled evaluation Compose override and tooling defaults remain scoped to bug triage; do not enable the follow-up profile until its target organization/position exists and the submitting tooling routes the corpus to that same scope. Profile activation does not add a scorer or evaluation concept to a HIVE message contract.

An enabled profile activates only the bounded `hive.directive-audit-export` v1 adapter. The API route is `GET /api/v1/organizations/{organizationId}/threads/{threadId}/directives/{directiveId}/audit-export?after_sequence={n}`. It returns at most 100 scoped, redacted audit events per page and an opaque monotonic cursor. A terminal response may also contain the canonical organizational result as `application/vnd.hive.org-message+json`, capped at 64 KiB with byte length and SHA-256. When enforcement replaces an accepted result, the adapter may additionally expose `accepted_observation` v1 as `application/vnd.hive.accepted-observation+json`, capped at 4 KiB with byte length and SHA-256; it contains only the canonical ids/string labels envelope and is consumed only when the terminal audit says `proposalOverridden=true`. Prompt/input text, memory, the superseded message body, raw or rejected model output, reasoning, traces, tool payloads, authorization secrets/configuration, corpus references, baselines, rubric metadata, scores, and run ids are never exported. Result capture uses the runtime-owned `audit.directive_export_results` table; the tooling has no database access.

The override must be present when the `api` container is created; adding it only to a later tooling command does not change an existing container. If the audit/export endpoint remains non-terminal or unavailable, recreate the API with the evaluation Compose file set above and wait for the health check before retrying.

For every new experiment, use a versioned `hive.evaluation-experiment` v1 manifest and the single generic `docker-compose.experiment.yml` adapter. Do not add another Compose override for a model, timeout, prompt, or outcome-policy variation. The historical `bug-triage-lab-v1` manifest and organization remain byte-identical because their one authorization was consumed by `US-F0-18-T01`. BUG-005 introduced the separate `bug-triage-lab-v2` profile, whose one authorization was consumed by `US-F0-21-T01`; its completed decision is `rejected-for-freeze-request` in `evidence/evaluation/bug-triage-lab-v2/post-f0-10-calibration-report.v1.md`. The following commands remain useful for offline preparation and host reproduction, but do not authorize another corpus run:

```powershell
$manifest='config/experiments/bug-triage-lab-v2/experiment.v1.json'
dotnet run --project src/Hive.Evaluation.Tooling -- experiment prepare --manifest $manifest
$experimentEnv='artifacts/evaluation/experiments/bug-triage-lab-v2/compose.env'
docker compose --env-file .env --env-file $experimentEnv -f docker-compose.yml -f docker-compose.demo.yml -f docker-compose.experiment.yml config
docker compose --env-file .env --env-file $experimentEnv -f docker-compose.yml -f docker-compose.demo.yml -f docker-compose.experiment.yml up --build -d
```

Preparation is local and makes no network calls. It rejects unknown manifest fields, invalid ids/limits/policy, paths outside the repository, SHA-256 drift, duplicate reproducibility roles/paths, missing positions, an invalid source→destination hierarchy, or a provider/model/limits-version/per-call-timeout/execution-timeout/output/iteration value that differs from the selected AI position in the organization snapshot. It also requires the external runner timeout to exceed the end-to-end execution budget by more than one poll interval. It writes only ignored files under `artifacts/evaluation/experiments/<experiment-id>/`: `compose.env`, which contains interpolation values but no credentials, and `effective-configuration.v2.json`, which contains the normalized portable configuration and its SHA-256. Pass the ignored `.env` first so database/provider secrets remain in their existing owner; never add secrets to the manifest or generated experiment env file.

The generic adapter activates only the scoped `LAB` audit/export profile, selects the declared real-provider default, applies the declared outcome mode and verifier timeout, bind-mounts the hash-verified organization snapshot read-only, and labels the container with the resolved limits version, end-to-end timeout and per-call timeout. Position model, both execution timeouts, output ceiling, and iteration limit remain owned by that snapshot and are checked by the tooling. Inspect `docker compose ... config` before creating the container; it is the effective deployment configuration, so a stale generated env file must not be reused after a manifest change.

Choose a new canonical `run-id` for each independent measurement. Gate-bearing bug-triage runs must use the tracked calibration/holdout plan; it verifies the SHA-256 of the corpus, baseline/rubric, business prompt, organization/provider/evaluation configuration, tooling code, and calibration latency evidence before submitting the first case. The plan fixes the model aliases, pricing version, structured-output mode, 45-second provider deadline, two-minute polling deadline, and one-second interval, so `--corpus`, `--rubric`, `--timeout-seconds`, and `--poll-milliseconds` cannot override it.

The existing v1 plan is a historical freeze and still names the former demo-client runner path. It therefore fails closed after the tooling extraction and must not be rewritten. A future authorized, non-historical experiment uses the manifest for the corpus, rubric, runner limits and target scope; CLI overrides of those values are rejected:

```powershell
dotnet run --project src/Hive.Evaluation.Tooling -- evaluate --run-id <new-authorized-run-id> --base-url http://localhost:8080 --manifest $manifest
```

The tooling submits cases sequentially through the generic directive API and writes temporary outputs under `artifacts/evaluation/` (for example, `artifacts/evaluation/<run-id>.json`). The complete `artifacts/` tree is ignored and remains disposable. Manifest-driven datasets repeat the manifest version/id/SHA-256 and effective-configuration SHA-256. Their `effective_configuration_validation` is `validated` only when every observed inference/verifier call carries the declared limits version, end-to-end budget and per-call ceiling; inference also uses the declared provider, model, output mode and output ceiling, starts at the declared per-call timeout and never expands across continuations; outcome resolutions use the declared mode; and verifier requests do not exceed their declared ceiling. Drift is emitted as closed failure codes and the command exits unsuccessfully. This validates observable runtime values; it does not authorize another calibration or any holdout.

The datasets already under `evidence/evaluation/` belong to historical plans and remain unchanged. For a new manifest-driven experiment, keep the raw dataset under ignored `artifacts/`, commit only its reviewed summary report under `evidence/evaluation/<experiment-id>/`, and publish the exact raw bytes to protected storage outside the repository:

```powershell
$dataset='artifacts/evaluation/<run-id>.json'
$summary='evidence/evaluation/<experiment-id>/<run-id>-summary.md'
$artifactStore='X:\hive-evaluation-artifacts'
dotnet run --project src/Hive.Evaluation.Tooling -- artifact publish --dataset $dataset --manifest $manifest --summary-report $summary --artifact-store $artifactStore --location-base 'https://artifacts.example/hive/evaluation/' --published-at '2026-07-30T12:00:00Z' --retain-until '2027-07-30T12:00:00Z'
```

The command accepts only a manifest-bound v1 dataset, verifies its manifest/effective-configuration hashes, and copies it without transformation to `sha256/<prefix>/<sha256>.json`. The filesystem storage root must be outside the repository. The logical base URI must be absolute and contain no credentials, query, or fragment; credentials and backend access configuration stay outside HIVE. Publication updates the single tracked `evidence/evaluation/artifact-index.v1.json` atomically with byte hash, size, case count, configuration-validation status, explicit UTC publication/retention instants, and hashed references to the manifest and summary. The manifest already owns hashes for corpus, rubric, organization snapshot, prompt, Compose, and reproduction code. An exact retry is idempotent; scope collisions, source/reference drift, a non-disposable repository-local dataset, or non-increasing retention fail closed. The publisher never overwrites or deletes a content-addressed object. The storage operator must enforce access control, encryption, availability, and the indexed retention deadline.

The tooling polls only the public audit/export route for at most two minutes per case, projects the optional canonical result under the tracked v1 rubric, and emits dimension rows ordered by `dimension_id` with `status`, admitted `labels`, and `score`, plus the unrounded corpus macro-mean. `run_analysis` reports terminal, explicit-cost, and scoreable-projection coverage; macro quality by dimension; the `report`/`escalation` decision matrix; escalation recall; available and unavailable cost; and uncensored latency percentiles. A historical plan also projects its declared censored-deadline calibration; a manifest-driven run leaves `deadline_calibration` as `null` because the manifest does not declare that historical freeze. Status is `ready` only when every case has an auditable terminal, explicit cost state, valid projection, and no effective-configuration drift; incomplete or drifted manifest runs are `not-ready` and make the command exit unsuccessfully. Quality thresholds are not applied here; they belong to the later go/no-go task.

The existing `bug-triage-corpus.v1.json` is calibration-only because its cases and earlier outputs have already been observed. Prompt, model, label, or deadline tuning may use only that partition. After a reviewed `ready` calibration dataset is recorded by path and SHA-256 in the plan's `calibration_readiness`, the runner unlocks the separately reviewed 30-case holdout:

```powershell
dotnet run --project src/Hive.Evaluation.Tooling -- evaluate --run-id holdout-v1 --base-url http://localhost:8080 --plan $plan --partition holdout
```

The holdout command fails before network access when readiness evidence is missing/drifted, any frozen input changed, or an exact case id/normalized context overlaps calibration. Never inspect holdout results and then tune prompt, model, labels, or timeout against them; a changed frozen input requires a new freeze and a new unseen holdout. T05 reports only holdout evidence and marks incomplete coverage explicitly; only a `gate-eligible` complete holdout is valid input to the T06 decision. Reusing a `run-id` reuses deterministic message/thread/directive identities for safe retry and does not create an independent measurement. A missing, invalid, or version-incompatible projection remains a structured scoring failure. Output contains correlations, canonical predicted labels, aggregate matrix/recall, scores, decision, outcome, provider/model, tokens, cost, and latency, but never corpus context, per-case human baseline, model text, credentials, or database configuration. Review new populated result datasets before publishing them through the artifact command; do not rewrite historical datasets already committed as evaluation evidence. Generic `--corpus`/`--rubric` runs without `--plan` remain available for non-gate fixture development, including the follow-up example.

For the calibration-only GPT-5.6 Luna recovery experiment, layer the dedicated override last. It bind-mounts an experimental copy of `organization.yaml` that changes only the `bug-triage` position model, sets the gateway default to the same model, and adds the official USD 1.00 input / USD 6.00 output text-token rates per one million tokens ([official GPT-5.6 Luna model page](https://developers.openai.com/api/docs/models/gpt-5.6-luna), accessed 2026-07-15). It does not alter the tracked organization source, the historical evaluation plan, or either burned holdout:

```powershell
docker compose -f docker-compose.yml -f docker-compose.demo.yml -f docker-compose.evaluation.yml -f docker-compose.postgres-external.yml -f docker-compose.evaluation.gpt-5.6-luna.yml up --build -d
$corpus='config/organizations/acme-delivery/examples/evaluation/bug-triage-corpus.v1.json'
$rubric='config/organizations/acme-delivery/examples/evaluation/bug-triage-rubric.v1.json'
dotnet run --project src/Hive.Evaluation.Tooling -- evaluate --run-id luna-calibration-001 --base-url http://localhost:8080 --corpus $corpus --rubric $rubric --timeout-seconds 120 --poll-milliseconds 1000
dotnet run --project src/Hive.Evaluation.Tooling -- evaluate --run-id luna-calibration-002 --base-url http://localhost:8080 --corpus $corpus --rubric $rubric --timeout-seconds 120 --poll-milliseconds 1000
dotnet run --project src/Hive.Evaluation.Tooling -- evaluate --run-id luna-calibration-003 --base-url http://localhost:8080 --corpus $corpus --rubric $rubric --timeout-seconds 120 --poll-milliseconds 1000
```

These are exploratory generic runs, not gate-bearing plan partitions. Keep the prompt, corpus, rubric, 45-second provider timeout, output schema, and code unchanged between runs. The public `gpt-5.6-luna` alias is acceptable for this calibration, but a later freeze requires an immutable snapshot or a documented provider-resolved version. After the experiment, recreate the normal evaluation stack without `docker-compose.evaluation.gpt-5.6-luna.yml` to restore the tracked organization model in the PostgreSQL registry.

For the T14 prompt-recovery calibration, use the separate override below. It preserves the T13 profile and datasets, selects the versioned `triage-v2` identity plus the generic HIVE intent boundary compiled into the current image, and keeps the same Luna pricing, corpus, rubric, output schema and timeouts. Confirm that `gpt-5.6-luna` is allowed by the same OpenAI project/key before starting; do not inspect or tune between the three runs:

```powershell
docker compose -f docker-compose.yml -f docker-compose.demo.yml -f docker-compose.evaluation.yml -f docker-compose.postgres-external.yml -f docker-compose.evaluation.gpt-5.6-luna-prompt-recovery.yml up --build -d
$corpus='config/organizations/acme-delivery/examples/evaluation/bug-triage-corpus.v1.json'
$rubric='config/organizations/acme-delivery/examples/evaluation/bug-triage-rubric.v1.json'
$evidence='evidence/evaluation/bug-triage-luna-prompt-recovery-v1'
dotnet run --project src/Hive.Evaluation.Tooling -- evaluate --run-id luna-prompt-calibration-001 --base-url http://localhost:8080 --corpus $corpus --rubric $rubric --output "$evidence/luna-prompt-calibration-001.json" --timeout-seconds 120 --poll-milliseconds 1000
dotnet run --project src/Hive.Evaluation.Tooling -- evaluate --run-id luna-prompt-calibration-002 --base-url http://localhost:8080 --corpus $corpus --rubric $rubric --output "$evidence/luna-prompt-calibration-002.json" --timeout-seconds 120 --poll-milliseconds 1000
dotnet run --project src/Hive.Evaluation.Tooling -- evaluate --run-id luna-prompt-calibration-003 --base-url http://localhost:8080 --corpus $corpus --rubric $rubric --output "$evidence/luna-prompt-calibration-003.json" --timeout-seconds 120 --poll-milliseconds 1000
```

Each run must independently have 30/30 terminal, explicit-cost-state and scoreable coverage and meet the T14 thresholds recorded in the bible. Any authorization, invalid-output or projection failure rejects that run. This remains calibration-only: do not use `--plan`, do not execute a holdout, and restore the normal evaluation stack without this override when the experiment is complete.

For the T15 `gpt-5.4-mini` model-only comparison, enable both the alias and its official `gpt-5.4-mini-2026-03-17` snapshot in the OpenAI project's **Model usage** allowlist. A pre-corpus smoke request to the alias showed that the provider returns this snapshot id, so the profile fixes it directly for reproducibility. It keeps `triage-v2` and every T14 runtime/evaluation input unchanged, changes only the effective bug-triage model, and records the official standard USD 0.75 input / USD 4.50 output text-token rates per one million tokens ([official GPT-5.4 mini model page](https://developers.openai.com/api/docs/models/gpt-5.4-mini), accessed 2026-07-15). Run the same-key snapshot smoke test before starting the corpus:

```powershell
$keyLine=Get-Content .env | Where-Object { $_ -match '^OPENAI_API_KEY=' } | Select-Object -First 1
if ($null -eq $keyLine) { throw 'OPENAI_API_KEY is missing from .env.' }
$env:HIVE_AI_GATEWAY_REAL_TEST_API_KEY=$keyLine.Substring($keyLine.IndexOf('=') + 1).Trim().Trim('"')
$env:HIVE_AI_GATEWAY_REAL_TEST_MODEL_ID='gpt-5.4-mini-2026-03-17'
try {
  dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~AiGatewayIntegrationTests.Optional_real_provider_smoke_test_runs_only_with_local_secret_and_model" -v minimal
} finally {
  Remove-Item Env:HIVE_AI_GATEWAY_REAL_TEST_API_KEY -ErrorAction SilentlyContinue
  Remove-Item Env:HIVE_AI_GATEWAY_REAL_TEST_MODEL_ID -ErrorAction SilentlyContinue
}
```

Only after the smoke test passes, start and verify the isolated evaluation profile:

```powershell
docker compose -f docker-compose.yml -f docker-compose.demo.yml -f docker-compose.evaluation.yml -f docker-compose.postgres-external.yml -f docker-compose.evaluation.gpt-5.4-mini-prompt-recovery.yml up --build -d
Invoke-WebRequest -UseBasicParsing http://localhost:8080/health/ready
Invoke-RestMethod http://localhost:8080/internal/organizations/acme-delivery/positions/bug-triage/configuration | ConvertTo-Json -Depth 8
```

The configuration response must show `identityPromptRef` equal to `triage-v2` and `model` equal to `gpt-5.4-mini-2026-03-17`. Then execute all three runs in one block; do not inspect or tune between them:

```powershell
$corpus='config/organizations/acme-delivery/examples/evaluation/bug-triage-corpus.v1.json'
$rubric='config/organizations/acme-delivery/examples/evaluation/bug-triage-rubric.v1.json'
$evidence='evidence/evaluation/bug-triage-gpt-5.4-mini-recovery-v1'
$runIds=@('gpt-5-4-mini-calibration-001','gpt-5-4-mini-calibration-002','gpt-5-4-mini-calibration-003')
$statuses=@()
foreach ($runId in $runIds) {
  dotnet run --project src/Hive.Evaluation.Tooling --no-restore -- evaluate --run-id $runId --base-url http://localhost:8080 --corpus $corpus --rubric $rubric --output "$evidence/$runId.json" --timeout-seconds 120 --poll-milliseconds 1000
  $statuses += "$runId=$LASTEXITCODE"
}
$statuses
```

The three runs contain 90 cases in total. Preserve all three datasets even when a runner exit code is `1`: that code means at least one case was not both successful and scoreable, not that the dataset was lost. Do not run a holdout. Restore the normal profile after the datasets have been written:

```powershell
docker compose -f docker-compose.yml -f docker-compose.demo.yml -f docker-compose.evaluation.yml -f docker-compose.postgres-external.yml up -d
```

For the one-run T16 `gpt-5.6-terra` exception, enable `gpt-5.6-terra` in the OpenAI project's **Model usage** allowlist. The isolated profile keeps every T15 input unchanged except the model and the official standard USD 2.50 input / USD 15.00 output text-token rates per one million tokens ([official GPT-5.6 Terra model page](https://developers.openai.com/api/docs/models/gpt-5.6-terra), accessed 2026-07-15). Run the same-key smoke test before consuming the corpus:

```powershell
$keyLine=Get-Content .env | Where-Object { $_ -match '^OPENAI_API_KEY=' } | Select-Object -First 1
if ($null -eq $keyLine) { throw 'OPENAI_API_KEY is missing from .env.' }
$env:HIVE_AI_GATEWAY_REAL_TEST_API_KEY=$keyLine.Substring($keyLine.IndexOf('=') + 1).Trim().Trim('"')
$env:HIVE_AI_GATEWAY_REAL_TEST_MODEL_ID='gpt-5.6-terra'
try {
  dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~AiGatewayIntegrationTests.Optional_real_provider_smoke_test_runs_only_with_local_secret_and_model" -v minimal
} finally {
  Remove-Item Env:HIVE_AI_GATEWAY_REAL_TEST_API_KEY -ErrorAction SilentlyContinue
  Remove-Item Env:HIVE_AI_GATEWAY_REAL_TEST_MODEL_ID -ErrorAction SilentlyContinue
}
```

Only after the smoke succeeds, start and verify the T16 profile, then execute its single 30-case calibration:

```powershell
docker compose -f docker-compose.yml -f docker-compose.demo.yml -f docker-compose.evaluation.yml -f docker-compose.postgres-external.yml -f docker-compose.evaluation.gpt-5.6-terra.yml up --build -d
Invoke-WebRequest -UseBasicParsing http://localhost:8080/health/ready
Invoke-RestMethod http://localhost:8080/internal/organizations/acme-delivery/positions/bug-triage/configuration | ConvertTo-Json -Depth 8
$corpus='config/organizations/acme-delivery/examples/evaluation/bug-triage-corpus.v1.json'
$rubric='config/organizations/acme-delivery/examples/evaluation/bug-triage-rubric.v1.json'
$output='evidence/evaluation/bug-triage-terra-v1/terra-calibration-001.json'
dotnet run --project src/Hive.Evaluation.Tooling --no-restore -- evaluate --run-id terra-calibration-001 --base-url http://localhost:8080 --corpus $corpus --rubric $rubric --output $output --timeout-seconds 120 --poll-milliseconds 1000
```

The registry response must show `identityPromptRef=triage-v2`, `model=gpt-5.6-terra`, and `maxTokens=4096`. Preserve the dataset even when the runner exits with code `1`; do not run a holdout. Restore the normal profile afterwards with the same command shown above without the Terra override.

For the corrected T17f hybrid outcome calibration, layer the isolated enforcement profile last. It restores the provider-resolved `gpt-5-mini-2025-08-07` snapshot while preserving `triage-v2`, the v1 corpus/baseline/rubric, JSON Schema output, 4096-token main-output limit, bounded 2048-token verifier-output limit, 45-second provider timeout, 120-second polling timeout, one-second polling interval, default provider pricing, and effective four-iteration position limit. The verifier smoke exercises the limited semantic-completion path (`NotDeclared` plus grounded `DirectiveInput`) and passes only when the closed classification is `Report.Done`; `Undetermined` is a failed preflight. The profile changes only the organization fixture needed by the experiment and `Hive:Outcomes:Mode=enforcement`. The original `hybrid-outcome-calibration-001` through `003` IDs are burned by the rejected v1 measurement. Before consuming the corrected v2 corpus runs, execute both the general provider smoke and the closed verifier-contract smoke against the fixed snapshot:

```powershell
$keyLine=Get-Content .env | Where-Object { $_ -match '^OPENAI_API_KEY=' } | Select-Object -First 1
if ($null -eq $keyLine) { throw 'OPENAI_API_KEY is missing from .env.' }
$env:HIVE_AI_GATEWAY_REAL_TEST_API_KEY=$keyLine.Substring($keyLine.IndexOf('=') + 1).Trim().Trim('"')
$env:HIVE_AI_GATEWAY_REAL_TEST_MODEL_ID='gpt-5-mini-2025-08-07'
try {
  dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~AiGatewayIntegrationTests.Optional_real_provider_smoke_test_runs_only_with_local_secret_and_model" -v minimal
  dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~AiGatewayIntegrationTests.Optional_real_outcome_verifier_smoke_confirms_semantic_done" -v minimal
} finally {
  Remove-Item Env:HIVE_AI_GATEWAY_REAL_TEST_API_KEY -ErrorAction SilentlyContinue
  Remove-Item Env:HIVE_AI_GATEWAY_REAL_TEST_MODEL_ID -ErrorAction SilentlyContinue
}
```

Only after the smoke succeeds, start and verify the isolated profile, then execute all three new runs in one block without inspecting or tuning between them:

```powershell
$compose=@('-f','docker-compose.yml','-f','docker-compose.demo.yml','-f','docker-compose.evaluation.yml','-f','docker-compose.postgres-external.yml','-f','docker-compose.evaluation.outcome-resolution.yml')
docker compose @compose up --build -d
Invoke-WebRequest -UseBasicParsing http://localhost:8080/health/ready
Invoke-RestMethod http://localhost:8080/internal/organizations/acme-delivery/positions/bug-triage/configuration | ConvertTo-Json -Depth 8
$corpus='config/organizations/acme-delivery/examples/evaluation/bug-triage-corpus.v1.json'
$rubric='config/organizations/acme-delivery/examples/evaluation/bug-triage-rubric.v1.json'
$evidence='evidence/evaluation/bug-triage-hybrid-outcome-v2'
$runIds=@('hybrid-outcome-corrected-calibration-001','hybrid-outcome-corrected-calibration-002','hybrid-outcome-corrected-calibration-003')
$statuses=@()
foreach ($runId in $runIds) {
  dotnet run --project src/Hive.Evaluation.Tooling --no-restore -- evaluate --run-id $runId --base-url http://localhost:8080 --corpus $corpus --rubric $rubric --output "$evidence/$runId.json" --timeout-seconds 120 --poll-milliseconds 1000
  $statuses += "$runId=$LASTEXITCODE"
}
$statuses
```

The configuration response must show `identityPromptRef=triage-v2`, `model=gpt-5-mini-2025-08-07`, and `maxTokens=4096`; the resolved Compose environment must show `Hive:Outcomes:Mode=enforcement`. Each dataset must contain 30 outcome-resolution projections and is preserved even when the runner exits with code `1`. T17f is calibration-only: do not use `--plan`, inspect or tune between runs, execute a holdout, or treat successful calibration as reopening F1a. Restore the normal profile afterwards with the standard evaluation stack command above.

The post-T17f timeout calibration uses a separate profile so the historical 45-second T17f configuration and frozen evaluation plan remain unchanged. The three corrected T17f runs timed out in 10 of 90 cases, while successful main-model calls reached 44.8 seconds and per-run main p95 reached 44.6 seconds. `docker-compose.evaluation.outcome-resolution-timeout60.yml` therefore mounts `config/experiments/hybrid-outcome-resolution-timeout60-v1/organization.yaml`, which changes only the `bug-triage` provider timeout from `PT45S` to `PT60S` relative to the historical hybrid profile. The normal organization remains unchanged.

`US-F0-13-T18a` only prepared and validated this configuration. Do not modify `bug-triage-evaluation-plan.v1.json`; that plan is historical and remains frozen at 45 seconds. The authorized `T18c` block was interrupted after intermediate observation exposed systemic verifier and response-budget defects. `hybrid-outcome-timeout60-calibration-001`, `002`, and `003` are burned and the single completed dataset is exploratory only. The historical timeout profile remains unchanged at 4096 tokens.

`US-F0-13-T18e/T18f` adds minimized diagnostics for provider responses and operational failures. `GatewayCalled` and `GatewayCostRecorded` preserve the resolved provider/model and response id, finish reason, token usage, calculable cost/pricing, validated numeric `providerStatusCode`, `requestTimeoutMilliseconds`, and `maxOutputTokens`; BUG-005 adds `executionLimitsVersion`, `executionBudgetMilliseconds`, and `perCallTimeoutMilliseconds`. `gateway_calls` projects the same safe values as `finish_reason`, `provider_status_code`, `request_timeout_ms`, `max_output_tokens`, `execution_limits_version`, `execution_budget_ms`, and `per_call_timeout_ms`. They never persist the provider error body/message, raw output, rejected message content, reasoning, or arbitrary provider properties. An empty response with `finishReason=Length` and output usage at the configured ceiling identifies output-budget exhaustion without exposing model reasoning. A latency beyond the intended timeout must be diagnosed from the persisted effective timeout before changing provider or runner deadlines.

`US-F0-13-T18g` tunes only the user-controlled `triage-v2` identity prompt; it does not change the generic HIVE protocol, resolver, corpus, rubric, or historical evidence. The next measurement is `T18h` and still requires explicit authorization. First run both real smokes from the preceding section, then start the new reliability profile:

```powershell
$compose=@('-f','docker-compose.yml','-f','docker-compose.demo.yml','-f','docker-compose.evaluation.yml','-f','docker-compose.postgres-external.yml','-f','docker-compose.evaluation.outcome-resolution-reliability.yml')
docker compose @compose up --build -d
Invoke-WebRequest -UseBasicParsing http://localhost:8080/health/ready
Invoke-RestMethod http://localhost:8080/internal/organizations/acme-delivery/positions/bug-triage/configuration | ConvertTo-Json -Depth 8
```

The configuration response for the BUG-005 revision must show `identityPromptRef=triage-v2`, `model=gpt-5-mini-2025-08-07`, `maxTokens=8192`, `limitsVersion=1`, `timeout=PT60S`, and `executionTimeout=PT90S`. The generated Compose labels must show the same 60-second per-call ceiling and 90-second end-to-end budget, while the external runner remains at 120 seconds with a one-second poll interval. These changes do not authorize a future measurement: it still requires new run IDs, a new evidence directory and separate authorization, and no historical or new holdout may run without a freeze decision. The verifier smoke must return exactly `Report.Done`; `Undetermined` is a failed preflight and the corpus must not start.

Revision 2.29 completed this block and rejected the corrected variant. The three `hybrid-outcome-corrected-calibration-*` IDs are now burned and the commands above are retained only as the operational record of the completed measurement; do not rerun them. No holdout is authorized. Any future experiment requires a new bible decision, code/configuration identity, evidence directory, and run IDs.

`US-F0-13-T18i/T18j` corrects the verifier input, observability and deadline contract without running a corpus. The verifier now receives a provider-neutral projection of the materialized proposed organizational message: its closed kind plus at most 16 canonical semantic fields and 16 KiB UTF-8 in total. Evaluation envelopes, provider output, prompts, tools, history, memory, attachments, reasoning and rejected values are excluded. An invalid or oversized artifact is not silently truncated; verification becomes unavailable and the resolver keeps its existing fail-safe behavior.

The outcome audit now carries three separate minimized values — `verifierStatus` (`Classified`, `Unavailable`, `TimedOut` or `InvalidOutput`), `verifierClassification` when the status is `Classified`, and `semanticCompletionCandidate` — projected to the read model/dataset as `verifier_status`, `verifier_classification` and `semantic_completion_candidate`. Historical rows without these fields remain readable. `Undetermined` is counted only from the exact closed verifier classification, not inferred from the final fail-safe outcome. The artifact and verifier output are never persisted.

The normal verifier timeout remains 15 seconds. The isolated `docker-compose.evaluation.outcome-resolution-verifier30.yml` layer used by T18k changes only the deployment-level timeout to 30 seconds, keeps enforcement enabled and reuses the frozen T18h reliability organization snapshot. Inspect its resolved configuration without starting it:

```powershell
$compose=@('-f','docker-compose.yml','-f','docker-compose.demo.yml','-f','docker-compose.evaluation.yml','-f','docker-compose.postgres-external.yml','-f','docker-compose.evaluation.outcome-resolution-verifier30.yml')
docker compose @compose config
```

`T18k` was explicitly authorized and completed with fresh run ids in `evidence/evaluation/bug-triage-hybrid-outcome-verifier30-v1/`. Both real smokes passed and the three 30-case calibration datasets are terminal and scoreable, but the candidate failed the frozen decision, escalation-recall, missing-information and macro-quality gates and is rejected for freeze. The `hybrid-outcome-verifier30-calibration-001`, `002`, and `003` ids are burned; do not rerun them. No holdout was executed or authorized. The normal stack was restored afterwards without the experimental `HIVE__OUTCOMES__*` override. See `hybrid-outcome-verifier30-calibration-report.v1.md` in that evidence directory for the hashes, measurements and decision.

`US-F0-13-T18l` adds diagnosis only; it does not change resolution behavior or an organization-controlled identity prompt. Outcome-resolution audit rows can now include `semanticCompletionIneligibilityReasonCount` plus ordered `semanticCompletionIneligibilityReason.{n}` values from the closed system vocabulary, and `deadlineRemainingMilliseconds` when a directive or processing deadline exists. The audit never includes the evidence source/reference that caused a failure. The evaluation dataset projects these as `semantic_completion_ineligibility_reasons` and `deadline_remaining_ms`, and preserves every per-iteration resolution in `outcome_resolution_steps`; the existing `outcome_resolution` field remains the final step. Missing fields in historical rows remain valid. These diagnostics are for offline analysis and do not authorize a calibration or holdout. The completed diagnosis is recorded in `evidence/evaluation/bug-triage-hybrid-outcome-verifier30-v1/t18l-diagnostic-report.v1.md`.

Generate the versioned T05 report from the holdout dataset and the tracked reporting profile:

```powershell
dotnet run --project src/Hive.Evaluation.Tooling -- report
```

The defaults read `evidence/evaluation/bug-triage-holdout-v1/holdout-v1.json` and `config/organizations/acme-delivery/examples/evaluation/bug-triage-report-profile.v1.json`, then write `evidence/evaluation/bug-triage-holdout-v1/bug-triage-unit-economics-quality-report.v1.md`. Use `--dataset`, `--profile`, and `--output` to select other versioned inputs/outputs. The profile owns the explicit workload assumption and at least two provider/model price scenarios. The report includes hashes of both inputs, coverage and quality by configured dimension, decision matrix and positive-label rate/recall, measured cost with unavailable observations kept visible, position/day projection, token-price model sensitivity, and latency percentiles. Repricing holds observed input/output token usage constant and is not a quality or latency claim for an alternative model; the report never defines T06 thresholds or a go/no-go outcome.

Stop the demo with the same Compose file set used to start it:

```powershell
docker compose -f docker-compose.yml -f docker-compose.demo.yml down
docker compose -f docker-compose.yml -f docker-compose.demo.yml -f docker-compose.evaluation.yml -f docker-compose.postgres-external.yml down
docker compose -f docker-compose.yml -f docker-compose.cluster.yml -f docker-compose.roles.yml -f docker-compose.demo.cluster.yml down
```

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

## AI gateway

The base `IAiGateway` provider remains unavailable until a provider is selected. The demo Compose overrides select the real provider by default; local tests and deterministic integration flows continue to select the stub explicitly with:

```text
HIVE__AIGATEWAY__PROVIDER=stub
```

Stub responses are configured under `Hive:AiGateway:Stub`:

| Setting | Default | Purpose |
| --- | --- | --- |
| `Hive:AiGateway:Stub:ProviderId` | `stub` | Provider id reported in `AiProviderMetadata`. |
| `Hive:AiGateway:Stub:ModelId` | `deterministic` | Model id reported in `AiProviderMetadata`. |
| `Hive:AiGateway:Stub:Scenario` | unset | Optional deterministic vertical-slice scenario. When set, it overrides `Outcome`/`Text` and returns the canned scenario result. Supported values: `bug-triage-report`, `bug-triage-missing-information`, `bug-triage-external-decision-blocked`, `provider-controlled-failure`. |
| `Hive:AiGateway:Stub:Outcome` | `success` | One of `success`, `error`, `timeout`, or `tool-call`. |
| `Hive:AiGateway:Stub:Text` | `Stub AI response.` | Text returned for `success` and optional text returned with `tool-call`. Set to empty/null only when a tool call is present. |
| `Hive:AiGateway:Stub:FinishReason` | `stop` | Finish reason for `success`, using the AI gateway wire values such as `stop`, `length`, or `content-filtered`. `tool-call` always returns `tool-calls`. |
| `Hive:AiGateway:Stub:Error:Code` | `provider-rejected` for `error`, fixed `timeout` for `timeout` | Error code wire value used by the `error` outcome. |
| `Hive:AiGateway:Stub:Error:Message` | outcome-specific | Sanitized error message. |
| `Hive:AiGateway:Stub:Error:IsRetryable` | `false` for `error`, `true` for `timeout` | Retryability on the structured error. |
| `Hive:AiGateway:Stub:Usage:InputTokens` / `OutputTokens` / `TotalTokens` / `IsEstimated` | unset | Optional token usage returned with successful outcomes. |
| `Hive:AiGateway:Stub:Cost:Amount` / `Currency` / `IsEstimated` | unset | Optional cost metadata returned with successful outcomes. |
| `Hive:AiGateway:Stub:ToolCall:Id` / `Name` / `Arguments:*` | `stub-tool-call-1` / `stub.tool` / empty | Simulated tool call returned by the `tool-call` outcome. |

Example:

```text
HIVE__AIGATEWAY__PROVIDER=stub
HIVE__AIGATEWAY__STUB__OUTCOME=tool-call
HIVE__AIGATEWAY__STUB__TEXT=
HIVE__AIGATEWAY__STUB__TOOLCALL__ID=call-1
HIVE__AIGATEWAY__STUB__TOOLCALL__NAME=ticket.lookup
HIVE__AIGATEWAY__STUB__TOOLCALL__ARGUMENTS__ticket=HIVE-123
HIVE__AIGATEWAY__STUB__USAGE__INPUTTOKENS=12
HIVE__AIGATEWAY__STUB__USAGE__OUTPUTTOKENS=8
HIVE__AIGATEWAY__STUB__USAGE__TOTALTOKENS=20
HIVE__AIGATEWAY__STUB__COST__AMOUNT=0.03
HIVE__AIGATEWAY__STUB__COST__CURRENCY=EUR
HIVE__AIGATEWAY__STUB__COST__ISESTIMATED=true
```

F0 bug-triage vertical-slice scenarios can be selected by name:

```text
HIVE__AIGATEWAY__PROVIDER=stub
HIVE__AIGATEWAY__STUB__SCENARIO=bug-triage-report
```

### Real provider configuration

The real provider reads its secure configuration from `Hive:AiGateway:Real` and is activated only when `Hive:AiGateway:Provider` is set to `real`. The demo Compose profile does this by default; the default test suite does not enable it, does not require credentials, and does not open external network connections. The first concrete provider supported by US-F0-07-T05c is OpenAI through `ProviderId=openai`; unsupported real provider ids fail startup as `configuration-invalid`. The `ApiKey` is a secret: keep it out of `appsettings.json` and supply it through environment variables, the ignored local `.env` file used by Compose, or user-secrets.

| Setting | Required | Purpose |
| --- | --- | --- |
| `Hive:AiGateway:Real:ProviderId` | yes | Default provider id, applied when the position config omits it. `openai` is the only concrete provider supported by T05c. Missing/empty or unsupported values fail with `configuration-invalid`. |
| `Hive:AiGateway:Real:ModelId` | yes | Default model id, applied when the position config omits it. Missing/empty fails with `configuration-invalid`. |
| `Hive:AiGateway:Real:ApiKey` | yes | Secret credential. Infrastructure-only; never logged or exposed to the domain. Missing/empty fails with `credentials-missing`. |
| `Hive:AiGateway:Real:Endpoint` | no | Absolute endpoint URI. A non-absolute value fails with `configuration-invalid`. |
| `Hive:AiGateway:Real:Temperature` | no | Default sampling temperature (0–2). Out-of-range values fail with `configuration-invalid`. |
| `Hive:AiGateway:Real:MaxOutputTokens` | no | Default maximum output tokens (> 0). Non-positive values fail with `configuration-invalid`. |
| `Hive:AiGateway:Real:TimeoutSeconds` | no | Default request timeout in seconds (> 0). Non-positive values fail with `configuration-invalid`. |
| `Hive:AiGateway:Real:OutputCapabilities` | no | Collection of response-format capabilities declared for the configured provider/model: `json-schema`, `json-object`, and/or `text`. The default is `text` only. Declare structured modes only when the concrete provider/model supports them; invalid or empty collections fail with `configuration-invalid`. HIVE never infers capabilities from a model name. |
| `Hive:AiGateway:Real:Pricing:Version` | when `Pricing` exists | Canonical version of the operational price catalog. Change it whenever any price or model mapping changes. |
| `Hive:AiGateway:Real:Pricing:TokenUnit` | when `Pricing` exists | Positive token unit used by every catalog rate, normally `1000000` for prices per million tokens. |
| `Hive:AiGateway:Real:Pricing:Models:{n}:ProviderId` / `ModelId` | per price entry | Exact canonical provider/model key for the price. |
| `Hive:AiGateway:Real:Pricing:Models:{n}:Aliases` | no | Exact response-model aliases or snapshots that use the same price. Aliases cannot be ambiguous within a provider. |
| `Hive:AiGateway:Real:Pricing:Models:{n}:InputPrice` / `OutputPrice` | per price entry | Non-negative decimal input/output prices per `TokenUnit`. |
| `Hive:AiGateway:Real:Pricing:Models:{n}:Currency` | per price entry | Three-letter uppercase currency code shared by the entry's input/output prices. |

Example (secret supplied as an environment variable):

```text
HIVE__AIGATEWAY__PROVIDER=real
HIVE__AIGATEWAY__REAL__PROVIDERID=openai
HIVE__AIGATEWAY__REAL__MODELID=gpt-5-mini
HIVE__AIGATEWAY__REAL__ENDPOINT=https://api.example.com/v1
HIVE__AIGATEWAY__REAL__APIKEY=<secret>
HIVE__AIGATEWAY__REAL__TEMPERATURE=0.5
HIVE__AIGATEWAY__REAL__MAXOUTPUTTOKENS=256
HIVE__AIGATEWAY__REAL__TIMEOUTSECONDS=30
HIVE__AIGATEWAY__REAL__OUTPUTCAPABILITIES__0=json-schema
HIVE__AIGATEWAY__REAL__OUTPUTCAPABILITIES__1=json-object
HIVE__AIGATEWAY__REAL__OUTPUTCAPABILITIES__2=text
HIVE__AIGATEWAY__REAL__PRICING__VERSION=openai-2026-07-13
HIVE__AIGATEWAY__REAL__PRICING__TOKENUNIT=1000000
HIVE__AIGATEWAY__REAL__PRICING__MODELS__0__PROVIDERID=openai
HIVE__AIGATEWAY__REAL__PRICING__MODELS__0__MODELID=gpt-5-mini
HIVE__AIGATEWAY__REAL__PRICING__MODELS__0__ALIASES__0=gpt-5-mini-2025-08-07
HIVE__AIGATEWAY__REAL__PRICING__MODELS__0__INPUTPRICE=0.25
HIVE__AIGATEWAY__REAL__PRICING__MODELS__0__OUTPUTPRICE=2.00
HIVE__AIGATEWAY__REAL__PRICING__MODELS__0__CURRENCY=USD
```

When a position's runtime configuration specifies provider/model, parameters, timeout, or max iterations, those values override these defaults; absent position values fall back to the defaults above without inventing new values. For new configurations, `occupant.ai.limits_version: 1` requires both a positive `timeout` (maximum duration of each provider, connector-tool or verifier call) and a positive `execution_timeout` (immutable end-to-end budget for the directive). The effective deadline is the earlier of `execution_timeout` and the directive deadline, and every subordinate operation uses the minimum of its per-call ceiling and the positive time remaining. For compatibility, an omitted `limits_version` retains the legacy version 0 contract in which `timeout` supplies both values; use this only for unchanged historical configuration, not new experiments. `OutputCapabilities` must describe the effective provider/model served by the adapter, including any model selected by position configuration. For a constrained request, HIVE prefers `json-schema`; it uses `json-object` or `text` only when the request explicitly allows that fallback, and rejects the request before network access when no acceptable mode remains. Local HIVE contract validation still runs for every negotiated mode.

### Checkpointable directive execution

Directive execution is `single-shot` unless both the directive and its destination position opt in to the version 1 checkpointable contract. The position capability is owned by the reviewed organization YAML under `positions[].occupant.ai`; it has no environment-variable override:

```yaml
occupant:
  type: ai-agent
  ai:
    limits_version: 1
    timeout: PT60S
    execution_timeout: PT90S
    directive_execution_policy:
      contract_version: 1
      maximum_mode: checkpointable
      checkpoint_lead_time: PT5S
```

`checkpoint_lead_time` is required and positive when `maximum_mode` is `checkpointable`, and must be less than the effective end-to-end execution budget. Use `maximum_mode: single-shot` without a lead time to prohibit progress checkpoints explicitly. An absent policy also remains single-shot.

The caller opts in per directive through the existing submission payload:

```jsonc
"executionPolicy": {
  "contractVersion": 1,
  "mode": "checkpointable"
}
```

Both sides must use contract version 1 and the directive cannot exceed the position capability. Missing or incompatible values fail closed to effective `single-shot`; only an effective checkpointable execution exposes `Report.Progress` to the model. A progress report persists one grounded checkpoint before external effects and does not schedule a retry, timer, or provider call. Later work resumes only when a separately submitted directive has the exact thread and directive/task lineage required by the checkpoint contract.

`Pricing` is optional, but a configured catalog is validated as a complete unit when the real provider starts. Partial entries, non-positive units, negative rates, invalid currencies, and duplicate canonical/alias keys fail with `configuration-invalid`. Provider-declared cost takes precedence. Otherwise HIVE estimates cost only when both input and output usage exist and the effective response provider/model has an exact canonical or alias match. Missing usage or price is emitted as `cost-unavailable`; HIVE never substitutes zero. The audit cost event and evaluation dataset retain the cost status, catalog version, token unit, input/output rates, currency, and estimation flag.

The demo catalog version `openai-2026-07-13` records the standard GPT-5 mini text-token rates observed on 2026-07-13: USD 0.25 input and USD 2.00 output per one million tokens, including the published `gpt-5-mini-2025-08-07` snapshot alias. Verify the [official GPT-5 mini model pricing](https://developers.openai.com/api/docs/models/gpt-5-mini) and update the catalog version and rates before relying on it after a provider pricing change. If `OPENAI_MODEL_ID` selects another model, add a matching price entry or expect `cost-unavailable`.

### Optional real-provider integration smoke test

The default test suite does not call external AI providers. The optional real-provider smoke test runs only when both local test variables below are present:

| Variable | Required for smoke test | Purpose |
| --- | --- | --- |
| `HIVE_AI_GATEWAY_REAL_TEST_API_KEY` | yes | Secret API key used only by the optional test. Do not commit it. |
| `HIVE_AI_GATEWAY_REAL_TEST_MODEL_ID` | yes | Model id to request for the optional test. |
| `HIVE_AI_GATEWAY_REAL_TEST_ENDPOINT` | no | Optional absolute endpoint override. |

When either required variable is absent, the test exits before configuring or resolving the real provider. The key must not appear in assertions, logs, snapshots, or committed configuration.

## Outbound occupant email (SMTP)

SMTP is the first outbound occupant-channel adapter. It activates only when the node declares the `connectors` role and `Hive:OccupantChannels:Email:Smtp:Enabled` is `true`; otherwise the neutral channel remains unavailable and no SMTP connection is opened. A connector node with SMTP enabled validates its complete configuration during startup. Nodes without the `connectors` role neither activate the adapter nor require its credentials.

Immediately before every send attempt, including retries, the adapter asks the `identity` subsystem to verify the exact organization, position, occupant, `UserId`, and opaque `OccupantChannelBindingId` and return the active normalized email endpoint. It does not cache, persist, or log that endpoint. A missing or revoked binding fails closed without opening SMTP; identity-subsystem unavailability is retryable. The active binding resolver and the signed correlation-token request factory must both be present for end-to-end delivery.

Each notification is text-only. It identifies the organization, position and thread, includes the already-rendered organizational message, states that the position inbox remains the source of truth, and adds plain-text reply instructions plus the opaque correlation token. Retries use a stable SMTP `Message-Id` derived from the organizational `MessageId`, but delivery remains at-least-once.

| Setting | Required | Default / purpose |
| --- | --- | --- |
| `Hive:OccupantChannels:Email:Smtp:Enabled` | no | `false`. Explicitly activates SMTP on a `connectors` node. |
| `Hive:OccupantChannels:Email:Smtp:Host` | when enabled | SMTP server hostname. |
| `Hive:OccupantChannels:Email:Smtp:Port` | when enabled | `587`; valid range 1–65535. |
| `Hive:OccupantChannels:Email:Smtp:Security` | when enabled | `start-tls` (strict STARTTLS). Other accepted values are `ssl-on-connect` and `none`; use `none` only for a trusted development relay. |
| `Hive:OccupantChannels:Email:Smtp:FromAddress` | when enabled | One normalized mailbox address without a display name. |
| `Hive:OccupantChannels:Email:Smtp:FromName` | no | `HIVE`; line breaks are rejected. |
| `Hive:OccupantChannels:Email:Smtp:ReplyToAddress` | no | Normalized inbound mailbox address; defaults to `FromAddress`. |
| `Hive:OccupantChannels:Email:Smtp:Username` / `Password` | no, but a pair | SMTP credentials. Supply both or neither (for a trusted relay). `Password` is a secret. |
| `Hive:OccupantChannels:Email:Smtp:SubjectPrefix` | no | `[HIVE]`; line breaks are rejected. |
| `Hive:OccupantChannels:Email:Smtp:MaxAttempts` | no | `3`; accepted range 1–10, including the initial attempt. Only retryable failures are retried. |
| `Hive:OccupantChannels:Email:Smtp:InitialBackoff` | no | `00:00:01`; positive initial retry delay. |
| `Hive:OccupantChannels:Email:Smtp:MaxBackoff` | no | `00:00:30`; positive cap for exponential backoff and not less than `InitialBackoff`. |
| `Hive:OccupantChannels:Email:Smtp:AttemptTimeout` | no | `00:00:30`; positive timeout for each connect/authenticate/send attempt. |

Example for a connector deployment:

```text
HIVE__NODE__ROLES__0=connectors
HIVE__OCCUPANTCHANNELS__EMAIL__SMTP__ENABLED=true
HIVE__OCCUPANTCHANNELS__EMAIL__SMTP__HOST=smtp.example.com
HIVE__OCCUPANTCHANNELS__EMAIL__SMTP__PORT=587
HIVE__OCCUPANTCHANNELS__EMAIL__SMTP__SECURITY=start-tls
HIVE__OCCUPANTCHANNELS__EMAIL__SMTP__FROMADDRESS=hive@example.com
HIVE__OCCUPANTCHANNELS__EMAIL__SMTP__REPLYTOADDRESS=hive-replies@example.com
HIVE__OCCUPANTCHANNELS__EMAIL__SMTP__USERNAME=<smtp-user>
HIVE__OCCUPANTCHANNELS__EMAIL__SMTP__PASSWORD=<secret>
```

Keep real SMTP credentials in the deployment platform's secret store, an ignored local `.env`, or .NET user-secrets. Never place them in `appsettings*.json`, `.env.example`, organization YAML, logs, or source control. Rotation applies to the next send after the process is restarted with the new environment-provided configuration; binding revocation is independent and is checked on every attempt.

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

The organization registry owns the `registry` schema and uses this same connection string. The common host bootstrap automatically applies its embedded, versioned SQL migrations and then imports every organization under `Hive:Organizations:RootPath` before role workloads start; concurrent cluster nodes serialize migration/import writers and unchanged YAML becomes a no-op. Migration `001_registry.sql` creates the organization header and the unit, position, occupant, authority, schedule, command-relation, and per-organization import-lock tables; `002_sparse_authority.sql` migrates authority rows to sparse `can_decide` + `overrides`. No additional credential is required beyond `ConnectionStrings:PostgreSql`; the configured database user must be allowed to create and modify the owned `registry` schema. A host with no configured connection string skips migration/import and remains not-ready under the existing readiness contract; a configured database that cannot connect/migrate or an absent/invalid organization tree fails host startup.

### PositionActor persistence (journal and snapshots)

The `PositionActor` journal and snapshot store use `Akka.Persistence.Sql` (Linq2Db, PostgreSQL provider — ADR-003, replacing the deprecated `Akka.Persistence.PostgreSql`) over the same `ConnectionStrings:PostgreSql` value, in a dedicated `persistence` schema isolated from the registry and the other subsystems that share the database (US-F0-06-T05a). The common bootstrap applies its embedded, versioned migration `001_persistence.sql` — serialized across nodes by an advisory lock and recorded in `persistence.schema_migrations` — before the `agents` workload starts the persistent entity. The migration owns and versions the dedicated `persistence` schema; the journal/snapshot/tag/metadata table DDL is owned by the plugin and created by auto-initialization inside that schema on first use, so the schema must exist first. The configured database user must be allowed to create and modify the owned `persistence` schema; no credential beyond `ConnectionStrings:PostgreSql` is required.

When the connection string is absent the persistence plugins are not wired into the actor system, the migration is skipped, and the node stays not-ready under the existing readiness contract; when it is present, a failed connection or migration aborts host startup, exactly like the registry. The schema name is a durable contract and must not change while journals exist. Binding the versionable serializers for commands, events, and snapshots (no default .NET serialization) is US-F0-06-T05b.

### Scheduler pulse delivery read model

The scheduler pulse delivery read model owns the `scheduler` schema and uses the same `ConnectionStrings:PostgreSql` value (US-F0-09-T07). The common bootstrap applies its embedded, versioned migration before role workloads start. Migration `001_pulse_delivery.sql` creates `scheduler.pulse_deliveries` for the current state keyed by the deterministic pulse idempotency key, `scheduler.pulse_delivery_history` for ordered transition history, and `scheduler.schema_migrations` for the migration ledger. The configured database user must be allowed to create and modify the owned `scheduler` schema; no additional credential is required.

When the connection string is absent the scheduler migration is skipped and the in-process scheduler delivery store is a no-op. When the connection string is present, migration failure aborts host startup like the registry and persistence migrations. The schema name and status values (`Registered`, `Fired`, `Delivered`, `Skipped`, `Failed`, `Redelivered`) are durable contracts for scheduler idempotency and future audit/read-model consumers.

## Public API organization authorization

Every read-only organization route under `/api/v1/organizations/{organizationId}` and the SignalR hub at `/api/v1/organization-updates` require a static bearer credential. The tracked `Hive:PublicApi:Authorization:Credentials` array is empty, so an operator must supply credentials outside the repository. Each credential maps one unique secret token to one or more allowed organization ids; the authorization seam resolves that list as the request principal's organization scope. A credential may additionally identify one F1 person and the positions that person occupies. A missing or unrecognized bearer token returns `401`. A valid credential used for an organization outside that scope returns the same `404 Organization not found` response as a missing organization before a REST read, and hub subscriptions fail with the same non-disclosing message.

Configure one credential for `acme-delivery` with standard hierarchical environment variables:

```powershell
$env:HIVE__PUBLICAPI__AUTHORIZATION__CREDENTIALS__0__TOKEN = "<high-entropy-secret>"
$env:HIVE__PUBLICAPI__AUTHORIZATION__CREDENTIALS__0__ORGANIZATIONIDS__0 = "acme-delivery"
```

```bash
export HIVE__PUBLICAPI__AUTHORIZATION__CREDENTIALS__0__TOKEN='<high-entropy-secret>'
export HIVE__PUBLICAPI__AUTHORIZATION__CREDENTIALS__0__ORGANIZATIONIDS__0='acme-delivery'
```

Add further organization ids to the same principal with `ORGANIZATIONIDS__1`, `ORGANIZATIONIDS__2`, and so on, or add a separate credential at `CREDENTIALS__1`. Send the selected secret to REST endpoints without placing it in a query string:

```http
Authorization: Bearer <high-entropy-secret>
```

The human inbox additionally requires a person binding on that credential. `PersonId` is a
stable opaque identifier for the F1 principal; every occupied-position entry is the exact
pair `(OrganizationId, PositionId)` and its organization must already be present in the
credential's `OrganizationIds`. For example:

```powershell
$env:HIVE__PUBLICAPI__AUTHORIZATION__CREDENTIALS__0__PERSONID = "person-alice"
$env:HIVE__PUBLICAPI__AUTHORIZATION__CREDENTIALS__0__POSITIONS__0__ORGANIZATIONID = "acme-delivery"
$env:HIVE__PUBLICAPI__AUTHORIZATION__CREDENTIALS__0__POSITIONS__0__POSITIONID = "delivery-lead"
```

```bash
export HIVE__PUBLICAPI__AUTHORIZATION__CREDENTIALS__0__PERSONID='person-alice'
export HIVE__PUBLICAPI__AUTHORIZATION__CREDENTIALS__0__POSITIONS__0__ORGANIZATIONID='acme-delivery'
export HIVE__PUBLICAPI__AUTHORIZATION__CREDENTIALS__0__POSITIONS__0__POSITIONID='delivery-lead'
```

Add further occupations with `POSITIONS__1`, `POSITIONS__2`, and so on. The aggregate inbox
contains only items assigned to those configured positions; a position route outside that set
and an item outside the effective set both return `404` without revealing whether the resource
exists. A credential without `PersonId` remains valid for organization-level read-only routes
but cannot read a human inbox. Do not configure substitutes as occupations: a delegation only
extends inbox scope when it exists as a persisted organizational fact, and F1 does not infer
delegation from configuration.

Configure a browser SignalR client with its standard `accessTokenFactory`. The negotiate request can use the bearer header; WebSocket and Server-Sent Events transports may carry the token as `access_token` in the query string because browser transport APIs cannot set that header. The API accepts this query parameter only under the hub path. Require TLS and redact `access_token` from reverse-proxy/request logs, because URLs can otherwise expose it operationally. After connecting or reconnecting, invoke the authorized organization subscription and refresh the authoritative REST snapshot as specified in the [T08 realtime contract](bible.html).

Store production tokens in the deployment platform's secret store, inject them into the process at runtime, and restart the API host when rotating environment-provided values. Never put real tokens in `appsettings*.json`, `.env.example`, organization YAML, logs, or source control. Health, diagnostics, OpenAPI, and private `/internal` routes retain their existing access contracts. During degraded realtime operation, clients poll `/api/v1/organizations/{organizationId}/position-states` at their configured interval, retain the last `ETag`, and send it as `If-None-Match`; an unchanged snapshot returns `304` with no body. There is no server-side polling interval setting.

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

### Outcome policy overlays

The outcome resolver uses a code-defined system policy (`outcome-policy-v1`) with a maximum of 8 iterations, 3 retries, verifier support enabled, and every closed objective risk trigger enabled. Organization files may only tighten that baseline. An optional `organization.outcome_policy` applies to every position; an optional `positions[].occupant.outcome_policy` tightens it for one position:

```yaml
organization:
  id: acme-delivery
  root_unit: raiz
  owner:
    type: human
    ref: owner@acme.pt
  outcome_policy:
    max_iterations: 6
    max_retries: 2
    verifier_enabled: false

positions:
  - id: bug-triage
    unit: engenharia
    reports_to: delivery-lead
    occupant:
      type: ai-agent
      outcome_policy:
        max_iterations: 4
        max_retries: 1
        verifier_enabled: false
```

`max_iterations` and `max_retries` are non-negative integers and may only stay equal to or decrease the inherited value. Zero is valid and causes the corresponding gate to escalate before another attempt. `verifier_enabled` may stay enabled or be changed to `false`; a position cannot re-enable it after the organization disabled it. The existing `occupant.ai.max_iterations` remains the operational AI-loop limit and also tightens the effective outcome-policy iteration maximum. Attempts to increase an inherited maximum or re-enable the verifier fail organization import. Omitted blocks inherit the effective parent policy.

The runtime policy snapshot identifies both the system contract and registry configuration version. Its SHA-256 fingerprint covers the registry fingerprint, canonical overlays, runtime AI iteration limit, and effective values. There are no environment-variable overrides for these fields; change them through the reviewed organization GitOps document.

The deployment-level rollout switch is separate from those policy overlays. `Hive:Outcomes:Mode` defaults to `shadow`; set `HIVE__OUTCOMES__MODE=enforcement` only for a deployment intended to apply the resolver to emitted messages. `Hive:Outcomes:VerifierTimeout` defaults to 15 seconds and must be positive; the runtime caps each verifier request by that value, the position per-call timeout and the remaining end-to-end/directive deadline. In both modes the audit row is idempotent by correlation and iteration and contains only the proposed intent/state/intervention, resolved outcome, closed reasons and diagnostics, policy version/fingerprint, override/verifier flags, verifier status and exact closed classification when present, semantic-completion-candidate flag, provider/model, token/cost metadata and resolver latency. Prompt text, model output, verifier artifact/output, next-action text, chain-of-thought and rejected values are never stored. The evaluation dataset projects the same minimized record and aggregates proposal→resolution, triggered reasons, overrides, verifier use, exact `Undetermined` classifications, false negatives/positives, resolver latency and diagnostics.

## Console API client

The console lives in `console/` with its own npm toolchain and consumes only the public
`/api/v1` surface. `createHiveApiClient` takes the API origin and the static organization
bearer credential from [Public API organization authorization](#public-api-organization-authorization);
`createOrganizationUpdatesClient` takes the same values and connects to
`/api/v1/organization-updates`. Both accept the token as a string or as a factory, so a
rotated credential is picked up on the next request or reconnect without rebuilding the
client. Credentials are supplied by the hosting page or build environment and never
committed.

```bash
npm --prefix console install
npm --prefix console run typecheck
npm --prefix console test
```

`npm test` runs one suite with two kinds of test. Logic tests (`*.test.ts`) run in Node;
component tests (`*.test.tsx`) render the console in `jsdom` against a stubbed API and hub,
and opt into that environment per file. Both need only the dev dependencies installed by
`npm --prefix console install`; neither needs a running API host or a browser.

Contract parity runs separately because it needs the OpenAPI document exported from the
API host. The document is a build artifact under `console/openapi/v1.json`, is git-ignored,
and is regenerated by the snapshot test; the parity check fails when it is missing, so it
never runs against a stale copy:

```bash
dotnet test tests/Hive.Tests --filter PublicApiOpenApiSnapshotTests
npm --prefix console run verify:contracts
```

Set `HIVE_OPENAPI_DOCUMENT` to verify against a document exported elsewhere, for example a
document published by a deployed environment. The check compares the routes the client
calls, the wire property names of every public schema and the enum values, so a backend
contract change fails here instead of in the browser.

## Console web application

The console is a static React + TypeScript bundle built by Vite from the same `console/`
workspace. It has no server of its own and no build-time secrets: the API origin, the
organization and that organization's read-only bearer credential are read at startup from
`window.__HIVE_CONSOLE_CONFIG__`, defined by a `config.js` served next to `index.html` and
injected into the page by `vite.config.ts`. The same bundle therefore runs against any
host, and rotating a credential is a file change rather than a rebuild.

| Key | Required | Meaning |
| --- | --- | --- |
| `apiBaseUrl` | no | Origin of the API host. Empty or absent means the console's own origin, which is what the dev-server proxy and a co-hosted deployment use. A value containing `/internal` is rejected. |
| `organizationId` | yes | Organization whose organogram is shown. |
| `token` | yes | Read-only organization bearer credential (see [Public API organization authorization](#public-api-organization-authorization)). |
| `pollIntervalMs` | no | Interval of the `/position-states` polling fallback. Defaults to `5000`, floored at `1000`. |

Missing or malformed configuration fails loudly on screen instead of rendering an empty
organogram, which would be indistinguishable from an organization with no units. The
credential is visible to anyone who can load the page, so it must be a read-only token and
`console/public/config.js` is git-ignored; `console/public/config.example.js` is the
template to copy.

```bash
cp console/public/config.example.js console/public/config.js   # then edit it
npm --prefix console run dev      # dev server, proxies /api/v1 to HIVE_CONSOLE_API_BASE_URL
npm --prefix console run build    # static bundle in console/dist
```

`HIVE_CONSOLE_API_BASE_URL` (default `http://localhost:53496`, the `Hive.Api` development
profile's HTTP endpoint) only configures the dev-server
proxy for REST and the SignalR websocket, so local development uses the same relative
`/api/v1` paths as a deployed bundle. When deploying, serve `console/dist` as static files
and place the environment's `config.js` at its root; `config.example.js` is copied into the
bundle as a template and is inert.

The organogram view keeps itself current from the SignalR hub and falls back to controlled
ETag polling of `/position-states` whenever the hub is not live. The REST snapshot is always
the source of truth: any reconnection or structural change refetches `/organogram`, and the
header states which channel is in use and when the view last agreed with the server.

What the console reports about its own state, and what each signal means operationally:

| Signal | Meaning |
| --- | --- |
| `Connecting to live updates` | The hub subscription is being established; polling covers the gap. |
| `Reconnecting — polling meanwhile` | The hub dropped. The snapshot is refetched when the subscription returns, so no update is silently missed. |
| `Polling fallback` | The hub is unavailable. States arrive with up to one `pollIntervalMs` of delay. |
| `Registry v<n> · updating` | A new registry version was published; the organogram on screen is the previous one until the refetch completes. |
| `Possibly out of date` | No update has succeeded for more than three `pollIntervalMs` (never less than 15s). Points at the API, the projection or the network, not at the console. |
| `The last update attempt failed` | The last request failed and the previous snapshot is still shown. A rejected credential or an out-of-scope organization is reported without a retry, because retrying cannot fix either. |

The view never replaces a snapshot it already holds with an error page, and it claims to be
current only with an established subscription: while polling it always shows the age of the
last agreement. `pollIntervalMs` therefore sets both how fast changes appear and how long a
stalled view stays quiet before it warns. An unreachable API before the first snapshot, an
unmaterialized read model (`503`) and an organization with no units or positions are three
distinct screens, because the operator reaction differs for each.

### Same-origin requirement

The API host declares no CORS policy, so a browser cannot call it from a different origin.
Every supported way of running the console therefore puts the two on one origin: the dev
server proxies `/api/v1` (REST and the SignalR websocket upgrade) to
`HIVE_CONSOLE_API_BASE_URL`, and the container image proxies the same prefix through nginx.
Leave `apiBaseUrl` empty in both cases — pointing it at another host reintroduces the
cross-origin request the proxy exists to avoid. Only `/api/v1` is proxied; `/internal`,
`/health` and `/diagnostics` are not reachable through the console.

### Run the console with Docker Compose

`docker-compose.console.yml` layers on the base one-node stack and adds the two things it
lacks for a browser to read an organogram: the static bearer credential of the public API,
absent by design from the tracked configuration, and the console itself served by nginx on
the same origin. Set `HIVE_CONSOLE_TOKEN` in `.env` first — Compose refuses to start while
it is empty.

```powershell
Copy-Item .env.example .env      # then set HIVE_CONSOLE_TOKEN
docker compose -f docker-compose.yml -f docker-compose.console.yml up --build
```

The console is published on `http://localhost:8090` and the API host stays on `8080`.

| Variable | Default | Purpose |
| --- | --- | --- |
| `HIVE_CONSOLE_ORGANIZATION_ID` | `acme-delivery` | Organization shown by the console and the single organization the credential is scoped to. |
| `HIVE_CONSOLE_TOKEN` | none, required | Serves as both `HIVE__PUBLICAPI__AUTHORIZATION__CREDENTIALS__0__TOKEN` on the API host and the token the console presents. |
| `HIVE_CONSOLE_POLL_INTERVAL_MS` | `5000` | Interval of the polling fallback. |

The image contains no configuration and no secret: `docker/40-hive-console-config.sh` runs
before nginx and writes `config.js` from these variables, failing the container start on
missing input rather than serving a console that only reports its own misconfiguration. The
same image therefore runs against any host, and rotating the credential is a restart.

The shared token is deliberate and is the reason this override is development-only: the
console presents the credential from the browser, so it is readable by anyone who can open
the page. That is tolerable only because the F1 public surface is read-only; real identity
arrives with `US-F1-09`.

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
