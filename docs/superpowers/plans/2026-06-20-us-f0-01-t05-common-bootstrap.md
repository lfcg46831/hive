# US-F0-01-T05 Common Bootstrap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add one shared .NET bootstrap that binds and fail-fast validates `Hive:Node:Roles` for both executable hosts.

**Architecture:** `Hive.Infrastructure.Configuration` exposes an `IHostApplicationBuilder` extension that configures the standard options pipeline and registers a focused `IValidateOptions<HiveOptions>` validator. `Hive.Api` and `Hive.Worker` reference that project and call the same extension before building, while Akka role application remains outside this task.

**Tech Stack:** .NET 8, C# 12, Microsoft.Extensions.Hosting 8.0.1, Microsoft.Extensions.Options.ConfigurationExtensions 8.0.0, xUnit 2.5.3.

---

## File Structure

- Modify `src/Hive.Infrastructure/Hive.Infrastructure.csproj`: reference the hosting and options abstractions needed by the shared bootstrap.
- Create `src/Hive.Infrastructure/Configuration/HiveBootstrapExtensions.cs`: bind `HiveOptions`, register validation, and enable startup validation.
- Create `src/Hive.Infrastructure/Configuration/HiveOptionsValidator.cs`: implement all role validation rules and diagnostics.
- Modify `tests/Hive.Tests/Hive.Tests.csproj`: add the concrete host package used by composition tests.
- Create `tests/Hive.Tests/HiveOptionsValidatorTests.cs`: verify role validation independently from configuration sources.
- Create `tests/Hive.Tests/HiveBootstrapTests.cs`: verify real binding and `ValidateOnStart` behavior.
- Modify `tests/Hive.Tests/SolutionStructureTests.cs`: enforce the shared-bootstrap reference and call in both executables.
- Modify `src/Hive.Api/Hive.Api.csproj`: reference `Hive.Infrastructure`.
- Modify `src/Hive.Api/Program.cs`: call the shared bootstrap.
- Modify `src/Hive.Worker/Hive.Worker.csproj`: reference `Hive.Infrastructure`.
- Modify `src/Hive.Worker/Program.cs`: call the shared bootstrap.
- Modify `docs/configuration.md`: document the implemented binding and startup validation contract.
- Modify `docs/bible.html`: record the T05 implementation as version 0.36.

### Task 1: Role validator and common options bootstrap

**Files:**
- Modify: `src/Hive.Infrastructure/Hive.Infrastructure.csproj`
- Modify: `tests/Hive.Tests/Hive.Tests.csproj`
- Create: `tests/Hive.Tests/HiveOptionsValidatorTests.cs`
- Create: `tests/Hive.Tests/HiveBootstrapTests.cs`
- Create: `src/Hive.Infrastructure/Configuration/HiveOptionsValidator.cs`
- Create: `src/Hive.Infrastructure/Configuration/HiveBootstrapExtensions.cs`

- [ ] **Step 1: Add the packages required to compile host-based tests and the future bootstrap**

Add this item group to `src/Hive.Infrastructure/Hive.Infrastructure.csproj`:

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="8.0.1" />
    <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="8.0.0" />
  </ItemGroup>
```

Add this package to the existing package item group in `tests/Hive.Tests/Hive.Tests.csproj`:

```xml
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.1" />
```

Run:

```powershell
dotnet restore Hive.sln
```

Expected: restore succeeds for all six projects.

- [ ] **Step 2: Write failing validator behavior tests**

Create `tests/Hive.Tests/HiveOptionsValidatorTests.cs`:

```csharp
using Hive.Infrastructure.Configuration;

namespace Hive.Tests;

public sealed class HiveOptionsValidatorTests
{
    private readonly HiveOptionsValidator _validator = new();

