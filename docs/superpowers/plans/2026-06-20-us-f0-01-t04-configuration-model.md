# US-F0-01-T04 Configuration Model Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Define the shared HIVE configuration model, executable role defaults, PostgreSQL connection-string contract, environment-variable documentation, and matching bible implementation note for US-F0-01-T04.

**Architecture:** Deployment-facing configuration types live in `Hive.Infrastructure.Configuration`, keeping `Hive.Domain` independent and giving future bootstrap code one reusable contract. The executable JSON files declare safe, credential-free defaults; focused tests verify both the public model and the tracked configuration files without introducing binding, DI, Akka, or PostgreSQL consumers.

**Tech Stack:** .NET 8, C# 12, xUnit 2.5.3, `System.Text.Json`, MSBuild project references, standard ASP.NET Core/Worker `appsettings` conventions.

---

## File Structure

- Create `src/Hive.Infrastructure/Configuration/HiveOptions.cs`: root `Hive` configuration model.
- Create `src/Hive.Infrastructure/Configuration/NodeOptions.cs`: node roles model.
- Create `src/Hive.Infrastructure/Configuration/NodeRoleNames.cs`: canonical role names.
- Create `src/Hive.Infrastructure/Configuration/ConnectionStringNames.cs`: canonical PostgreSQL connection-string name.
- Modify `tests/Hive.Tests/Hive.Tests.csproj`: reference `Hive.Infrastructure` for behavioral contract tests.
- Create `tests/Hive.Tests/ConfigurationModelTests.cs`: verify typed configuration names, roles, and defaults.
- Create `tests/Hive.Tests/ConfigurationFileTests.cs`: verify executable JSON contracts and absence of tracked credentials.
- Modify `src/Hive.Api/appsettings.json`: add the API base role and empty PostgreSQL key.
- Modify `src/Hive.Api/appsettings.Development.json`: add the explicit all-in-one roles.
- Modify `src/Hive.Worker/appsettings.json`: add backend base roles and empty PostgreSQL key.
- Leave `src/Hive.Worker/appsettings.Development.json` without a `Hive` override so it inherits base roles.
- Create `docs/configuration.md`: document keys, defaults, environment variables, and current scope.
- Modify `docs/bible.html`: record the completed T04 implementation decisions as version 0.34.

### Task 1: Shared typed configuration contract

**Files:**
- Modify: `tests/Hive.Tests/Hive.Tests.csproj`
- Create: `tests/Hive.Tests/ConfigurationModelTests.cs`
- Create: `src/Hive.Infrastructure/Configuration/HiveOptions.cs`
- Create: `src/Hive.Infrastructure/Configuration/NodeOptions.cs`
- Create: `src/Hive.Infrastructure/Configuration/NodeRoleNames.cs`
- Create: `src/Hive.Infrastructure/Configuration/ConnectionStringNames.cs`

- [ ] **Step 1: Add the infrastructure test reference**

Add this item group to `tests/Hive.Tests/Hive.Tests.csproj`:

```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\Hive.Infrastructure\Hive.Infrastructure.csproj" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing model contract tests**

Create `tests/Hive.Tests/ConfigurationModelTests.cs`:

```csharp
using Hive.Infrastructure.Configuration;

namespace Hive.Tests;

public sealed class ConfigurationModelTests
{
    [Fact]
    public void Configuration_contract_uses_canonical_section_names()
    {
        Assert.Equal("Hive", HiveOptions.SectionName);
        Assert.Equal("PostgreSql", ConnectionStringNames.PostgreSql);
    }

