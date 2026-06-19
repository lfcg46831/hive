# US-F0-01-T03 Project References Design

**Date:** 2026-06-19  
**Status:** Approved

## Context

US-F0-01-T03 defines the minimum compile-time dependency graph for the base Hive projects. The project bible, version 0.30, limits this task to `ProjectReference` entries and architecture tests. Runtime packages, Akka configuration, dependency injection bootstrap, and executable host behavior belong to later tasks.

## Goals

- Define a small, directional project dependency graph.
- Keep `Hive.Domain` independent from Akka, ASP.NET Core, infrastructure, and application bootstrap concerns.
- Protect the graph with architecture tests that fail on missing or unexpected references.

## Non-goals

- Adding NuGet packages or framework references.
- Adding application, actor, infrastructure, API, or worker implementation code.
- Configuring Akka, dependency injection, settings, logging, health checks, or executable hosts.

## Dependency Graph

The allowed direct references are:

| Project | Direct project references |
| --- | --- |
| `Hive.Domain` | None |
| `Hive.Actors` | `Hive.Domain` |
| `Hive.Infrastructure` | `Hive.Domain` |
| `Hive.Api` | `Hive.Actors`, `Hive.Infrastructure` |
| `Hive.Worker` | `Hive.Actors`, `Hive.Infrastructure` |
| `Hive.Tests` | None |

`Hive.Api` and `Hive.Worker` are composition boundaries. They can combine actor and infrastructure implementations without forcing either implementation project to depend on the other. They do not reference `Hive.Domain` directly until host code has a concrete need for domain types.

`Hive.Tests` reads project XML to validate architecture metadata, so it does not need production `ProjectReference` entries for this task. Future behavioral tests can add only the references they actually use.

## Architecture Tests

The tests will parse each `.csproj` with `System.Xml.Linq` and compare normalized project names against the exact allowed set. This detects both missing dependencies and accidental extra coupling.

An additional domain isolation test will verify that `Hive.Domain`:

- has no `ProjectReference` entries;
- has no `PackageReference` entries;
- has no `FrameworkReference` entries; and
- uses the plain `Microsoft.NET.Sdk`, not a web or worker SDK.

Failures will identify the project and show the expected and actual reference sets. No runtime startup or dependency injection composition is exercised.

## Verification

Implementation follows test-driven development:

1. Add the architecture expectations and run them to observe failure because the required references are absent.
2. Add the approved `ProjectReference` entries.
3. Run the focused architecture tests.
4. Run the complete test project and build the solution.

## Scope Boundary

The implementation must not modify `docs/bible.html`, add runtime packages, or introduce source code outside the architecture test. Existing unrelated workspace changes remain untouched.
