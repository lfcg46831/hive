# US-F0-01-T05 Common Bootstrap Design

**Date:** 2026-06-20
**Status:** Approved

## Context

US-F0-01-T05 introduces the common dependency-injection and configuration bootstrap shared by `Hive.Api` and `Hive.Worker`. The project bible, version 0.35, requires strongly typed binding for `Hive:Node:Roles` and fail-fast startup validation. Empty role lists, empty entries, unknown names, and duplicates must be rejected explicitly; duplicate detection occurs after trimming and uses case-insensitive comparison.

The configuration model and executable defaults already exist from US-F0-01-T04. Applying roles to Akka or selecting workloads remains assigned to US-F0-01-T06.

## Goals

- Give both executable hosts one common bootstrap entry point.
- Bind the `Hive` configuration section to `HiveOptions` through the standard .NET options pipeline.
- Validate `Hive:Node:Roles` before a host begins normal execution.
- Produce actionable validation errors that identify `Hive:Node:Roles` and the offending values or positions.
- Preserve the configured collection without trimming, case normalization, or silent deduplication.
- Protect binding, validation, and startup behavior with automated tests.
- Record the completed T05 implementation in the project bible and operational configuration reference.

## Non-goals

- Applying roles to Akka.Cluster or starting role-specific workloads.
- Validating or opening the PostgreSQL connection string.
- Adding application services beyond the options bootstrap and validator.
- Adding logging, health checks, diagnostics endpoints, Compose, or Kubernetes configuration.
- Rewriting the existing configuration model or executable defaults.

## Selected Approach

Add an extension for `IHostApplicationBuilder` in `Hive.Infrastructure`. The extension registers `HiveOptions` from the `Hive` section, registers a dedicated `IValidateOptions<HiveOptions>` implementation, and enables `ValidateOnStart`. Both executable `Program.cs` files call this extension before building their host.

This shape is preferred because both `WebApplicationBuilder` and `HostApplicationBuilder` implement the same hosting abstraction. It keeps configuration and service registration together, makes incorrect partial setup harder, and exercises the standard options lifecycle rather than adding a parallel validation mechanism.

The alternatives are not selected:

- An `IServiceCollection` extension taking `IConfiguration` would work but would expose two inputs that are already available together on `IHostApplicationBuilder`.
- Manual binding and exceptions in each `Program.cs` would duplicate behavior and bypass the options pipeline.

## Components and Boundaries

`HiveBootstrapExtensions` owns the public composition entry point. It binds only the root `Hive` section and registers only services needed by T05. It does not know which executable called it or activate runtime workloads.

`HiveOptionsValidator` owns the role validation algorithm. It has no dependency on a host, file, or environment variable source, so it can be tested directly. It returns `ValidateOptionsResult` failures rather than throwing itself; the options system turns those failures into `OptionsValidationException` during startup validation.

`Hive.Api` and `Hive.Worker` add a project reference to `Hive.Infrastructure` and invoke the common extension. They retain their existing host construction and configuration-source precedence.

## Binding and Validation Rules

The bootstrap binds `configuration.GetSection(HiveOptions.SectionName)` to `HiveOptions`. Standard .NET configuration precedence remains unchanged: base JSON, environment-specific JSON, command-line configuration where applicable, and environment variables are combined by each executable's existing builder.

Validation uses the following rules:

1. `Hive:Node:Roles` must resolve to a non-null collection with at least one entry.
2. Every entry must contain non-whitespace text after `Trim`.
3. A non-empty trimmed value must match one of `agents`, `gateway`, `connectors`, or `api` using `StringComparer.OrdinalIgnoreCase`.
4. Duplicate groups are detected from trimmed values using `StringComparer.OrdinalIgnoreCase`; for example, `"api"`, `" API "`, and `"Api"` are duplicates.
5. Validation never rewrites, normalizes, removes, or deduplicates bound values.

A single validation attempt reports all applicable failures so an operator can correct the configuration in one iteration. Every failure includes the `Hive:Node:Roles` path. Empty-entry failures include array positions; unknown and duplicate failures include the offending configured values. Values are formatted safely for diagnostics, including explicit markers for `null` and whitespace-only entries.

## Startup Behavior

The bootstrap uses `ValidateOnStart()`. Calling `StartAsync`, `Run`, or `RunAsync` on either built host therefore resolves and validates `HiveOptions` before normal hosted execution. Invalid configuration produces an `OptionsValidationException` whose failures contain the actionable messages defined by the validator.

Building the service provider alone is not treated as successful startup. Tests that assert fail-fast behavior start a host and observe the exception from the options startup validator.

## Testing

Focused validator tests cover:

- a canonical role list;
- case-insensitive, trimmed recognition of individual role names;
- an empty or null role collection;
- null, empty, and whitespace-only entries;
- unknown names;
- exact, case-only, and whitespace-only duplicate variants;
- combined invalid conditions and explicit diagnostic content;
- preservation of the original bound values.

Bootstrap composition tests use an in-memory configuration provider and a real `HostApplicationBuilder`. They verify successful typed binding for valid roles and an `OptionsValidationException` when the host starts with invalid roles. Existing solution-boundary tests and compilation verify that both executables reference the shared infrastructure project and use the common entry point.

## Documentation and Bible Update

`docs/configuration.md` is updated to describe binding, accepted role matching, validation failures, and fail-fast behavior as implemented by T05. The project bible is advanced to version 0.36 with an implementation note recording the shared bootstrap location, standard options pipeline, validator semantics, and continued T06 runtime boundary.

## Verification

Implementation follows red-green-refactor:

1. Add validator and bootstrap tests and observe the expected failures.
2. Add the minimum package/project references and production types required to pass them.
3. Integrate both executable programs and verify invalid startup behavior.
4. Run focused tests, the complete test project, and a full solution build.
5. Review the final diff against this design, US-F0-01-T05, and bible section 5.10.