    [Fact]
    public void Node_roles_match_the_canonical_F0_values()
    {
        var expected = new[] { "agents", "gateway", "connectors", "api" };

        Assert.Equal(expected, NodeRoleNames.All);
        Assert.Equal(expected.Length, NodeRoleNames.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("agents", NodeRoleNames.Agents);
        Assert.Equal("gateway", NodeRoleNames.Gateway);
        Assert.Equal("connectors", NodeRoleNames.Connectors);
        Assert.Equal("api", NodeRoleNames.Api);
    }

    [Fact]
    public void Options_have_non_null_empty_defaults()
    {
        var options = new HiveOptions();

        Assert.NotNull(options.Node);
        Assert.NotNull(options.Node.Roles);
        Assert.Empty(options.Node.Roles);
    }
}
```

- [ ] **Step 3: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter FullyQualifiedName~ConfigurationModelTests
```

Expected: build fails because `Hive.Infrastructure.Configuration` and its public types do not exist.

- [ ] **Step 4: Add the minimal root and node options models**

Create `src/Hive.Infrastructure/Configuration/HiveOptions.cs`:

```csharp
namespace Hive.Infrastructure.Configuration;

public sealed class HiveOptions
{
    public const string SectionName = "Hive";

    public NodeOptions Node { get; set; } = new();
}
```

Create `src/Hive.Infrastructure/Configuration/NodeOptions.cs`:

```csharp
namespace Hive.Infrastructure.Configuration;

public sealed class NodeOptions
{
    public string[] Roles { get; set; } = [];
}
```

- [ ] **Step 5: Add the canonical role and connection-string names**

Create `src/Hive.Infrastructure/Configuration/NodeRoleNames.cs`:

```csharp
namespace Hive.Infrastructure.Configuration;

public static class NodeRoleNames
{
    public const string Agents = "agents";
    public const string Gateway = "gateway";
    public const string Connectors = "connectors";
    public const string Api = "api";

    public static IReadOnlyList<string> All { get; } =
        Array.AsReadOnly(new[] { Agents, Gateway, Connectors, Api });
}
```

Create `src/Hive.Infrastructure/Configuration/ConnectionStringNames.cs`:

```csharp
namespace Hive.Infrastructure.Configuration;

public static class ConnectionStringNames
{
    public const string PostgreSql = "PostgreSql";
}
```

- [ ] **Step 6: Run the focused tests and verify GREEN**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter FullyQualifiedName~ConfigurationModelTests --no-restore
```

Expected: 3 tests pass with 0 failures.

- [ ] **Step 7: Commit the typed contract**

```powershell
git add tests/Hive.Tests/Hive.Tests.csproj tests/Hive.Tests/ConfigurationModelTests.cs src/Hive.Infrastructure/Configuration
git commit -m "Add shared configuration contract"
```

### Task 2: Executable JSON configuration contracts

**Files:**
- Create: `tests/Hive.Tests/ConfigurationFileTests.cs`
- Modify: `src/Hive.Api/appsettings.json`
- Modify: `src/Hive.Api/appsettings.Development.json`
- Modify: `src/Hive.Worker/appsettings.json`
- Verify unchanged: `src/Hive.Worker/appsettings.Development.json`

- [ ] **Step 1: Write the failing executable configuration tests**

Create `tests/Hive.Tests/ConfigurationFileTests.cs`:

```csharp
using System.Text.Json;

namespace Hive.Tests;

public sealed class ConfigurationFileTests
{
    public static TheoryData<string, string[]> BaseRoleDefaults => new()
    {
        { "src/Hive.Api/appsettings.json", new[] { "api" } },
        { "src/Hive.Worker/appsettings.json", new[] { "agents", "gateway", "connectors" } },
    };

    [Theory]
    [MemberData(nameof(BaseRoleDefaults))]
    public void Base_configuration_declares_expected_roles(string relativePath, string[] expectedRoles)
    {
        using var document = Load(relativePath);

        var roles = document.RootElement
            .GetProperty("Hive")
            .GetProperty("Node")
            .GetProperty("Roles")
            .EnumerateArray()
            .Select(role => role.GetString())
            .ToArray();

        Assert.Equal(expectedRoles, roles);
    }