    [Fact]
    public void Canonical_roles_are_valid()
    {
        var result = Validate(["agents", "gateway", "connectors", "api"]);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Trimmed_case_insensitive_roles_are_valid_without_mutation()
    {
        var roles = new[] { " API ", "Agents" };
        var options = CreateOptions(roles);

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
        Assert.Same(roles, options.Node.Roles);
        Assert.Equal(new[] { " API ", "Agents" }, options.Node.Roles);
    }

    [Fact]
    public void Empty_role_list_is_invalid()
    {
        var result = Validate([]);

        AssertFailure(result, "Hive:Node:Roles must contain at least one role.");
    }

    [Fact]
    public void Null_role_list_is_invalid()
    {
        var options = new HiveOptions();
        options.Node.Roles = null!;

        var result = _validator.Validate(null, options);

        AssertFailure(result, "Hive:Node:Roles must contain at least one role.");
    }

    [Fact]
    public void Empty_unknown_and_duplicate_values_are_reported_together()
    {
        var result = Validate(["api", " API ", "scheduler", " ", null!]);
        var failures = result.Failures.ToArray();

        Assert.True(result.Failed);
        Assert.Contains(failures, failure => failure.Contains("Hive:Node:Roles[2]") && failure.Contains("\"scheduler\""));
        Assert.Contains(failures, failure => failure.Contains("Hive:Node:Roles[3]") && failure.Contains("must not be empty"));
        Assert.Contains(failures, failure => failure.Contains("Hive:Node:Roles[4]") && failure.Contains("<null>"));
        Assert.Contains(failures, failure => failure.Contains("duplicate role values") && failure.Contains("\"api\"") && failure.Contains("\" API \""));
    }

    [Theory]
    [InlineData("api", "api")]
    [InlineData("api", "API")]
    [InlineData("api", " API ")]
    public void Duplicate_roles_use_trimmed_case_insensitive_comparison(string first, string second)
    {
        var result = Validate([first, second]);

        Assert.Contains(
            result.Failures,
            failure => failure.Contains("Hive:Node:Roles") && failure.Contains("duplicate role values"));
    }

    private Microsoft.Extensions.Options.ValidateOptionsResult Validate(string[] roles) =>
        _validator.Validate(null, CreateOptions(roles));

    private static HiveOptions CreateOptions(string[] roles) =>
        new()
        {
            Node = new NodeOptions
            {
                Roles = roles,
            },
        };

    private static void AssertFailure(
        Microsoft.Extensions.Options.ValidateOptionsResult result,
        string expectedFailure)
    {
        Assert.True(result.Failed);
        Assert.Contains(expectedFailure, result.Failures);
    }
}
```

- [ ] **Step 3: Write failing real-bootstrap tests**

Create `tests/Hive.Tests/HiveBootstrapTests.cs`:

```csharp
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class HiveBootstrapTests
{
    [Fact]
    public async Task Bootstrap_binds_roles_and_preserves_configured_values()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = " API ",
            ["Hive:Node:Roles:1"] = "Agents",
        });
        using var host = builder.Build();

        await host.StartAsync();
        var options = host.Services.GetRequiredService<IOptions<HiveOptions>>().Value;
        await host.StopAsync();

        Assert.Equal(new[] { " API ", "Agents" }, options.Node.Roles);
    }

    [Fact]
    public async Task Bootstrap_rejects_invalid_roles_when_host_starts()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = "api",
            ["Hive:Node:Roles:1"] = "API",
        });
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("Hive:Node:Roles") && failure.Contains("duplicate role values"));
    }

    private static HostApplicationBuilder CreateBuilder(
        IReadOnlyDictionary<string, string?> configuration)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.AddHiveBootstrap();
        return builder;
    }
}
```

- [ ] **Step 4: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter "FullyQualifiedName~HiveOptionsValidatorTests|FullyQualifiedName~HiveBootstrapTests" --no-restore
```

Expected: compilation fails because `HiveOptionsValidator` and `AddHiveBootstrap` do not exist.

- [ ] **Step 5: Implement the role validator**

