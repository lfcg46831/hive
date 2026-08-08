using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Api.Authorization;
using Hive.Api.Inbox;
using Hive.Api.OpenApi;
using Hive.Api.Organization;
using Hive.Contracts.Inbox;
using Hive.Domain.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class InboxEndpointTests
{
    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset LastEventAppliedAt = GeneratedAt.AddSeconds(-5);

    private static readonly DateTimeOffset SentAt = GeneratedAt.AddMinutes(-30);

    private static readonly DateTimeOffset DeadlineAt = GeneratedAt.AddHours(2);

    private const string ItemId =
        "delivery-lead/cf2b086f-dd04-445f-a68e-8e40a75530b9";

    [Fact]
    public async Task Public_queries_expose_person_position_and_item_resources_with_filters()
    {
        var readModel = RecordingInboxReadModel.Available();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);
        var basePath = $"{InboxEndpointExtensions.BasePath}/acme";

        var aggregate = await client.GetFromJsonAsync<JsonElement>(
            $"{basePath}/inbox" +
            "?type=ApprovalRequest" +
            "&read_state=Unread" +
            "&response_state=NotApplicable" +
            "&priority=Critical" +
            "&deadline_from_utc=2026-08-04T09%3A00%3A00Z" +
            "&deadline_to_utc=2026-08-04T13%3A00%3A00Z" +
            "&approval_pending=true" +
            "&page_size=25" +
            "&cursor=page-two");
        var position = await client.GetFromJsonAsync<JsonElement>(
            $"{basePath}/positions/delivery-lead/inbox?page_size=10");
        var detail = await client.GetFromJsonAsync<JsonElement>(
            $"{basePath}/inbox/{Uri.EscapeDataString(ItemId)}");

        Assert.Equal(GeneratedAt, aggregate.GetProperty("generated_at_utc").GetDateTimeOffset());
        Assert.Equal(
            LastEventAppliedAt,
            aggregate.GetProperty("last_event_applied_at_utc").GetDateTimeOffset());
        Assert.Equal(25, aggregate.GetProperty("page_size").GetInt32());
        Assert.Equal(
            ItemId,
            Assert.Single(aggregate.GetProperty("items").EnumerateArray())
                .GetProperty("item_id")
                .GetString());
        Assert.Equal(10, position.GetProperty("page_size").GetInt32());
        Assert.Equal(
            ItemId,
            detail.GetProperty("item").GetProperty("item_id").GetString());

        Assert.Collection(
            readModel.ListRequests,
            request =>
            {
                Assert.Equal(PersonId, request.PersonId);
                Assert.Equal("acme", request.OrganizationId);
                Assert.Equal(["delivery-lead"], request.ScopedPositionIds);
                Assert.Null(request.PositionId);
                Assert.Equal(InboxMessageType.ApprovalRequest, request.Query.MessageType);
                Assert.Equal(InboxReadState.Unread, request.Query.ReadState);
                Assert.Equal(InboxResponseState.NotApplicable, request.Query.ResponseState);
                Assert.Equal(InboxPriority.Critical, request.Query.Priority);
                Assert.Equal(
                    new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.Zero),
                    request.Query.DeadlineFromUtc);
                Assert.Equal(
                    new DateTimeOffset(2026, 8, 4, 13, 0, 0, TimeSpan.Zero),
                    request.Query.DeadlineToUtc);
                Assert.True(request.Query.ApprovalPending);
                Assert.Equal(25, request.Query.PageSize);
                Assert.Equal("page-two", request.Query.Cursor);
            },
            request =>
            {
                Assert.Equal(PersonId, request.PersonId);
                Assert.Equal("acme", request.OrganizationId);
                Assert.Equal(["delivery-lead"], request.ScopedPositionIds);
                Assert.Equal("delivery-lead", request.PositionId);
                Assert.Equal(10, request.Query.PageSize);
            });
        var itemRequest = Assert.Single(readModel.ItemRequests);
        Assert.Equal(PersonId, itemRequest.PersonId);
        Assert.Equal("acme", itemRequest.OrganizationId);
        Assert.Equal(["delivery-lead"], itemRequest.ScopedPositionIds);
        Assert.Equal(ItemId, itemRequest.ItemId);
    }

    [Theory]
    [InlineData("/acme/inbox")]
    [InlineData("/acme/positions/delivery-lead/inbox")]
    [InlineData("/acme/inbox/delivery-lead%2Fcf2b086f-dd04-445f-a68e-8e40a75530b9")]
    public async Task Public_snapshots_support_private_etag_polling(string suffix)
    {
        await using var app = BuildApp(RecordingInboxReadModel.Available());
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);

        using var initial = await client.GetAsync(InboxEndpointExtensions.BasePath + suffix);
        var etag = initial.Headers.ETag;
        using var poll = new HttpRequestMessage(
            HttpMethod.Get,
            InboxEndpointExtensions.BasePath + suffix);
        poll.Headers.IfNoneMatch.Add(etag!);
        using var unchanged = await client.SendAsync(poll);

        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.NotNull(etag);
        Assert.True(etag!.IsWeak);
        Assert.True(initial.Headers.CacheControl?.Private);
        Assert.True(initial.Headers.CacheControl?.NoCache);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Equal(etag, unchanged.Headers.ETag);
        Assert.Equal(0, unchanged.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Malformed_polling_validator_is_ignored_and_replaced()
    {
        await using var app = BuildApp(RecordingInboxReadModel.Available());
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{InboxEndpointExtensions.BasePath}/acme/inbox");
        request.Headers.TryAddWithoutValidation("If-None-Match", "not-an-entity-tag");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
    }

    [Theory]
    [InlineData("?type=Pulse")]
    [InlineData("?read_state=Maybe")]
    [InlineData("?response_state=Expired")]
    [InlineData("?priority=Urgent")]
    [InlineData("?deadline_from_utc=2026-08-04T09%3A00%3A00%2B01%3A00")]
    [InlineData("?deadline_from_utc=2026-08-04T13%3A00%3A00Z&deadline_to_utc=2026-08-04T09%3A00%3A00Z")]
    [InlineData("?approval_pending=yes")]
    [InlineData("?page_size=0")]
    [InlineData("?page_size=101")]
    [InlineData("?cursor=%20")]
    public async Task List_queries_reject_invalid_filters_before_reading_the_projection(
        string query)
    {
        var readModel = RecordingInboxReadModel.Available();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);

        using var response = await client.GetAsync(
            $"{InboxEndpointExtensions.BasePath}/acme/inbox{query}");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Invalid inbox query", problem!.Title);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Empty(readModel.ListRequests);
    }

    [Theory]
    [InlineData("/%20acme/inbox", "Invalid organization identifier")]
    [InlineData("/acme/positions/%20delivery-lead/inbox", "Invalid position identifier")]
    [InlineData("/acme/inbox/%20", "Invalid inbox item identifier")]
    public async Task Routes_reject_invalid_identifiers(
        string suffix,
        string expectedTitle)
    {
        var readModel = RecordingInboxReadModel.Available();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);

        using var response = await client.GetAsync(InboxEndpointExtensions.BasePath + suffix);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedTitle, problem!.Title);
        Assert.Empty(readModel.ListRequests);
        Assert.Empty(readModel.ItemRequests);
    }

    [Theory]
    [InlineData("/acme/inbox", "Organization not found")]
    [InlineData("/acme/positions/delivery-lead/inbox", "Position not found")]
    [InlineData("/acme/inbox/delivery-lead%2Fcf2b086f-dd04-445f-a68e-8e40a75530b9", "Inbox item not found")]
    public async Task Missing_resources_return_problem_details(
        string suffix,
        string expectedTitle)
    {
        var readModel = RecordingInboxReadModel.Missing();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);

        using var response = await client.GetAsync(InboxEndpointExtensions.BasePath + suffix);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedTitle, problem!.Title);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
    }

    [Theory]
    [InlineData("/acme/inbox")]
    [InlineData("/acme/positions/delivery-lead/inbox")]
    [InlineData("/acme/inbox/delivery-lead%2Fcf2b086f-dd04-445f-a68e-8e40a75530b9")]
    public async Task Default_read_model_reports_unavailable_until_projection_is_registered(
        string suffix)
    {
        await using var app = BuildApp(readModel: null);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);

        using var response = await client.GetAsync(InboxEndpointExtensions.BasePath + suffix);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Inbox read model unavailable", problem!.Title);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.Status);
    }

    [Fact]
    public async Task Organization_scope_is_applied_before_the_inbox_read_model()
    {
        var readModel = RecordingInboxReadModel.Available();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);

        using var response = await client.GetAsync(
            $"{InboxEndpointExtensions.BasePath}/globex/inbox");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Organization not found", problem!.Title);
        Assert.Empty(readModel.ListRequests);
    }

    [Fact]
    public async Task Position_scope_is_applied_before_the_inbox_read_model()
    {
        var readModel = RecordingInboxReadModel.Available();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);

        using var response = await client.GetAsync(
            $"{InboxEndpointExtensions.BasePath}/acme/positions/engineer/inbox");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Position not found", problem!.Title);
        Assert.Empty(readModel.ListRequests);
    }

    [Fact]
    public async Task Organization_only_credentials_cannot_be_used_as_person_inbox_credentials()
    {
        var readModel = RecordingInboxReadModel.Available();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app, OrganizationOnlyToken);

        using var response = await client.GetAsync(
            $"{InboxEndpointExtensions.BasePath}/acme/inbox");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Organization not found", problem!.Title);
        Assert.Empty(readModel.ListRequests);
    }

    [Fact]
    public async Task Person_positions_outside_the_credential_organization_scope_fail_startup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Token"] =
                    "invalid-person-scope-token",
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:OrganizationIds:0"] =
                    "acme",
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:PersonId"] =
                    PersonId,
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Positions:0:OrganizationId"] =
                    "globex",
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Positions:0:PositionId"] =
                    "delivery-lead",
            });
        builder.Services.AddHiveInboxApi();
        await using var app = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());
    }

    [Fact]
    public async Task Public_document_can_describe_all_inbox_routes_and_query_parameters()
    {
        var readModel = RecordingInboxReadModel.Available();
        await using var app = BuildApp(readModel, includeOpenApi: true);
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(PublicApiOpenApiExtensions.DocumentPath);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var paths = document.RootElement.GetProperty("paths");
        var aggregate = paths
            .GetProperty("/api/v1/organizations/{organizationId}/inbox")
            .GetProperty("get");
        var queryNames = aggregate
            .GetProperty("parameters")
            .EnumerateArray()
            .Where(parameter => parameter.GetProperty("in").GetString() == "query")
            .Select(parameter => parameter.GetProperty("name").GetString())
            .ToArray();

        Assert.True(paths.TryGetProperty(
            "/api/v1/organizations/{organizationId}/positions/{positionId}/inbox",
            out _));
        Assert.True(paths.TryGetProperty(
            "/api/v1/organizations/{organizationId}/inbox/{itemId}",
            out _));
        Assert.Equal(
            [
                "type",
                "read_state",
                "response_state",
                "priority",
                "deadline_from_utc",
                "deadline_to_utc",
                "approval_pending",
                "page_size",
                "cursor",
            ],
            queryNames);
    }

    private static WebApplication BuildApp(
        IInboxReadModel? readModel,
        bool includeOpenApi = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Token"] =
                    OrganizationToken,
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:OrganizationIds:0"] =
                    "acme",
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:PersonId"] =
                    PersonId,
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Positions:0:OrganizationId"] =
                    "acme",
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Positions:0:PositionId"] =
                    "delivery-lead",
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:1:Token"] =
                    OrganizationOnlyToken,
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:1:OrganizationIds:0"] =
                    "acme",
            });
        if (readModel is not null)
        {
            builder.Services.AddSingleton(readModel);
        }

        if (includeOpenApi)
        {
            builder.Services.AddHivePublicApiOpenApi();
        }

        // The production host composes both public API slices over the same authorization seam.
        builder.Services.AddHiveInboxApi();
        builder.Services.AddHiveOrganizationApi();
        var app = builder.Build();
        if (includeOpenApi)
        {
            app.UseHivePublicApiOpenApi();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHiveInboxApi();
        return app;
    }

    private static HttpClient CreateAuthorizedClient(
        WebApplication app,
        string token = OrganizationToken)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static InboxItem CreateItem() => new(
        ItemId,
        Guid.Parse("cf2b086f-dd04-445f-a68e-8e40a75530b9"),
        "delivery-lead",
        InboxMessageType.ApprovalRequest,
        new InboxMessageEndpoint(InboxMessageEndpointType.Position, "engineer"),
        new InboxMessageEndpoint(InboxMessageEndpointType.Position, "delivery-lead"),
        Guid.Parse("68bba79b-d881-40a8-82fd-09b08e2adfd7"),
        InboxPriority.Critical,
        SentAt,
        DeadlineAt,
        InboxReadState.Unread,
        InboxResponseState.NotApplicable,
        new InboxApprovalMetadata(
            Guid.Parse("cf2b086f-dd04-445f-a68e-8e40a75530b9"),
            "deployment.production",
            "production-change",
            InboxApprovalState.Pending,
            canDecide: true));

    private sealed class RecordingInboxReadModel : IInboxReadModel
    {
        private RecordingInboxReadModel(bool available, bool missing)
        {
            IsAvailable = available;
            IsMissing = missing;
        }

        public bool IsAvailable { get; }

        public bool IsMissing { get; }

        public List<(
            string PersonId,
            string OrganizationId,
            string[] ScopedPositionIds,
            string? PositionId,
            InboxListQuery Query)>
            ListRequests
        { get; } = [];

        public List<(
            string PersonId,
            string OrganizationId,
            string[] ScopedPositionIds,
            string ItemId)> ItemRequests
        { get; } = [];

        public static RecordingInboxReadModel Available() =>
            new(available: true, missing: false);

        public static RecordingInboxReadModel Missing() =>
            new(available: true, missing: true);

        public ValueTask<InboxReadResult<InboxPage>> ListAsync(
            PersonOrganizationScope scope,
            PositionId? positionId,
            InboxListQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListRequests.Add((
                scope.PersonId,
                scope.OrganizationId.Value,
                scope.PositionIds.Select(static position => position.Value).ToArray(),
                positionId?.Value,
                query));
            if (!IsAvailable)
            {
                return ValueTask.FromResult(InboxReadResult<InboxPage>.Unavailable);
            }

            return ValueTask.FromResult(InboxReadResult<InboxPage>.Available(
                IsMissing
                    ? null
                    : new InboxPage(
                        GeneratedAt,
                        LastEventAppliedAt,
                        query.PageSize,
                        nextCursor: "next-page",
                        [CreateItem()])));
        }

        public ValueTask<InboxReadResult<InboxItemResponse>> ReadItemAsync(
            PersonOrganizationScope scope,
            string itemId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ItemRequests.Add((
                scope.PersonId,
                scope.OrganizationId.Value,
                scope.PositionIds.Select(static position => position.Value).ToArray(),
                itemId));
            if (!IsAvailable)
            {
                return ValueTask.FromResult(InboxReadResult<InboxItemResponse>.Unavailable);
            }

            return ValueTask.FromResult(InboxReadResult<InboxItemResponse>.Available(
                IsMissing
                    ? null
                    : new InboxItemResponse(
                        GeneratedAt,
                        LastEventAppliedAt,
                        CreateItem())));
        }
    }

    private const string OrganizationToken = "test-token-for-acme";

    private const string OrganizationOnlyToken = "organization-only-token-for-acme";

    private const string PersonId = "person-alice";
}