    [Theory]
    [InlineData("src/Hive.Api/appsettings.json")]
    [InlineData("src/Hive.Worker/appsettings.json")]
    public void Base_configuration_declares_empty_PostgreSql_contract(string relativePath)
    {
        using var document = Load(relativePath);

        var connectionString = document.RootElement
            .GetProperty("ConnectionStrings")
            .GetProperty("PostgreSql")
            .GetString();

        Assert.Equal(string.Empty, connectionString);
    }

    [Fact]
    public void Api_development_configuration_declares_all_in_one_roles()
    {
        using var document = Load("src/Hive.Api/appsettings.Development.json");

        var roles = document.RootElement
            .GetProperty("Hive")
            .GetProperty("Node")
            .GetProperty("Roles")
            .EnumerateArray()
            .Select(role => role.GetString())
            .ToArray();

        Assert.Equal(new[] { "api", "agents", "gateway", "connectors" }, roles);
    }

    [Fact]
    public void Worker_development_configuration_inherits_base_roles()
    {
        using var document = Load("src/Hive.Worker/appsettings.Development.json");

        Assert.False(document.RootElement.TryGetProperty("Hive", out _));
    }

    private static JsonDocument Load(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot, relativePath);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string RepositoryRoot => FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the Hive repository root.");
    }
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter FullyQualifiedName~ConfigurationFileTests --no-restore
```

Expected: 5 tests fail because the tracked JSON files do not yet contain `Hive` roles or `ConnectionStrings:PostgreSql`.

- [ ] **Step 3: Add the API base configuration**

Replace `src/Hive.Api/appsettings.json` with:

```json
{
  "ConnectionStrings": {
    "PostgreSql": ""
  },
  "Hive": {
    "Node": {
      "Roles": ["api"]
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

- [ ] **Step 4: Add the API all-in-one development override**

Replace `src/Hive.Api/appsettings.Development.json` with:

```json
{
  "Hive": {
    "Node": {
      "Roles": ["api", "agents", "gateway", "connectors"]
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

- [ ] **Step 5: Add the worker base configuration**

Replace `src/Hive.Worker/appsettings.json` with:

```json
{
  "ConnectionStrings": {
    "PostgreSql": ""
  },
  "Hive": {
    "Node": {
      "Roles": ["agents", "gateway", "connectors"]
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  }
}
```

- [ ] **Step 6: Run the focused tests and verify GREEN**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter FullyQualifiedName~ConfigurationFileTests --no-restore
```

Expected: 6 tests pass with 0 failures.

- [ ] **Step 7: Commit executable defaults**

```powershell
git add tests/Hive.Tests/ConfigurationFileTests.cs src/Hive.Api/appsettings.json src/Hive.Api/appsettings.Development.json src/Hive.Worker/appsettings.json
git commit -m "Define executable configuration defaults"
```

### Task 3: Operator documentation and bible update

**Files:**
- Create: `docs/configuration.md`
- Modify: `docs/bible.html`

- [ ] **Step 1: Add the operational configuration reference**

Create `docs/configuration.md`:

```markdown
# HIVE Configuration

The canonical product and architecture decisions remain in `docs/bible.html`. This document is the operational reference for the configuration contract introduced by US-F0-01-T04.

## Sources and precedence

The executable projects use the standard .NET configuration hierarchy. Base `appsettings.json` values are overridden by `appsettings.{Environment}.json` and then by environment variables when the common bootstrap is implemented in US-F0-01-T05.

T04 defines the data contract only. It does not bind or validate options, register dependency injection services, configure PostgreSQL consumers, or apply roles to Akka.Cluster.

## PostgreSQL

Both executables declare an empty `ConnectionStrings:PostgreSql` value. Supply an operational value outside tracked source files:

```text
ConnectionStrings__PostgreSql=Host=localhost;Port=5432;Database=hive;Username={user};Password={secret}
```

The same F0 database serves journal/snapshots, registry, audit log, read models, budgets, and scheduler idempotency. Each subsystem retains ownership of its schemas, tables, and migrations.

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

Startup rejection of missing or unknown roles belongs to US-F0-01-T05. Runtime workload placement belongs to US-F0-01-T06.
```

- [ ] **Step 2: Advance the bible version and changelog**

In `docs/bible.html`, change the header version from `0.33` to `0.34` and append this row after version 0.33:

```html
<tr><td>0.34</td><td>2026-06-20</td><td>Registo da implementação de <code>US-F0-01-T04</code>: modelo partilhado em <code>Hive.Infrastructure</code>, defaults seguros por executável, override local all-in-one e contrato PostgreSQL sem credenciais versionadas</td></tr>
```

- [ ] **Step 3: Add the T04 implementation note to bible §5.10**

Insert this paragraph after the existing paragraph that assigns binding to T05 and runtime application to T06:

```html
<p><strong>Implementação de <code>US-F0-01-T04</code>:</strong> o modelo tipado partilhado fica em <code>Hive.Infrastructure.Configuration</code>; os dois <code>appsettings.json</code> base declaram <code>ConnectionStrings:PostgreSql</code> com valor vazio para não versionar credenciais; <code>Hive.Api/appsettings.Development.json</code> materializa o override all-in-one; e <code>Hive.Worker/appsettings.Development.json</code> herda as roles base sem as redefinir. Binding, validação de arranque e registo em DI continuam reservados a <code>US-F0-01-T05</code>.</p>
```

- [ ] **Step 4: Check documentation consistency**

Run:

```powershell
rg -n "0\.34|Hive\.Infrastructure\.Configuration|ConnectionStrings__PostgreSql|HIVE__NODE__ROLES__0|US-F0-01-T05" docs/bible.html docs/configuration.md
```

Expected: both documents expose the same connection-string name, role environment-variable hierarchy, model ownership, and T05 scope boundary.

- [ ] **Step 5: Commit documentation and source-of-truth update**

```powershell
git add docs/configuration.md docs/bible.html
git commit -m "Document T04 configuration implementation"
```

### Task 4: Full verification and scope audit

**Files:**
- Verify: `src/Hive.Infrastructure/Configuration/*.cs`
- Verify: `src/Hive.Api/appsettings*.json`
- Verify: `src/Hive.Worker/appsettings*.json`
- Verify: `tests/Hive.Tests/*.cs`
- Verify: `docs/configuration.md`
- Verify: `docs/bible.html`

- [ ] **Step 1: Run all tests**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore
```

Expected: all tests pass with 0 failures.

- [ ] **Step 2: Build the complete solution**

Run:

```powershell
dotnet build Hive.sln --no-restore
```

Expected: build succeeds with 0 errors.

- [ ] **Step 3: Validate JSON syntax independently**

Run:

```powershell
Get-Content src/Hive.Api/appsettings.json | ConvertFrom-Json | Out-Null
Get-Content src/Hive.Api/appsettings.Development.json | ConvertFrom-Json | Out-Null
Get-Content src/Hive.Worker/appsettings.json | ConvertFrom-Json | Out-Null
Get-Content src/Hive.Worker/appsettings.Development.json | ConvertFrom-Json | Out-Null
```

Expected: all four commands exit without a JSON parsing error.

- [ ] **Step 4: Audit forbidden scope additions**

Run:

```powershell
git diff HEAD~3 -- src tests | rg "AddOptions|Configure<|ValidateOnStart|AddSingleton|ActorSystem|Akka|Npgsql"
```

Expected: no matches, confirming T04 did not add DI binding, startup validation, Akka runtime configuration, or PostgreSQL consumers.

- [ ] **Step 5: Check the final diff**

Run:

```powershell
git status --short
git log -4 --oneline
```

Expected: the worktree is clean and the three implementation commits appear after the design and plan documentation history.