Create `src/Hive.Infrastructure/Configuration/HiveOptionsValidator.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Configuration;

public sealed class HiveOptionsValidator : IValidateOptions<HiveOptions>
{
    private const string RolesPath = $"{HiveOptions.SectionName}:Node:Roles";

    private static readonly HashSet<string> KnownRoles =
        new(NodeRoleNames.All, StringComparer.OrdinalIgnoreCase);

    public ValidateOptionsResult Validate(string? name, HiveOptions options)
    {
        var roles = options.Node?.Roles;
        if (roles is null || roles.Length == 0)
        {
            return ValidateOptionsResult.Fail($"{RolesPath} must contain at least one role.");
        }

        var failures = new List<string>();
        var comparableRoles = new List<(string Raw, string Trimmed)>();

        for (var index = 0; index < roles.Length; index++)
        {
            var role = roles[index];
            if (string.IsNullOrWhiteSpace(role))
            {
                failures.Add(
                    $"{RolesPath}[{index}] must not be empty (configured value: {Format(role)}).");
                continue;
            }

            var trimmed = role.Trim();
            comparableRoles.Add((role, trimmed));

            if (!KnownRoles.Contains(trimmed))
            {
                failures.Add(
                    $"{RolesPath}[{index}] contains unknown role {Format(role)}. " +
                    $"Allowed values: {string.Join(", ", NodeRoleNames.All)}.");
            }
        }

        foreach (var duplicateGroup in comparableRoles
                     .GroupBy(role => role.Trimmed, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            failures.Add(
                $"{RolesPath} contains duplicate role values after trimming and " +
                $"case-insensitive comparison: {string.Join(", ", duplicateGroup.Select(role => Format(role.Raw)))}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static string Format(string? value)
    {
        if (value is null)
        {
            return "<null>";
        }

        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return $"<whitespace:{value.Length}>";
        }

        return $"\"{value.Replace("\"", "\\\"")}\"";
    }
}
```

- [ ] **Step 6: Implement the shared bootstrap extension**

Create `src/Hive.Infrastructure/Configuration/HiveBootstrapExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Configuration;

public static class HiveBootstrapExtensions
{
    public static IHostApplicationBuilder AddHiveBootstrap(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IValidateOptions<HiveOptions>, HiveOptionsValidator>();
        builder.Services
            .AddOptions<HiveOptions>()
            .Bind(builder.Configuration.GetSection(HiveOptions.SectionName))
            .ValidateOnStart();

        return builder;
    }
}
```

- [ ] **Step 7: Run focused tests and verify GREEN**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter "FullyQualifiedName~HiveOptionsValidatorTests|FullyQualifiedName~HiveBootstrapTests" --no-restore
```

Expected: 10 tests pass with 0 failures.

- [ ] **Step 8: Commit the validator and bootstrap**

```powershell
git add src/Hive.Infrastructure/Hive.Infrastructure.csproj src/Hive.Infrastructure/Configuration/HiveOptionsValidator.cs src/Hive.Infrastructure/Configuration/HiveBootstrapExtensions.cs tests/Hive.Tests/Hive.Tests.csproj tests/Hive.Tests/HiveOptionsValidatorTests.cs tests/Hive.Tests/HiveBootstrapTests.cs
git commit -m "Add fail-fast configuration bootstrap"
```

### Task 2: Integrate both executable hosts

**Files:**
- Modify: `tests/Hive.Tests/SolutionStructureTests.cs`
- Modify: `src/Hive.Api/Hive.Api.csproj`
- Modify: `src/Hive.Api/Program.cs`
- Modify: `src/Hive.Worker/Hive.Worker.csproj`
- Modify: `src/Hive.Worker/Program.cs`

- [ ] **Step 1: Add failing executable-composition tests**

Add this data and these tests inside `SolutionStructureTests`, before `RepositoryRoot`:

```csharp
    public static TheoryData<string, string> ExecutableProjects => new()
    {
        { "src/Hive.Api/Hive.Api.csproj", "src/Hive.Api/Program.cs" },
        { "src/Hive.Worker/Hive.Worker.csproj", "src/Hive.Worker/Program.cs" },
    };

    [Theory]
    [MemberData(nameof(ExecutableProjects))]
    public void Executable_references_shared_infrastructure(string projectPath, string _)
    {
        var project = XDocument.Load(Path.Combine(RepositoryRoot, projectPath));
        var references = project.Root?
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value.Replace('\\', '/'))
            .ToArray();

        Assert.Contains("../Hive.Infrastructure/Hive.Infrastructure.csproj", references);
    }

    [Theory]
    [MemberData(nameof(ExecutableProjects))]
    public void Executable_calls_common_bootstrap(string _, string programPath)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, programPath));

        Assert.Contains("builder.AddHiveBootstrap();", source);
    }
