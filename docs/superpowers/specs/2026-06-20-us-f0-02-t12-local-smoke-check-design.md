# US-F0-02-T12 Local Smoke Check Design

**Date:** 2026-06-20
**Status:** Approved

## Context

US-F0-02-T12 requires a local smoke check that validates three properties of an already-running Docker Compose environment: the API is healthy, PostgreSQL is accessible, and the configured HIVE node count matches the expected one-node or three-node topology. The base and cluster Compose files, health checks, readiness contract, local credentials template, and lifecycle commands already exist from US-F0-02-T03 through T11.

The smoke check is diagnostic only. It must not start, stop, rebuild, or otherwise mutate the local stack.

## Goals

- Provide one deterministic command for validating either supported local topology.
- Verify the API readiness endpoint returns HTTP 200.
- Verify PostgreSQL accepts connections from inside its Compose container.
- Verify the selected Compose configuration contains exactly the expected HIVE services and that those services are running and healthy.
- Return exit code zero only when every check passes, with concise progress and failure output.
- Keep validation logic testable without requiring Docker or a running stack.

## Non-goals

- Starting, stopping, rebuilding, or cleaning the Compose environment.
- Verifying Akka cluster convergence or membership; T12 asks for the expected number of configured nodes, not a new cluster-management endpoint.
- Publishing the internal PostgreSQL or secondary-node ports.
- Introducing a CI container-integration suite, Testcontainers, Pester, or other dependencies.
- Changing the product architecture or the contracts already recorded in `docs/bible.html`.

## Selected Approach

Add `scripts/Test-LocalStack.ps1`, a PowerShell smoke command with a required `-ExpectedNodes` argument restricted to `1` or `3`. The argument selects the same Compose file set documented for operators: the base file alone for one node, or the base plus `docker-compose.cluster.yml` for three nodes.

The command calls Docker Compose directly, queries the resolved services and running container state as JSON, executes `pg_isready` inside PostgreSQL, and calls `http://localhost:8080/health/ready`. It stops at the first failed invariant, prints a clear error, and exits non-zero. It prints a short success line for each completed check and exits zero when all checks pass.

PowerShell is preferred because it matches the primary repository environment, offers reliable process exit-code handling and JSON parsing, and needs no new project or package. Maintaining parallel Bash and PowerShell implementations would duplicate behavior. An xUnit/Testcontainers suite would own infrastructure lifecycle and conflict with the approved requirement to inspect an already-running stack.

## Components and Boundaries

`scripts/Test-LocalStack.ps1` owns orchestration and external calls. Its small validation functions accept parsed data and throw on invalid service sets or states. The script executes its main routine only when run normally, allowing tests to dot-source it and exercise those functions without calling Docker or HTTP.

`tests/Smoke/LocalStackSmoke.Tests.ps1` is a dependency-free PowerShell test runner. It supplies representative Compose snapshots to the pure validation functions, verifies both Compose JSON formats, and exercises successful orchestration through substituted Docker and HTTP command boundaries. It covers accepted one-node/three-node states plus rejection of missing, extra, stopped, or unhealthy HIVE nodes. It exits non-zero on an assertion failure, so it can be run locally or by future CI.

`docs/configuration.md` owns the operational instructions. It gains the commands for running the smoke check against each topology and states that the stack must already be running. The bible already owns the T12 requirement and therefore needs no duplicated implementation narrative.

## Validation Flow

1. Resolve the Compose arguments from `-ExpectedNodes`.
2. Run `docker compose ... config --services` and require the exact HIVE service set: `api` for one node; `api`, `api2`, and `api3` for three nodes. Require `postgres` in both modes.
3. Run `docker compose ... ps --format json`, normalize the supported JSON shape, and require every expected service to be present and running.
4. Require PostgreSQL and every expected HIVE service to report Docker health `healthy`.
5. Run `pg_isready` inside the `postgres` service using its container environment and require a zero exit code.
6. Request `/health/ready` from the published API endpoint and require HTTP 200.
7. Print the successful topology summary and exit zero.

## Error Handling

- A missing Docker executable, Compose failure, invalid JSON response, absent service, wrong node count, non-running container, unhealthy container, failed `pg_isready`, or failed HTTP request is a smoke-check failure.
- Errors identify the failed invariant and retain relevant command output without printing credentials.
- The script does not attempt recovery or cleanup because it does not own the running stack.

## Testing

Implementation follows red-green-refactor:

1. Add dependency-free tests for service-set and container-state validation and observe them fail because the script does not exist.
2. Add the minimum validation functions and entry-point guard needed to pass those tests.
3. Add the Docker Compose, PostgreSQL, and HTTP orchestration around the tested validation logic.
4. Run the focused PowerShell tests.
5. Run the repository's existing .NET test suite and build to detect regressions.
6. If Docker is available and a local stack is active, run the smoke command against that topology; otherwise verify Compose configuration resolution and report the environmental limitation explicitly.

## Documentation

Add only the operational commands to `docs/configuration.md`:

```powershell
./scripts/Test-LocalStack.ps1 -ExpectedNodes 1
./scripts/Test-LocalStack.ps1 -ExpectedNodes 3
```

No bible change is required because T12 and the supported topologies are already canonical there, and the smoke check introduces no durable architectural contract.
