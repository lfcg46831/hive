# US-F0-01-T07 Common Structured Logging Design

**Date:** 2026-06-20
**Status:** Approved

## Context

US-F0-01-T07 configures the common structured logging shared by every executable host. The project bible requires that "logging, configuração e health checks mínimos existem desde o primeiro executável" and treats observability (§11) as a first-class concern, with distributed tracing correlated by `ThreadId`/`DirectiveId` arriving in later phases.

The common dependency-injection and configuration bootstrap already exists from US-F0-01-T05 (`HiveBootstrapExtensions.AddHiveBootstrap`), and US-F0-01-T06 wired a real Akka.Cluster `ActorSystem` per role. Today both hosts call `AddLogging()`, which only registers the logging infrastructure and leaves each host with the default text console provider. Akka writes through its own default stdout logger, so a single host emits log lines in two unrelated, unstructured formats.

## Goals

- Give both executable hosts (`Hive.Api`, `Hive.Worker`) one common, structured logging configuration.
- Emit machine-readable structured log records (JSON) with consistent options across hosts.
- Preserve log scopes so future correlation metadata (`ThreadId`/`DirectiveId`) is carried in output.
- Route the Akka actor system's logs through the same `ILoggerFactory` so the host emits one uniform log stream.
- Keep the configuration in the shared bootstrap so no host can opt out by accident.
- Respect the standard `Logging` configuration section (levels, filters) already present in `appsettings`.
- Protect the logging composition with automated tests.

## Non-goals

- Adding OpenTelemetry, metrics, exporters, or distributed tracing (later phases, §11).
- Adding Serilog or any third-party logging framework.
- Defining correlation enrichment for `ThreadId`/`DirectiveId` (the message protocol does not exist yet; arrives with US-F0-03 and the actors that carry it).
- File/sink configuration, log shipping, or the optional Docker log volume (US-F0-02-T07).
- Health checks or the diagnostics endpoint (US-F0-01-T08 / T09).

## Selected Approach

Add an `IHostApplicationBuilder` extension `AddHiveStructuredLogging` in `Hive.Infrastructure.Logging`. It clears the default logging providers and registers the built-in JSON console formatter with consistent options (scopes included, UTC timestamps). `AddHiveBootstrap` calls this extension instead of the bare `AddLogging()`, so both hosts inherit identical structured logging through the single composition entry point they already share.

The built-in `Microsoft.Extensions.Logging.Console` JSON formatter is preferred over a third-party framework because the F0 stack is deliberately minimal and Microsoft.Extensions-only; the bible reserves richer observability (OpenTelemetry) for later phases. JSON console output is structured, captures scopes and structured state, and needs no extra infrastructure to be useful locally or in Docker Compose, where stdout is the collection point.

To make logging genuinely common "para todos os hosts", the Akka bootstrap routes the actor system's logs into the same `ILoggerFactory` via Akka.Hosting's `ConfigureLoggers`/`AddLoggerFactory`. Without this, Akka keeps its default stdout logger and the host emits a second, unstructured stream, defeating the "comum" requirement.

Alternatives not selected:

- A `Serilog` pipeline would add a dependency and a parallel configuration model the rest of F0 does not use.
- Configuring logging separately inside each `Program.cs` would duplicate behavior and let the two hosts drift.
- Leaving Akka on its default logger would keep two log formats inside one process.

## Components and Boundaries

`HiveLoggingBootstrapExtensions` (in `Hive.Infrastructure.Logging`) owns the structured logging composition: clear providers, add the JSON console formatter, set formatter options. It does not read the `Hive` section, start workloads, or touch Akka.

`HiveBootstrapExtensions.AddHiveBootstrap` calls `AddHiveStructuredLogging` in place of `AddLogging()`. It remains the single shared composition entry point for both executables.

`HiveActorSystemBootstrapExtensions` (in `Hive.Actors`) adds a `ConfigureLoggers` block that clears Akka's default loggers and adds the `LoggerFactory` logger, so the actor system writes through the DI `ILoggerFactory`. It does not change cluster, remoting, or role behavior from US-F0-01-T06.

The executable `Program.cs` files are unchanged: they already call `AddHiveBootstrap` then `AddHiveActorSystem`.

## Logging Configuration Rules

1. Default logging providers are cleared so output is deterministic and identical across hosts.
2. A JSON console provider is registered as the single structured sink.
3. The JSON formatter includes scopes and uses UTC timestamps with a fixed timestamp format.
4. Standard log-level configuration from the `Logging` section keeps working through the normal options pipeline; the extension changes the output format and provider set, not the level filters.
5. Akka logs are emitted through the same `ILoggerFactory`, so actor-system messages share the structured format and level filtering.

## Startup Behavior

Both hosts build their logging during host construction. After `AddHiveBootstrap`, the only registered `ILoggerProvider` is the JSON console provider, and `JsonConsoleFormatterOptions.IncludeScopes` is `true`. The actor system, started by `AddHiveActorSystem`, logs through that same provider once running.

## Testing

Focused tests in `Hive.Tests` cover:

- the extension clears any pre-existing logging providers and leaves exactly one console provider registered;
- the JSON console formatter options enable scopes and UTC timestamps;
- `AddHiveBootstrap` composes the structured logging so a built host exposes the JSON console provider as its only provider.

The Akka logger bridge is verified by compilation and existing actor-system composition tests, which build the host and resolve the actor system; routing logs through `ILoggerFactory` does not change those assertions but must keep them green.

## Documentation and Bible Update

`docs/configuration.md` gains a "Logging" section describing the structured JSON console output, scope inclusion, the unchanged `Logging` level section, and the Akka-to-`ILoggerFactory` bridge. The project bible is advanced one version with a §5.10 implementation note and a changelog row recording the common structured logging shared by both hosts and the Akka log routing, while keeping OpenTelemetry/tracing reserved for later phases.

## Verification

Implementation follows red-green-refactor:

1. Add logging tests and observe the expected failures.
2. Add the minimum package reference and production types to pass them.
3. Wire the bootstrap and the Akka logger bridge.
4. Run focused tests, the full test project, and a solution build (in Visual Studio / `dotnet`; the sandbox has no .NET SDK).
5. Review the final diff against this design, US-F0-01-T07, and bible §5.10/§11.