```

- [ ] **Step 2: Run the composition tests and verify RED**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter "FullyQualifiedName~Executable_references_shared_infrastructure|FullyQualifiedName~Executable_calls_common_bootstrap" --no-restore
```

Expected: all 4 cases fail because neither executable references `Hive.Infrastructure` or calls the common bootstrap.

- [ ] **Step 3: Reference infrastructure from the API**

Add to `src/Hive.Api/Hive.Api.csproj`:

```xml
  <ItemGroup>
    <ProjectReference Include="..\Hive.Infrastructure\Hive.Infrastructure.csproj" />
  </ItemGroup>
```

- [ ] **Step 4: Register the common bootstrap in the API**

Replace `src/Hive.Api/Program.cs` with:

```csharp
using Hive.Infrastructure.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddHiveBootstrap();

var app = builder.Build();

app.Run();
```

- [ ] **Step 5: Reference infrastructure from the worker**

Add to `src/Hive.Worker/Hive.Worker.csproj`:

```xml
  <ItemGroup>
    <ProjectReference Include="..\Hive.Infrastructure\Hive.Infrastructure.csproj" />
  </ItemGroup>
```

- [ ] **Step 6: Register the common bootstrap in the worker**

Replace `src/Hive.Worker/Program.cs` with:

```csharp
using Hive.Infrastructure.Configuration;

var builder = Host.CreateApplicationBuilder(args);
builder.AddHiveBootstrap();

var host = builder.Build();
host.Run();
```

