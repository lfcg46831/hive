# US-F0-05-T11 Internal Registry API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose the PostgreSQL organization registry through the five internal read-only HTTP queries fixed in `docs/bible.html` for US-F0-05-T11.

**Architecture:** `Hive.Api` owns a Minimal API route group and response DTOs that are independent from the registry persistence records. An API-scoped lifetime holder creates a PostgreSQL-backed `IOrganizationRegistryReader` only when `ConnectionStrings:PostgreSql` exists; handlers return Problem Details for invalid or absent resources and map immutable snapshots into deterministically ordered responses.

**Tech Stack:** .NET 8, ASP.NET Core Minimal APIs, System.Text.Json, Npgsql, xUnit, ASP.NET Core TestHost, Testcontainers PostgreSQL 16

---

### Task 1: Add the API registry reader lifetime and unavailable response

**Files:**
- Create: `src/Hive.Api/Organization/OrganizationRegistryApiReader.cs`
- Create: `src/Hive.Api/Organization/OrganizationRegistryApiServiceCollectionExtensions.cs`
- Create: `src/Hive.Api/Organization/OrganizationRegistryEndpointExtensions.cs`
- Create: `tests/Hive.Tests/OrganizationRegistryEndpointTests.cs`

- [ ] **Step 1: Write the failing unavailable-registry HTTP test**

Create an app with TestServer, an empty in-memory configuration, `AddHiveOrganizationRegistryApi()`, and `MapHiveOrganizationRegistryApi()`. Request the organization endpoint and assert the exact boundary behavior:

```csharp
[Fact]
public async Task Organization_query_returns_problem_details_when_PostgreSql_is_not_configured()
{
    await using var app = BuildApp(connectionString: null);
    await app.StartAsync();
    using var client = app.GetTestClient();

    var response = await client.GetAsync(
        $"{OrganizationRegistryEndpointExtensions.BasePath}/acme-delivery");
    var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

    Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    Assert.Equal("Organization registry unavailable", problem!.Title);
    Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.Status);
}
```

The helper must register only this feature, so the test proves that the API controls its own data-source lifetime:

```csharp
private static WebApplication BuildApp(string? connectionString)
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseTestServer();
    builder.Configuration.Sources.Clear();
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:PostgreSql"] = connectionString,
    });
    builder.Services.AddHiveOrganizationRegistryApi();

    var app = builder.Build();
    app.MapHiveOrganizationRegistryApi();
    return app;
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter FullyQualifiedName~OrganizationRegistryEndpointTests.Organization_query_returns_problem_details_when_PostgreSql_is_not_configured`

Expected: compilation fails because the organization API registration and endpoint extensions do not exist.

- [ ] **Step 3: Implement the API-owned reader lifetime**

`OrganizationRegistryApiReader` is an internal singleton and async-disposable owner of the optional data source:

```csharp
internal sealed class OrganizationRegistryApiReader : IAsyncDisposable
{
    private readonly NpgsqlDataSource? _dataSource;

    public OrganizationRegistryApiReader(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        Reader = new PostgreSqlOrganizationRegistry(_dataSource);
    }

    public IOrganizationRegistryReader? Reader { get; }

    public ValueTask DisposeAsync() =>
        _dataSource is null ? ValueTask.CompletedTask : _dataSource.DisposeAsync();
}
```

Register it through a public extension used by both `Program` and tests:

```csharp
public static IServiceCollection AddHiveOrganizationRegistryApi(this IServiceCollection services)
{
    ArgumentNullException.ThrowIfNull(services);
    services.AddSingleton<OrganizationRegistryApiReader>();
    return services;
}
```

- [ ] **Step 4: Map the base route with the minimal 503 behavior**

Create the route group and only the base endpoint needed by the first test:

```csharp
public const string BasePath = "/internal/organizations";

public static IEndpointRouteBuilder MapHiveOrganizationRegistryApi(
    this IEndpointRouteBuilder endpoints)
{
    ArgumentNullException.ThrowIfNull(endpoints);
    endpoints.MapGroup(BasePath).MapGet("/{organizationId}", GetOrganizationAsync);
    return endpoints;
}

private static async Task<IResult> GetOrganizationAsync(
    string organizationId,
    OrganizationRegistryApiReader source,
    CancellationToken cancellationToken)
{
    if (source.Reader is null)
    {
        return TypedResults.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Organization registry unavailable");
    }

    var snapshot = await source.Reader.FindSnapshotAsync(
        OrganizationId.From(organizationId),
        cancellationToken);
    return snapshot is null
        ? TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Organization not found")
        : TypedResults.Json(new { snapshot.OrganizationId.Value });
}
```

