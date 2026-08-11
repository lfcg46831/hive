using System.Text.Json;
using Hive.Api.Inbox;
using Hive.Api.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests;

public sealed class InboxOpenApiTests
{
    private static readonly string[] InboxPaths =
    [
        "/api/v1/organizations/{organizationId}/inbox",
        "/api/v1/organizations/{organizationId}/positions/{positionId}/inbox",
        "/api/v1/organizations/{organizationId}/inbox/{itemId}",
        "/api/v1/organizations/{organizationId}/inbox/{itemId}/read",
        "/api/v1/organizations/{organizationId}/inbox/{itemId}/unread",
        "/api/v1/organizations/{organizationId}/inbox/{itemId}/draft",
        "/api/v1/organizations/{organizationId}/inbox/{itemId}/reply",
        "/api/v1/organizations/{organizationId}/inbox/{itemId}/decision",
    ];

    private static readonly string[] CompleteResponseSchemas =
    [
        "InboxMessageEndpoint",
        "InboxApprovalMetadata",
        "InboxItem",
        "InboxPage",
        "InboxItemResponse",
        "InboxInteractionResponse",
        "InboxReplyResponse",
        "InboxDecisionResponse",
    ];

    private static readonly HashSet<string> NullableResponseProperties = new(
        StringComparer.Ordinal)
    {
        "InboxMessageEndpoint.position_id",
        "InboxApprovalMetadata.decision_message_id",
        "InboxApprovalMetadata.decided_at_utc",
        "InboxItem.deadline_at_utc",
        "InboxItem.last_reminder_at_utc",
        "InboxItem.approval",
        "InboxPage.last_event_applied_at_utc",
        "InboxPage.next_cursor",
        "InboxItemResponse.last_event_applied_at_utc",
        "InboxItemResponse.draft_text",
        "InboxItemResponse.content",
        "InboxInteractionResponse.last_event_applied_at_utc",
        "InboxInteractionResponse.draft_text",
        "InboxReplyResponse.directive_id",
        "InboxDecisionResponse.reason",
    };

