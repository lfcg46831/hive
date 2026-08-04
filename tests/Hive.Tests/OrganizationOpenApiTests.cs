using System.Net;
using System.Text.Json;
using Hive.Api.OpenApi;
using Hive.Api.Organization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class OrganizationOpenApiTests
{
    private static readonly string[] OrganizationPaths =
    [
        "/api/v1/organizations/{organizationId}/organogram",
        "/api/v1/organizations/{organizationId}/units/{unitId}/organogram",
        "/api/v1/organizations/{organizationId}/positions/{positionId}",
        "/api/v1/organizations/{organizationId}/position-states",
    ];

    private static readonly string[] CompleteSnapshotSchemas =
    [
        "RegistryVersion",
        "OrganizationSummary",
        "OrganizationUnit",
        "OrganizationOccupant",
        "PositionHierarchy",
        "PositionCorrelatedEvent",
        "OrganizationPositionState",
        "OrganizationPosition",
        "OrganogramResponse",
        "PositionDetailResponse",
        "PositionStatesResponse",
    ];

    private static readonly HashSet<string> NullableSnapshotProperties = new(
        StringComparer.Ordinal)
    {
        "OrganizationSummary.name",
        "OrganizationUnit.name",
        "OrganizationUnit.parent_unit_id",
        "OrganizationOccupant.id",
        "PositionHierarchy.reports_to_position_id",
        "OrganizationPositionState.last_correlated_event",
        "OrganizationPosition.name",
        "PositionStatesResponse.last_event_applied_at_utc",
    };

    [Fact]
    public async Task Public_document_exposes_only_the_versioned_public_surface()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(PublicApiOpenApiExtensions.DocumentPath);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "HIVE Public API",
            document.RootElement.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal(
            "v1",
            document.RootElement.GetProperty("info").GetProperty("version").GetString());
        Assert.Contains(
            "incompatible changes require a new path version",
            document.RootElement
                .GetProperty("info")
                .GetProperty("description")
                .GetString(),
            StringComparison.Ordinal);

        var documentedPaths = document.RootElement
            .GetProperty("paths")
            .EnumerateObject()
            .Select(path => path.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(OrganizationPaths.Order(StringComparer.Ordinal), documentedPaths);
        Assert.DoesNotContain(documentedPaths, path =>
            path.StartsWith("/internal", StringComparison.Ordinal));

        var bearerScheme = document.RootElement
            .GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("Bearer");
        Assert.Equal("http", bearerScheme.GetProperty("type").GetString());
        Assert.Equal("bearer", bearerScheme.GetProperty("scheme").GetString());
    }

    [Theory]
    [InlineData(
        "/api/v1/organizations/{organizationId}/organogram",
        "GetOrganizationOrganogramV1",
        "OrganogramResponse")]
    [InlineData(
        "/api/v1/organizations/{organizationId}/units/{unitId}/organogram",
        "GetOrganizationUnitOrganogramV1",
        "OrganogramResponse")]
    [InlineData(
        "/api/v1/organizations/{organizationId}/positions/{positionId}",
        "GetOrganizationPositionV1",
        "PositionDetailResponse")]
    [InlineData(
        "/api/v1/organizations/{organizationId}/position-states",
        "GetOrganizationPositionStatesV1",
        "PositionStatesResponse")]
    public async Task Organization_operations_document_success_and_problem_contracts(
        string path,
        string operationId,
        string responseSchema)
    {
        using var document = await ReadDocumentAsync();

        var operation = GetOperation(document, path);
        var responses = operation.GetProperty("responses");

        Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
        Assert.Equal("Organization", Assert.Single(
            operation.GetProperty("tags").EnumerateArray()).GetString());
        var securityRequirement = Assert.Single(
            operation.GetProperty("security").EnumerateArray());
        Assert.Empty(
            securityRequirement.GetProperty("Bearer").EnumerateArray());
        Assert.Contains(
            "pagination",
            operation.GetProperty("description").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            $"/components/schemas/{responseSchema}",
            responses
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString(),
            StringComparison.Ordinal);

        foreach (var statusCode in new[] { "400", "401", "404", "503" })
        {
            var problemResponse = responses.GetProperty(statusCode);
            Assert.Contains(
                "Problem Details",
                problemResponse.GetProperty("description").GetString(),
                StringComparison.Ordinal);
            Assert.EndsWith(
                "/components/schemas/ProblemDetails",
                problemResponse
                    .GetProperty("content")
                    .GetProperty("application/problem+json")
                    .GetProperty("schema")
                    .GetProperty("$ref")
                    .GetString(),
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            operation.GetProperty("parameters").EnumerateArray(),
            parameter => string.Equals(
                parameter.GetProperty("in").GetString(),
                "query",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Position_state_polling_documents_conditional_requests()
    {
        using var document = await ReadDocumentAsync();

        var operation = GetOperation(
            document,
            "/api/v1/organizations/{organizationId}/position-states");
        var ifNoneMatch = Assert.Single(
            operation.GetProperty("parameters").EnumerateArray(),
            parameter =>
                string.Equals(
                    parameter.GetProperty("name").GetString(),
                    "If-None-Match",
                    StringComparison.Ordinal) &&
                string.Equals(
                    parameter.GetProperty("in").GetString(),
                    "header",
                    StringComparison.Ordinal));
        var responses = operation.GetProperty("responses");

        Assert.False(
            ifNoneMatch.TryGetProperty("required", out var required) &&
            required.GetBoolean());
        Assert.Equal(
            "Weak ETag returned by an earlier position-state snapshot.",
            ifNoneMatch.GetProperty("description").GetString());
        Assert.True(responses.TryGetProperty("304", out var notModified));
        Assert.Contains(
            "no body",
            notModified.GetProperty("description").GetString(),
            StringComparison.Ordinal);
        Assert.True(
            responses.GetProperty("200").GetProperty("headers").TryGetProperty(
                "ETag",
                out _));
        Assert.True(notModified.GetProperty("headers").TryGetProperty("ETag", out _));
        Assert.False(notModified.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task Public_schemas_preserve_the_ui_wire_contract()
    {
        using var document = await ReadDocumentAsync();

        var schemas = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas");
        var organogramProperties = schemas
            .GetProperty("OrganogramResponse")
            .GetProperty("properties");
        var states = schemas
            .GetProperty("PositionOperationalState")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();

        Assert.True(organogramProperties.TryGetProperty("generated_at_utc", out _));
        Assert.True(organogramProperties.TryGetProperty("root_unit_id", out _));
        Assert.Equal(
            ["Offline", "Blocked", "WaitingHuman", "Working", "Idle"],
            states);
        Assert.DoesNotContain(
            schemas.EnumerateObject().Select(schema => schema.Name),
            name =>
                name.Contains("Infrastructure", StringComparison.Ordinal) ||
                name.Contains("Domain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Public_snapshot_schemas_distinguish_required_fields_from_nullable_values()
    {
        using var document = await ReadDocumentAsync();

        var schemas = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas");
        foreach (var schemaName in CompleteSnapshotSchemas)
        {
            var schema = schemas.GetProperty(schemaName);
            var properties = schema
                .GetProperty("properties")
                .EnumerateObject()
                .ToArray();
            var required = schema
                .GetProperty("required")
                .EnumerateArray()
                .Select(property => property.GetString())
                .ToArray();

            Assert.Equal(
                properties.Select(property => property.Name).Order(StringComparer.Ordinal),
                required.Order(StringComparer.Ordinal));
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());

            foreach (var property in properties)
            {
                var expectedNullable = NullableSnapshotProperties.Contains(
                    $"{schemaName}.{property.Name}");
                var actualNullable =
                    property.Value.TryGetProperty("nullable", out var nullable) &&
                    nullable.GetBoolean();
                Assert.Equal(expectedNullable, actualNullable);
            }
        }

        var problemDetails = schemas.GetProperty("ProblemDetails");
        Assert.False(problemDetails.TryGetProperty("required", out _));
        Assert.Equal(
            JsonValueKind.Object,
            problemDetails.GetProperty("additionalProperties").ValueKind);
    }

    private static async Task<JsonDocument> ReadDocumentAsync()
    {
        var app = BuildApp();
        try
        {
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var response = await client.GetAsync(PublicApiOpenApiExtensions.DocumentPath);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static JsonElement GetOperation(JsonDocument document, string path) =>
        document.RootElement
            .GetProperty("paths")
            .GetProperty(path)
            .GetProperty("get");

    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHivePublicApiOpenApi();
        builder.Services.AddHiveOrganizationApi();
        builder.Services.AddHiveOrganizationRegistryApi();

        var app = builder.Build();
        app.UseHivePublicApiOpenApi();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHiveOrganizationApi();
        app.MapHiveOrganizationRegistryApi();
        return app;
    }
}