- [ ] **Step 5: Run the focused test and verify GREEN**

Run the command from Step 2.

Expected: one passing test with HTTP 503 and `application/problem+json`.

### Task 2: Expose organization, unit, and position projections

**Files:**
- Create: `src/Hive.Api/Organization/OrganizationRegistryResponses.cs`
- Create: `src/Hive.Api/Organization/OrganizationRegistryResponseMapper.cs`
- Modify: `src/Hive.Api/Organization/OrganizationRegistryEndpointExtensions.cs`
- Modify: `tests/Hive.Tests/OrganizationRegistryEndpointTests.cs`

- [ ] **Step 1: Add PostgreSQL seed support and failing success-path tests**

Put the test class in `PostgreSqlCollection` and add `SeedRegistryAsync()` that resets the schema, runs `PostgreSqlOrganizationRegistryMigrator`, parses `config/organizations/acme-delivery/organization.yaml`, and imports it through `OrganizationConfigurationImporter`. Add three tests, running each once before production changes:

```csharp
[Fact]
public async Task Organization_query_returns_header_and_materialization_metadata()
{
    var snapshot = await SeedRegistryAsync();
    await using var app = BuildApp(_fixture.ConnectionString);
    await app.StartAsync();
    using var client = app.GetTestClient();

    var json = await client.GetFromJsonAsync<JsonElement>(
        $"{OrganizationRegistryEndpointExtensions.BasePath}/acme-delivery");

    Assert.Equal("acme-delivery", json.GetProperty("id").GetString());
    Assert.Equal("ACME Engenharia/Delivery", json.GetProperty("name").GetString());
    Assert.Equal("raiz", json.GetProperty("rootUnitId").GetString());
    Assert.Equal(snapshot.Version, json.GetProperty("version").GetInt64());
    Assert.Equal(snapshot.Fingerprint, json.GetProperty("fingerprint").GetString());
    Assert.Equal("human", json.GetProperty("owner").GetProperty("type").GetString());
    Assert.Equal(2, json.GetProperty("prompts").GetArrayLength());
}

[Fact]
public async Task Unit_query_returns_units_ordered_by_identifier()
{
    await SeedRegistryAsync();
    await using var app = BuildApp(_fixture.ConnectionString);
    await app.StartAsync();
    using var client = app.GetTestClient();

    var json = await client.GetFromJsonAsync<JsonElement>(
        $"{OrganizationRegistryEndpointExtensions.BasePath}/acme-delivery/units");

    Assert.Equal(
        new[] { "engenharia", "raiz" },
        json.GetProperty("units").EnumerateArray()
            .Select(unit => unit.GetProperty("id").GetString()));
}

[Fact]
public async Task Position_query_returns_positions_ordered_by_identifier()
{
    await SeedRegistryAsync();
    await using var app = BuildApp(_fixture.ConnectionString);
    await app.StartAsync();
    using var client = app.GetTestClient();

    var json = await client.GetFromJsonAsync<JsonElement>(
        $"{OrganizationRegistryEndpointExtensions.BasePath}/acme-delivery/positions");

    Assert.Equal(
        new[] { "ceo", "delivery-lead" },
        json.GetProperty("positions").EnumerateArray()
            .Select(position => position.GetProperty("id").GetString()));
}
```

- [ ] **Step 2: Run the three tests and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter "FullyQualifiedName~OrganizationRegistryEndpointTests.Organization_query_returns_header|FullyQualifiedName~OrganizationRegistryEndpointTests.Unit_query_returns|FullyQualifiedName~OrganizationRegistryEndpointTests.Position_query_returns"`

Expected: the organization payload lacks the defined DTO fields and the two collection routes return 404.

- [ ] **Step 3: Define independent response DTOs**

Create immutable API records; none may expose `RegistryEntry<T>` or configuration-domain objects directly:

```csharp
public sealed record OrganizationResponse(
    string Id,
    string? Name,
    string RootUnitId,
    OwnerResponse Owner,
    IReadOnlyList<PromptResponse> Prompts,
    long Version,
    string Fingerprint,
    DateTimeOffset ImportedAt,
    DateTimeOffset UpdatedAt);

public sealed record OwnerResponse(string Type, string Ref);
public sealed record PromptResponse(string Id, string Path);
public sealed record UnitsResponse(IReadOnlyList<UnitResponse> Units);
public sealed record UnitResponse(
    string Id,
    string? Name,
    string? ParentId,
    string LeadershipPositionId,
    DateTimeOffset UpdatedAt);
