using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Api.Organization;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Hive.Tests.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationRegistryEndpointTests(PostgreSqlFixture fixture)
{
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

    [Theory]
    [InlineData("")]
    [InlineData("/units")]
    [InlineData("/positions")]
    [InlineData("/command-relations")]
    [InlineData("/positions/delivery-lead/configuration")]
    public async Task All_queries_return_problem_details_for_invalid_organization_identifier(
        string suffix)
    {
        await using var app = BuildApp(connectionString: null);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(
            $"{OrganizationRegistryEndpointExtensions.BasePath}/%20acme-delivery{suffix}");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Invalid organization identifier", problem!.Title);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/units")]
    [InlineData("/positions")]
    [InlineData("/command-relations")]
    [InlineData("/positions/delivery-lead/configuration")]
    public async Task All_queries_return_problem_details_when_organization_does_not_exist(
        string suffix)
    {
        await PrepareEmptyRegistryAsync();
        await using var app = BuildApp(fixture.ConnectionString);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(
            $"{OrganizationRegistryEndpointExtensions.BasePath}/missing-organization{suffix}");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Organization not found", problem!.Title);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
    }

    [Fact]
    public async Task Position_configuration_returns_problem_details_for_invalid_position_identifier()
    {
        await using var app = BuildApp(connectionString: null);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(
            $"{OrganizationRegistryEndpointExtensions.BasePath}/acme-delivery/positions/%20delivery-lead/configuration");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Invalid position identifier", problem!.Title);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
    }

    [Fact]
    public async Task Position_configuration_returns_problem_details_when_position_does_not_exist()
    {
        await SeedRegistryAsync();
        await using var app = BuildApp(fixture.ConnectionString);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(
            $"{OrganizationRegistryEndpointExtensions.BasePath}/acme-delivery/positions/missing-position/configuration");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Position not found", problem!.Title);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
    }

    [Fact]
    public async Task Organization_query_returns_header_and_materialization_metadata()
    {
        var snapshot = await SeedRegistryAsync();
        await using var app = BuildApp(fixture.ConnectionString);
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
        await using var app = BuildApp(fixture.ConnectionString);
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
        await using var app = BuildApp(fixture.ConnectionString);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var json = await client.GetFromJsonAsync<JsonElement>(
            $"{OrganizationRegistryEndpointExtensions.BasePath}/acme-delivery/positions");

        Assert.Equal(
            new[] { "ceo", "delivery-lead" },
            json.GetProperty("positions").EnumerateArray()
                .Select(position => position.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Command_relations_query_returns_root_owner_and_ordered_edges()
    {
        await SeedRegistryAsync();
        await using var app = BuildApp(fixture.ConnectionString);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var json = await client.GetFromJsonAsync<JsonElement>(
            $"{OrganizationRegistryEndpointExtensions.BasePath}/acme-delivery/command-relations");

        Assert.Equal("ceo", json.GetProperty("rootUnitLeadershipPositionId").GetString());
        Assert.Equal("owner@acme.pt", json.GetProperty("owner").GetProperty("ref").GetString());
        var relations = json.GetProperty("relations").EnumerateArray().ToArray();
        Assert.Equal(
            new[] { "ceo", "delivery-lead" },
            relations.Select(item => item.GetProperty("positionId").GetString()));
        Assert.Equal("ceo", relations[1].GetProperty("reportsToPositionId").GetString());
        Assert.Equal(
            "delivery-lead",
            Assert.Single(
                relations[0]
                    .GetProperty("directSubordinatePositionIds")
                    .EnumerateArray())
                .GetString());
    }

    [Fact]
    public async Task Position_configuration_query_aggregates_all_position_projections()
    {
        await SeedRegistryAsync();
        await using var app = BuildApp(fixture.ConnectionString);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var json = await client.GetFromJsonAsync<JsonElement>(
            $"{OrganizationRegistryEndpointExtensions.BasePath}/acme-delivery/positions/delivery-lead/configuration");

        Assert.Equal(
            "delivery-lead",
            json.GetProperty("position").GetProperty("id").GetString());
        Assert.Equal(
            "ai-agent",
            json.GetProperty("occupant").GetProperty("type").GetString());
        Assert.Equal(
            "stub",
            json.GetProperty("occupant").GetProperty("ai").GetProperty("provider").GetString());
        Assert.Equal(
            "triagem-de-bugs",
            Assert.Single(
                json.GetProperty("authority").GetProperty("canDecide").EnumerateArray())
                .GetString());
        Assert.Equal(
            "relatorio-diario",
            Assert.Single(json.GetProperty("schedules").EnumerateArray())
                .GetProperty("id")
                .GetString());
    }

    private async Task<OrganizationRegistrySnapshot> SeedRegistryAsync()
    {
        await PrepareEmptyRegistryAsync();
        await using var dataSource = fixture.CreateDataSource();

        var parseResult = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(
                RepositoryRoot,
                "config",
                "organizations",
                "acme-delivery",
                "organization.yaml"));
        Assert.True(parseResult.IsSuccess, string.Join(Environment.NewLine, parseResult.Errors));

        var registry = new PostgreSqlOrganizationRegistry(dataSource);
        var importResult = await new OrganizationConfigurationImporter(registry)
            .ImportAsync(parseResult.Configuration!);
        return importResult.Snapshot!;
    }

    private async Task PrepareEmptyRegistryAsync()
    {
        await fixture.ResetRegistryAsync();
        await using var dataSource = fixture.CreateDataSource();
        await new PostgreSqlOrganizationRegistryMigrator(dataSource).MigrateAsync();
    }

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

    private static string RepositoryRoot
    {
        get
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
}