- [ ] **Step 7: Run the composition and bootstrap tests and verify GREEN**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter "FullyQualifiedName~Executable_references_shared_infrastructure|FullyQualifiedName~Executable_calls_common_bootstrap|FullyQualifiedName~HiveBootstrapTests" --no-restore
```

Expected: all 6 cases pass with 0 failures.

- [ ] **Step 8: Commit executable integration**

```powershell
git add tests/Hive.Tests/SolutionStructureTests.cs src/Hive.Api/Hive.Api.csproj src/Hive.Api/Program.cs src/Hive.Worker/Hive.Worker.csproj src/Hive.Worker/Program.cs
git commit -m "Use common bootstrap in executable hosts"
```

### Task 3: Operational documentation and bible record

**Files:**
- Modify: `docs/configuration.md`
- Modify: `docs/bible.html`

- [ ] **Step 1: Replace the operational configuration reference**

Replace `docs/configuration.md` with:

````markdown
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
````

- [ ] **Step 2: Advance the bible header and changelog to version 0.36**

Change the header version from `0.35` to `0.36`, then add this row after the 0.35 row:

```html
<tr><td>0.36</td><td>2026-06-20</td><td>Registo da implementação de <code>US-F0-01-T05</code>: bootstrap comum de configuração e DI, binding tipado e validação fail-fast das roles em ambos os executáveis</td></tr>
```

- [ ] **Step 3: Add the T05 implementation note to bible section 5.10**

Insert this paragraph immediately after the T04 implementation paragraph:

```html
<p><strong>Implementação de <code>US-F0-01-T05</code>:</strong> o bootstrap comum é exposto por <code>Hive.Infrastructure.Configuration</code> para os builders de ambos os executáveis; faz binding da secção <code>Hive</code> para <code>HiveOptions</code>, regista a validação no options pipeline e usa <code>ValidateOnStart()</code>. As roles são reconhecidas após <code>Trim</code> com comparação case-insensitive, mas os valores ligados não são alterados nem deduplicados. Erros identificam <code>Hive:Node:Roles</code> e os valores ou posições inválidos. A aplicação das roles ao runtime continua reservada a <code>US-F0-01-T06</code>.</p>
```

- [ ] **Step 4: Advance the next-iteration marker**

At the end of `docs/bible.html`, change `Próxima iteração (v0.36)` to `Próxima iteração (v0.37)`.

- [ ] **Step 5: Check documentation consistency**

Run:

```powershell
rg -n "0\.36|US-F0-01-T05|ValidateOnStart|Hive:Node:Roles|US-F0-01-T06" docs/bible.html docs/configuration.md
```

Expected: both documents identify the shared bootstrap, fail-fast role rules, unchanged bound values, and the T06 runtime boundary.

- [ ] **Step 6: Commit documentation and source-of-truth update**

```powershell
git add docs/configuration.md docs/bible.html
git commit -m "Document T05 bootstrap implementation"
```

### Task 4: Full verification and scope audit

**Files:**
- Verify: `src/Hive.Infrastructure/Configuration/*.cs`
- Verify: `src/Hive.Api/Hive.Api.csproj`
- Verify: `src/Hive.Api/Program.cs`
- Verify: `src/Hive.Worker/Hive.Worker.csproj`
- Verify: `src/Hive.Worker/Program.cs`
- Verify: `tests/Hive.Tests/*.cs`
- Verify: `docs/configuration.md`
- Verify: `docs/bible.html`

- [ ] **Step 1: Run all tests**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore
```

Expected: every test passes with 0 failures.

- [ ] **Step 2: Build the complete solution**

Run:

```powershell
dotnet build Hive.sln --no-restore
```

Expected: all six projects build with 0 errors and 0 warnings.

- [ ] **Step 3: Verify the API fails before serving with invalid roles**

Run:

```powershell
$env:Hive__Node__Roles__0 = 'api'
$env:Hive__Node__Roles__1 = ' API '
dotnet run --project src/Hive.Api/Hive.Api.csproj --no-build --no-launch-profile
```

Expected: process exits with `OptionsValidationException`; output identifies `Hive:Node:Roles` and both duplicate values. Remove both environment variables after the check:

```powershell
Remove-Item Env:Hive__Node__Roles__0
Remove-Item Env:Hive__Node__Roles__1
```

- [ ] **Step 4: Verify the worker fails before normal execution with an unknown role**

Run:

```powershell
$env:Hive__Node__Roles__0 = 'scheduler'
dotnet run --project src/Hive.Worker/Hive.Worker.csproj --no-build
```

Expected: process exits with `OptionsValidationException`; output identifies `Hive:Node:Roles[0]`, `scheduler`, and the allowed values. Remove the environment variable after the check:

```powershell
Remove-Item Env:Hive__Node__Roles__0
```

- [ ] **Step 5: Audit the T06 boundary and final diff**

Run:

```powershell
git diff 6d0097b..HEAD -- src tests | rg "ActorSystem|Akka|AddAkka|Cluster|Npgsql"
git status --short
```

Expected: the scope search has no matches, and only the implementation-plan file remains uncommitted if it was intentionally kept outside the task commits.

- [ ] **Step 6: Commit the implementation plan if still uncommitted**

```powershell
git add docs/superpowers/plans/2026-06-20-us-f0-01-t05-common-bootstrap.md
git commit -m "Plan T05 common bootstrap implementation"
```