public sealed record PositionsResponse(IReadOnlyList<PositionResponse> Positions);
public sealed record PositionResponse(
    string Id,
    string? Name,
    string UnitId,
    string? ReportsToPositionId,
    string? Timezone,
    DateTimeOffset UpdatedAt);
```

- [ ] **Step 4: Implement deterministic snapshot mapping**

The pure mapper uses ordinal ordering and explicit wire spellings:

```csharp
public static OrganizationResponse MapOrganization(OrganizationRegistrySnapshot snapshot) =>
    new(
        snapshot.Organization.Value.Id.Value,
        snapshot.Organization.Value.Name,
        snapshot.Organization.Value.RootUnit.Value,
        new OwnerResponse(
            snapshot.Organization.Value.Owner.Type switch
            {
                OwnerType.Human => "human",
                OwnerType.Group => "group",
                _ => throw new InvalidOperationException("Unknown organization owner type."),
            },
            snapshot.Organization.Value.Owner.Ref),
        snapshot.Organization.Value.Prompts
            .OrderBy(prompt => prompt.Id, StringComparer.Ordinal)
            .Select(prompt => new PromptResponse(prompt.Id, prompt.Path))
            .ToArray(),
        snapshot.Version,
        snapshot.Fingerprint,
        snapshot.ImportedAt,
        snapshot.Organization.UpdatedAt);

public static UnitsResponse MapUnits(OrganizationRegistrySnapshot snapshot) =>
    new(snapshot.Units.Values
        .OrderBy(entry => entry.Value.Id.Value, StringComparer.Ordinal)
        .Select(entry => new UnitResponse(
            entry.Value.Id.Value,
            entry.Value.Name,
            entry.Value.Parent?.Value,
            entry.Value.Leadership.Value,
            entry.UpdatedAt))
        .ToArray());

public static PositionsResponse MapPositions(OrganizationRegistrySnapshot snapshot) =>
    new(snapshot.Positions.Values
        .OrderBy(entry => entry.Value.Id.Value, StringComparer.Ordinal)
        .Select(MapPosition)
        .ToArray());
```

- [ ] **Step 5: Map the two collection routes and return the organization DTO**

Add `/units` and `/positions` to the group. Each handler validates the organization ID, loads one snapshot through `IOrganizationRegistryReader`, returns 404 Problem Details when absent, and calls the matching mapper. Replace the anonymous base response with `OrganizationRegistryResponseMapper.MapOrganization(snapshot)`.

- [ ] **Step 6: Run the three tests and verify GREEN**

Run the command from Step 2.

Expected: three passing tests with the exact metadata and ordinal identifier ordering.

### Task 3: Expose command relations and complete position configuration

**Files:**
- Modify: `src/Hive.Api/Organization/OrganizationRegistryResponses.cs`
- Modify: `src/Hive.Api/Organization/OrganizationRegistryResponseMapper.cs`
- Modify: `src/Hive.Api/Organization/OrganizationRegistryEndpointExtensions.cs`
- Modify: `tests/Hive.Tests/OrganizationRegistryEndpointTests.cs`

- [ ] **Step 1: Write failing command-relations and position-configuration tests**

Add tests for the remaining successful contracts:

```csharp
[Fact]
public async Task Command_relations_query_returns_root_owner_and_ordered_edges()
{
    await SeedRegistryAsync();
    await using var app = BuildApp(_fixture.ConnectionString);
    await app.StartAsync();
    using var client = app.GetTestClient();

    var json = await client.GetFromJsonAsync<JsonElement>(
        $"{OrganizationRegistryEndpointExtensions.BasePath}/acme-delivery/command-relations");

    Assert.Equal("ceo", json.GetProperty("rootUnitLeadershipPositionId").GetString());
    Assert.Equal("owner@acme.pt", json.GetProperty("owner").GetProperty("ref").GetString());
    var relations = json.GetProperty("relations").EnumerateArray().ToArray();
    Assert.Equal(new[] { "ceo", "delivery-lead" },
        relations.Select(item => item.GetProperty("positionId").GetString()));
    Assert.Equal("ceo", relations[1].GetProperty("reportsToPositionId").GetString());
    Assert.Equal("delivery-lead",
        Assert.Single(relations[0].GetProperty("directSubordinatePositionIds")
            .EnumerateArray()).GetString());
}