    [Fact]
    public async Task Public_document_exposes_the_complete_inbox_surface()
    {
        using var document = await ReadDocumentAsync();

        var paths = document.RootElement
            .GetProperty("paths")
            .EnumerateObject()
            .Select(path => path.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(InboxPaths.Order(StringComparer.Ordinal), paths);
        Assert.DoesNotContain(paths, path => path.StartsWith("/internal", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(
        "/api/v1/organizations/{organizationId}/inbox",
        "get",
        "GetOrganizationInboxV1",
        "200",
        "InboxPage")]
    [InlineData(
        "/api/v1/organizations/{organizationId}/positions/{positionId}/inbox",
        "get",
        "GetOrganizationPositionInboxV1",
        "200",
        "InboxPage")]
    [InlineData(
        "/api/v1/organizations/{organizationId}/inbox/{itemId}",
        "get",
        "GetOrganizationInboxItemV1",
        "200",
        "InboxItemResponse")]
    [InlineData(
        "/api/v1/organizations/{organizationId}/inbox/{itemId}/read",
        "post",
        "MarkOrganizationInboxItemReadV1",
        "200",
        "InboxInteractionResponse")]
    [InlineData(
        "/api/v1/organizations/{organizationId}/inbox/{itemId}/unread",
        "post",
        "MarkOrganizationInboxItemUnreadV1",
        "200",
        "InboxInteractionResponse")]
    [InlineData(
        "/api/v1/organizations/{organizationId}/inbox/{itemId}/draft",
        "post",
        "SaveOrganizationInboxItemDraftV1",
        "200",
        "InboxInteractionResponse")]
    [InlineData(
        "/api/v1/organizations/{organizationId}/inbox/{itemId}/reply",
        "post",
        "ReplyToOrganizationInboxItemV1",
        "202",
        "InboxReplyResponse")]
    [InlineData(
        "/api/v1/organizations/{organizationId}/inbox/{itemId}/decision",
        "post",
        "DecideOrganizationInboxApprovalV1",
        "202",
        "InboxDecisionResponse")]
    public async Task Inbox_operations_document_success_authentication_and_problem_contracts(
        string path,
        string method,
        string operationId,
        string successStatus,
        string successSchema)
    {
        using var document = await ReadDocumentAsync();

        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty(path)
            .GetProperty(method);
        var responses = operation.GetProperty("responses");

        Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
        Assert.Equal(
            "Inbox",
            Assert.Single(operation.GetProperty("tags").EnumerateArray()).GetString());
        var security = Assert.Single(operation.GetProperty("security").EnumerateArray());
        Assert.Empty(security.GetProperty("Bearer").EnumerateArray());
        Assert.EndsWith(
            $"/components/schemas/{successSchema}",
            responses
                .GetProperty(successStatus)
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString(),
            StringComparison.Ordinal);

        foreach (var statusCode in new[] { "400", "401", "404", "503" })
        {
            var problem = responses.GetProperty(statusCode);
            Assert.Contains(
                "Problem Details",
                problem.GetProperty("description").GetString(),
                StringComparison.Ordinal);
            Assert.EndsWith(
                "/components/schemas/ProblemDetails",
                problem
                    .GetProperty("content")
                    .GetProperty("application/problem+json")
                    .GetProperty("schema")
                    .GetProperty("$ref")
                    .GetString(),
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(
        "/api/v1/organizations/{organizationId}/inbox/{itemId}/draft",
        "InboxDraftRequest")]
    [InlineData(
        "/api/v1/organizations/{organizationId}/inbox/{itemId}/reply",
        "InboxReplyRequest")]
    [InlineData(
        "/api/v1/organizations/{organizationId}/inbox/{itemId}/decision",
        "InboxDecisionRequest")]
    public async Task Mutation_bodies_reference_the_public_request_contract(
        string path,
        string requestSchema)
    {
        using var document = await ReadDocumentAsync();

        var requestBody = document.RootElement
            .GetProperty("paths")
            .GetProperty(path)
            .GetProperty("post")
            .GetProperty("requestBody");
        var schema = requestBody
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        var reference = Assert.Single(schema.GetProperty("allOf").EnumerateArray())
            .GetProperty("$ref")
            .GetString();

        Assert.EndsWith(
            $"/components/schemas/{requestSchema}",
            reference,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/v1/organizations/{organizationId}/inbox")]
    [InlineData("/api/v1/organizations/{organizationId}/positions/{positionId}/inbox")]
    [InlineData("/api/v1/organizations/{organizationId}/inbox/{itemId}")]
    public async Task Inbox_snapshot_reads_document_conditional_polling(string path)
    {
        using var document = await ReadDocumentAsync();

        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty(path)
            .GetProperty("get");
        var ifNoneMatch = Assert.Single(
            operation.GetProperty("parameters").EnumerateArray(),
            parameter =>
                parameter.GetProperty("name").GetString() == "If-None-Match" &&
                parameter.GetProperty("in").GetString() == "header");
        var responses = operation.GetProperty("responses");
        var notModified = responses.GetProperty("304");

        Assert.False(
            ifNoneMatch.TryGetProperty("required", out var required) &&
            required.GetBoolean());
        Assert.True(responses.GetProperty("200").GetProperty("headers").TryGetProperty(
            "ETag",
            out _));
        Assert.True(notModified.GetProperty("headers").TryGetProperty("ETag", out _));
        Assert.False(notModified.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task Inbox_response_schemas_distinguish_required_fields_from_nullable_values()
    {
        using var document = await ReadDocumentAsync();

        var schemas = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas");
        foreach (var schemaName in CompleteResponseSchemas)
        {
            var schema = schemas.GetProperty(schemaName);
            var properties = schema.GetProperty("properties").EnumerateObject().ToArray();
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
                var expectedNullable = NullableResponseProperties.Contains(
                    $"{schemaName}.{property.Name}");
                var actualNullable =
                    property.Value.TryGetProperty("nullable", out var nullable) &&
                    nullable.GetBoolean();
                Assert.Equal(expectedNullable, actualNullable);
            }
        }
    }

    [Fact]
    public async Task Inbox_request_schemas_keep_optional_nullable_inputs_explicit()
    {
        using var document = await ReadDocumentAsync();

        var schemas = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas");
        foreach (var schemaName in new[]
                 {
                     "InboxDraftRequest",
                     "InboxReplyRequest",
                     "InboxDecisionRequest",
                 })
        {
            var schema = schemas.GetProperty(schemaName);

            Assert.False(schema.TryGetProperty("required", out _));
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            Assert.All(schema.GetProperty("properties").EnumerateObject(), property =>
            {
                Assert.True(property.Value.GetProperty("nullable").GetBoolean());
            });
        }
    }

    [Fact]
    public async Task Inbox_enums_preserve_the_stable_wire_values()
    {
        using var document = await ReadDocumentAsync();

        var schemas = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas");
        var expected = new Dictionary<string, string[]>
        {
            ["InboxMessageEndpointType"] = ["Position", "OrganizationOwner"],
            ["InboxMessageType"] =
            [
                "Directive",
                "Report",
                "Escalation",
                "Memo",
                "PeerRequest",
                "PeerResponse",
                "ApprovalRequest",
                "ApprovalDecision",
            ],
            ["InboxPriority"] = ["Low", "Normal", "High", "Critical"],
            ["InboxReadState"] = ["Unread", "Read"],
            ["InboxResponseState"] =
                ["NotApplicable", "AwaitingResponse", "InProgress", "Responded"],
            ["InboxApprovalState"] = ["Pending", "Approved", "Rejected", "Expired"],
            ["InboxReminderState"] = ["None", "Sent"],
        };

        foreach (var (schemaName, expectedValues) in expected)
        {
            var values = schemas
                .GetProperty(schemaName)
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            Assert.Equal(expectedValues, values);
        }
    }

    [Fact]
    public async Task Inbox_detail_content_is_a_closed_discriminated_union()
    {
        using var document = await ReadDocumentAsync();

        var schemas = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas");
        var content = schemas.GetProperty("InboxMessageContent");
        Assert.Equal("type", content.GetProperty("discriminator").GetProperty("propertyName").GetString());
        var expected = new Dictionary<string, string[]>
        {
            ["InboxDirectiveMessageContent"] = ["type", "objective", "context"],
            ["InboxReportMessageContent"] = ["type", "body", "kind"],
            ["InboxEscalationMessageContent"] = ["type", "issue", "context"],
            ["InboxMemoMessageContent"] = ["type", "body"],
            ["InboxPeerRequestMessageContent"] = ["type", "ask"],
            ["InboxPeerResponseMessageContent"] = ["type", "body"],
            ["InboxApprovalRequestMessageContent"] = ["type", "action", "justification"],
            ["InboxApprovalDecisionMessageContent"] = ["type", "reason"],
        };
        var references = content.GetProperty("oneOf")
            .EnumerateArray()
            .Select(schema => schema.GetProperty("$ref").GetString()!.Split('/')[^1])
            .ToArray();
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), references.Order(StringComparer.Ordinal));

        foreach (var (schemaName, expectedProperties) in expected)
        {
            var schema = schemas.GetProperty(schemaName);
            Assert.Equal(
                expectedProperties.Order(StringComparer.Ordinal),
                schema.GetProperty("properties").EnumerateObject()
                    .Select(property => property.Name)
                    .Order(StringComparer.Ordinal));
            var required = schema.GetProperty("required")
                .EnumerateArray()
                .Select(property => property.GetString())
                .ToArray();
            var expectedRequired = schemaName == "InboxApprovalDecisionMessageContent"
                ? ["type"]
                : expectedProperties;
            Assert.Equal(
                expectedRequired.Order(StringComparer.Ordinal),
                required.Order(StringComparer.Ordinal));
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        }

        Assert.Equal(
            ["progress", "done"],
            schemas.GetProperty("InboxReportKind").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()));
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

    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHivePublicApiOpenApi();
        builder.Services.AddHiveInboxApi();

        var app = builder.Build();
        app.UseHivePublicApiOpenApi();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHiveInboxApi();
        return app;
    }
}
