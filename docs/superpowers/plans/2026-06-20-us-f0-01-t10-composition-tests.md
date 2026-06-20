# US-F0-01-T10 Composition Tests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add composition tests that exercise the real API and Worker entry points and lock down startup, dependency injection, mandatory configuration, and domain isolation.

**Architecture:** Replace top-level entry points with explicit `Program.Build(args)` composition roots that are called by both `Main` and the tests. The tests start the resulting in-process hosts with isolated Akka ports, inspect the real service provider and readiness report, and independently verify that `Hive.Domain` has no forbidden project or assembly dependencies.

**Tech Stack:** .NET 8, ASP.NET Core, Generic Host, Akka.NET, xUnit, Microsoft health checks, XML project inspection.

---

### Task 1: Exercise the real API composition root

**Files:**
- Modify: `src/Hive.Api/Program.cs`
- Create: `tests/Hive.Tests/CompositionTests.cs`

- [ ] **Step 1: Write the failing API composition test**

Create `CompositionTests` in the existing non-parallel `AkkaClusterCollection`. Build the API through `Hive.Api.Program.Build(args)`, passing `--urls=http://127.0.0.1:0`, an ephemeral `Hive:Cluster:Port`, the `api` role, and a non-empty PostgreSQL connection string. Start it and assert that `ActorSystem`, `ActiveNodeRoles`, `HealthCheckService`, and `NodeDiagnosticsProvider` resolve, that only `api` is active, and that the readiness report is healthy.

- [ ] **Step 2: Run the API test and verify RED**

Run: `dotnet test Hive.sln --filter FullyQualifiedName~CompositionTests.Api_entry_point --no-restore`

Expected: compilation fails because the top-level API entry point does not expose `Hive.Api.Program.Build`.

- [ ] **Step 3: Expose the API composition root**

Replace the top-level statements with an explicit `Hive.Api.Program` class:

```csharp
namespace Hive.Api;

public static class Program
{
    public static void Main(string[] args) => Build(args).Run();

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddHiveBootstrap();
        builder.AddHiveActorSystem();

        var app = builder.Build();
        app.MapHiveDiagnostics();
        return app;
    }
}
```

Keep the existing `Hive.Actors`, diagnostics, and configuration using directives.

- [ ] **Step 4: Run the API test and verify GREEN**

Run: `dotnet test Hive.sln --filter FullyQualifiedName~CompositionTests.Api_entry_point --no-restore`

Expected: one passing test; the API starts and stops cleanly.

### Task 2: Exercise Worker composition and mandatory configuration

**Files:**
- Modify: `src/Hive.Worker/Program.cs`
- Modify: `tests/Hive.Tests/Hive.Tests.csproj`
- Modify: `tests/Hive.Tests/CompositionTests.cs`
- Modify: `tests/Hive.Tests/DiagnosticsEndpointTests.cs`
- Modify: `tests/Hive.Tests/HiveActorSystemTests.cs`

- [ ] **Step 1: Add the Worker project reference and failing Worker test**

Reference `src/Hive.Worker/Hive.Worker.csproj` from `Hive.Tests`. Add a test that calls `Hive.Worker.Program.Build(args)` with ephemeral Akka configuration, the canonical backend roles, and a PostgreSQL connection string. Start it and assert the same common services resolve, that the three backend roles are active, and that readiness is healthy.

- [ ] **Step 2: Run the Worker test and verify RED**

Run: `dotnet test Hive.sln --filter FullyQualifiedName~CompositionTests.Worker_entry_point --no-restore`

Expected: compilation fails because the top-level Worker entry point does not expose `Hive.Worker.Program.Build`.

- [ ] **Step 3: Expose the Worker composition root**

Replace the top-level statements with an explicit `Hive.Worker.Program` class:

```csharp
namespace Hive.Worker;

public static class Program
{
    public static void Main(string[] args) => Build(args).Run();

    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddHiveBootstrap();
        builder.AddHiveActorSystem();
        return builder.Build();
    }
}
```

Keep the existing actors and configuration using directives and add `Microsoft.Extensions.Hosting` if implicit usings do not supply it.

- [ ] **Step 4: Add composition assertions for required configuration**

Add entry-point tests that:

- pass an empty role value to each executable and assert `StartAsync` throws `OptionsValidationException` mentioning `Hive:Node:Roles`;
- start each executable without `ConnectionStrings:PostgreSql`, run only checks tagged `ready`, and assert `HealthStatus.Unhealthy` with the dependencies check unhealthy.

Use a fresh ephemeral Akka port per host and keep all host lifetimes inside `using`/`await using` scopes.
Pass the source directory of each executable as its explicit `contentRoot`, because direct references to both executable projects copy same-named `appsettings.json` files to the test output. Clear or disable default configuration sources in pre-existing test-only builders so they cannot consume those colliding output files.

- [ ] **Step 5: Run all composition tests**

Run: `dotnet test Hive.sln --filter FullyQualifiedName~CompositionTests --no-restore`

Expected: all API and Worker composition tests pass without port conflicts.

### Task 3: Lock down domain isolation

**Files:**
- Modify: `tests/Hive.Tests/Hive.Tests.csproj`
- Create: `tests/Hive.Tests/DomainIsolationTests.cs`

- [ ] **Step 1: Reference and inspect the domain assembly**

Add a project reference from `Hive.Tests` to `Hive.Domain`. Create tests that load `Hive.Domain.dll` from `AppContext.BaseDirectory` and assert its referenced assembly names do not start with `Akka`, `Microsoft.AspNetCore`, `Microsoft.Extensions.Hosting`, or `Microsoft.Extensions.DependencyInjection`, and do not equal any non-domain Hive assembly.

- [ ] **Step 2: Inspect the domain project contract**

Load `src/Hive.Domain/Hive.Domain.csproj` with `XDocument` and assert it contains no `ProjectReference`, `PackageReference`, `FrameworkReference`, or explicit `Reference` elements. Reuse a local repository-root locator in this focused test class.

- [ ] **Step 3: Run the isolation tests**

Run: `dotnet test Hive.sln --filter FullyQualifiedName~DomainIsolationTests --no-restore`

Expected: both tests pass and prove the existing isolated domain boundary.

### Task 4: Verify the complete solution

**Files:**
- Modify only if verification exposes a T10 regression.

- [ ] **Step 1: Run the complete test suite**

Run: `dotnet test Hive.sln --no-restore`

Expected: exit code 0 and no failed tests.

- [ ] **Step 2: Build all executable projects**

Run: `dotnet build Hive.sln --no-restore`

Expected: exit code 0 with no errors.

- [ ] **Step 3: Review scope and working-tree diff**

Run: `git diff --check` and `git status --short`.

Expected: no whitespace errors; changes are limited to entry-point composition seams, T10 tests, test project references, and this plan.

- [ ] **Step 4: Prepare the commit message**

Do not create a commit. Return a short English message suitable for the user to apply, for example: `test(composition): cover real host startup and domain isolation (US-F0-01-T10)`.