[Fact]
public async Task Position_configuration_query_aggregates_all_position_projections()
{
    await SeedRegistryAsync();
    await using var app = BuildApp(_fixture.ConnectionString);
    await app.StartAsync();
    using var client = app.GetTestClient();

    var json = await client.GetFromJsonAsync<JsonElement>(
        $"{OrganizationRegistryEndpointExtensions.BasePath}/acme-delivery/positions/delivery-lead/configuration");

    Assert.Equal("delivery-lead", json.GetProperty("position").GetProperty("id").GetString());
    Assert.Equal("ai-agent", json.GetProperty("occupant").GetProperty("type").GetString());
    Assert.Equal("stub", json.GetProperty("occupant").GetProperty("ai").GetProperty("provider").GetString());
    Assert.Equal("triagem-de-bugs",
        Assert.Single(json.GetProperty("authority").GetProperty("canDecide").EnumerateArray()).GetString());
    Assert.Equal("relatorio-diario",
        Assert.Single(json.GetProperty("schedules").EnumerateArray()).GetProperty("id").GetString());
}
```

- [ ] **Step 2: Run the two tests and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter "FullyQualifiedName~OrganizationRegistryEndpointTests.Command_relations_query|FullyQualifiedName~OrganizationRegistryEndpointTests.Position_configuration_query"`

Expected: both routes return 404 because they are not mapped.

- [ ] **Step 3: Add the relation and configuration DTO graph**

Add these API-owned contracts:

```csharp
public sealed record CommandRelationsResponse(
    OwnerResponse Owner,
    string RootUnitLeadershipPositionId,
    IReadOnlyList<CommandRelationResponse> Relations);
public sealed record CommandRelationResponse(
    string PositionId,
    string UnitId,
    string? ReportsToPositionId,
    IReadOnlyList<string> DirectSubordinatePositionIds);
public sealed record PositionConfigurationResponse(
    PositionResponse Position,
    OccupantResponse Occupant,
    AuthorityResponse Authority,
    IReadOnlyList<ScheduleResponse> Schedules);
public sealed record OccupantResponse(
    string Type,
    string? IdentityPromptRef,
    AiResponse? Ai,
    WorkingHoursResponse? WorkingHours,
    IReadOnlyList<SubscriptionResponse> Subscriptions,
    IReadOnlyList<ToolResponse> Tools,
    DateTimeOffset UpdatedAt);
public sealed record AiResponse(
    string Provider,
    string Model,
    double? Temperature,
    int? MaxTokens,
    string? Processing,
    string? BatchWindow,
    IReadOnlyList<AiFallbackResponse> Fallback,
    BudgetResponse? Budget);
public sealed record AiFallbackResponse(string Provider, string Model);
public sealed record BudgetResponse(
    decimal? ReactiveMaxEurPerDay,
    decimal? ProactiveMaxEurPerDay,
    decimal? TotalMaxEurPerDay,
    int? MaxCallsPerHour);
public sealed record WorkingHoursResponse(string Start, string End);
public sealed record SubscriptionResponse(string Event, string Within);
public sealed record ToolResponse(string Connector, IReadOnlyList<string> Scope);
public sealed record AuthorityResponse(
    IReadOnlyList<string> CanDecide,
    IReadOnlyList<string> MustEscalate,
    IReadOnlyList<string> RequiresHumanApproval,
    DateTimeOffset UpdatedAt);
public sealed record ScheduleResponse(
    string Id,
    string Cron,
    string Instruction,
    DateTimeOffset UpdatedAt);
```

- [ ] **Step 4: Implement complete mapping**

For command relations, iterate positions by ordinal ID and derive direct subordinates from `snapshot.Relations.Value`, sorting those IDs ordinally. For position configuration, require the position key in `Positions`, `Occupants`, and `Authorities`, filter schedules by `PositionId`, sort by `ScheduleId`, map `OccupantType.AiAgent` to `ai-agent` and `OccupantType.Human` to `human`, and copy nested AI, fallback, budget, working-hours, subscription, tool, authority, and schedule values into their API DTOs. Preserve declared order for semantic leaf lists such as fallback, subscriptions, scopes, and authority actions; only identity-bearing registry collections are key-sorted.

- [ ] **Step 5: Map the two remaining routes**

Add:

```csharp
group.MapGet("/{organizationId}/command-relations", GetCommandRelationsAsync);
group.MapGet(
    "/{organizationId}/positions/{positionId}/configuration",
    GetPositionConfigurationAsync);
```

Both load a single organization snapshot. The configuration handler parses `PositionId`, returns position-not-found Problem Details if `snapshot.Positions` lacks the key, and then maps the aggregate.

- [ ] **Step 6: Run the two tests and verify GREEN**

Run the command from Step 2.

Expected: two passing tests proving root/owner/edge semantics and full aggregation.

- [ ] **Step 7: Add boundary tests one at a time and verify RED before each fix**

Add and run these cases separately:

1. Encoded whitespace in `organizationId` returns 400, title `Invalid organization identifier`, and Problem Details content type.
2. A valid absent organization returns 404, title `Organization not found`.
3. Encoded leading whitespace in `positionId` returns 400, title `Invalid position identifier`.
4. A valid absent position under an existing organization returns 404, title `Position not found`.

Implement a shared ID parsing helper that catches only `ArgumentException` from `OrganizationId.From`/`PositionId.From`. Do not catch cancellation, Npgsql, or unexpected failures. Implement shared Problem Details factories for 400, 404, and 503 without exception messages or database details.

- [ ] **Step 8: Run all endpoint tests and verify GREEN**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter FullyQualifiedName~OrganizationRegistryEndpointTests`

Expected: all internal registry endpoint tests pass against PostgreSQL 16.

### Task 4: Wire the feature into Hive.Api and verify the task

**Files:**
- Modify: `src/Hive.Api/Program.cs`
- Modify: `tests/Hive.Tests/CompositionTests.cs`
- Verify: `docs/bible.html`

- [ ] **Step 1: Write the failing entry-point route test**

Build the real API entry point and inspect its route endpoints:

```csharp
[Fact]
public async Task Api_entry_point_maps_internal_organization_registry_routes()
{
    await using var app = global::Hive.Api.Program.Build(CreateApiArgs(
        roles: [NodeRoleNames.Api],
        includePostgreSql: true));

    var routes = app.DataSources
        .SelectMany(source => source.Endpoints)
        .OfType<RouteEndpoint>()
        .Select(endpoint => endpoint.RoutePattern.RawText)
        .ToArray();

    Assert.Contains("/internal/organizations/{organizationId}", routes);
    Assert.Contains("/internal/organizations/{organizationId}/units", routes);
    Assert.Contains("/internal/organizations/{organizationId}/positions", routes);
    Assert.Contains("/internal/organizations/{organizationId}/command-relations", routes);
    Assert.Contains(
        "/internal/organizations/{organizationId}/positions/{positionId}/configuration",
        routes);
}
```

- [ ] **Step 2: Run the entry-point test and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter FullyQualifiedName~CompositionTests.Api_entry_point_maps_internal_organization_registry_routes`

Expected: route assertions fail because `Program.Build` does not register or map the feature.

- [ ] **Step 3: Register and map the API feature**

Update `Program.Build` after the common/actor registrations and after app construction:

```csharp
builder.Services.AddHiveOrganizationRegistryApi();

var app = builder.Build();
app.MapHiveDiagnostics();
app.MapHiveOrganizationRegistryApi();
```

- [ ] **Step 4: Run the entry-point and endpoint suites**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --filter "FullyQualifiedName~CompositionTests|FullyQualifiedName~OrganizationRegistryEndpointTests"
```

Expected: all composition and internal-registry API tests pass.

- [ ] **Step 5: Verify bible integrity**

Run:

```powershell
git diff --check -- docs/bible.html
git diff -- docs/bible.html
Get-Content docs/bible.html -Tail 1
(Get-Content docs/bible.html).Count
```

Expected: only the approved T11 contract and version metadata changed, the final line is `</html>`, and the line count remains 2,862 or grows only through intentional surgical additions.

- [ ] **Step 6: Run fresh full verification**

Run:

```powershell
dotnet test Hive.sln --no-restore
dotnet build Hive.sln --no-restore
git diff --check
git status --short
```

Expected: all tests pass, build exits 0, no whitespace errors are reported, and only T11 files plus the pre-existing untracked `docs/analise-arquitetura-2026-06.md` appear.

- [ ] **Step 7: Review the implementation against the bible contract**

Confirm from tests and code that all five routes exist only in `Hive.Api`; reads go through PostgreSQL-backed `IOrganizationRegistryReader`; DTOs do not leak persistence records; organization metadata, units, positions, command relations, and complete position configuration are represented; stable-key collections are ordered; 400/404/503 errors use Problem Details; no write/import endpoint exists; and no authentication mechanism was added.

Final commit-summary suggestion required by `AGENTS.md`:

```text
feat(api): expose the internal read-only organization registry API
```
