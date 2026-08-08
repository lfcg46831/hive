using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Api.Authorization;
using Hive.Api.Inbox;
using Hive.Api.OpenApi;
using Hive.Contracts.Inbox;
using Hive.Domain.Identity;
using Hive.Infrastructure.Inbox.ReadModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class InboxInteractionEndpointTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 8, 10, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastEventAppliedAt = Now.AddSeconds(-4);
    private static readonly Guid MessageId =
        Guid.Parse("71000000-0000-0000-0000-000000000001");
    private const string ItemId =
        "engineer/71000000-0000-0000-0000-000000000001";

    [Fact]
    public async Task Authorized_actions_persist_principal_scoped_read_reply_and_draft_state()
    {
        var readModel = new RecordingReadModel(Item());
        var sink = new RecordingInteractionSink();
        await using var app = BuildApp(readModel, sink);
        await app.StartAsync();
        using var client = AuthorizedClient(app, PersonToken);
        var actionPath =
            $"{InboxEndpointExtensions.BasePath}/acme/inbox/{Uri.EscapeDataString(ItemId)}";

        using var readResponse = await client.PostAsync($"{actionPath}/read", content: null);
        using var unreadResponse = await client.PostAsync($"{actionPath}/unread", content: null);
        using var startResponse = await client.PostAsJsonAsync(
            $"{actionPath}/draft",
            new InboxDraftRequest(body: null));
        using var saveResponse = await client.PostAsJsonAsync(
            $"{actionPath}/draft",
            new InboxDraftRequest("A partially written response. "));
        using var clearResponse = await client.PostAsJsonAsync(
            $"{actionPath}/draft",
            new InboxDraftRequest(string.Empty));

        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unreadResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, clearResponse.StatusCode);
        Assert.Equal(
            [
                InboxInteractionAction.MarkRead,
                InboxInteractionAction.MarkUnread,
                InboxInteractionAction.StartReply,
                InboxInteractionAction.SaveDraft,
                InboxInteractionAction.ClearDraft,
            ],
            sink.Mutations.Select(static mutation => mutation.Action));
        Assert.All(sink.Mutations, mutation =>
        {
            Assert.Equal("acme", mutation.ItemKey.OrganizationId.Value);
            Assert.Equal("engineer", mutation.ItemKey.AssignedPositionId.Value);
            Assert.Equal(MessageId, mutation.ItemKey.MessageId.Value);
            Assert.Equal("person-alice", mutation.PersonId);
            Assert.Equal(Now, mutation.OccurredAtUtc);
        });
        Assert.Equal("A partially written response. ", sink.Mutations[3].DraftText);

        var read = await readResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Read", read.GetProperty("read_state").GetString());
        Assert.Equal("AwaitingResponse", read.GetProperty("response_state").GetString());
        var unread = await unreadResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Unread", unread.GetProperty("read_state").GetString());
        var saved = await saveResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Now, saved.GetProperty("generated_at_utc").GetDateTimeOffset());
        Assert.Equal(
            LastEventAppliedAt,
            saved.GetProperty("last_event_applied_at_utc").GetDateTimeOffset());
        Assert.Equal(ItemId, saved.GetProperty("item_id").GetString());
        Assert.Equal("Unread", saved.GetProperty("read_state").GetString());
        Assert.Equal("InProgress", saved.GetProperty("response_state").GetString());
        Assert.Equal("A partially written response. ", saved.GetProperty("draft_text").GetString());
        Assert.Equal(
            Now,
            saved.GetProperty("interaction_updated_at_utc").GetDateTimeOffset());
        var cleared = await clearResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("draft_text").ValueKind);
    }

    [Theory]
    [InlineData("organization", "/globex")]
    [InlineData("item", "/acme")]
    public async Task Actions_hide_resources_outside_the_principal_scope(
        string missingResource,
        string organizationSuffix)
    {
        var readModel = new RecordingReadModel(
            string.Equals(missingResource, "item", StringComparison.Ordinal) ? null : Item());
        var sink = new RecordingInteractionSink();
        await using var app = BuildApp(readModel, sink);
        await app.StartAsync();
        using var client = AuthorizedClient(app, PersonToken);

        using var response = await client.PostAsync(
            $"{InboxEndpointExtensions.BasePath}{organizationSuffix}/inbox/" +
            $"{Uri.EscapeDataString(ItemId)}/read",
            content: null);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            string.Equals(missingResource, "organization", StringComparison.Ordinal)
                ? "Organization not found"
                : "Inbox item not found",
            problem.GetProperty("title").GetString());
        Assert.Empty(sink.Mutations);
    }

    [Fact]
    public async Task Organization_only_credentials_cannot_mutate_person_interaction_state()
    {
        var readModel = new RecordingReadModel(Item());
        var sink = new RecordingInteractionSink();
        await using var app = BuildApp(readModel, sink);
        await app.StartAsync();
        using var client = AuthorizedClient(app, OrganizationOnlyToken);

        using var response = await client.PostAsync(
            $"{InboxEndpointExtensions.BasePath}/acme/inbox/" +
            $"{Uri.EscapeDataString(ItemId)}/read",
            content: null);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Organization not found", problem.GetProperty("title").GetString());
        Assert.Empty(sink.Mutations);
    }

    [Fact]
    public async Task Draft_validation_and_store_unavailability_use_problem_details()
    {
        var readModel = new RecordingReadModel(Item());
        var sink = new RecordingInteractionSink(isAvailable: false);
        await using var app = BuildApp(readModel, sink);
        await app.StartAsync();
        using var client = AuthorizedClient(app, PersonToken);
        var actionPath =
            $"{InboxEndpointExtensions.BasePath}/acme/inbox/{Uri.EscapeDataString(ItemId)}";

        using var invalid = await client.PostAsJsonAsync(
            $"{actionPath}/draft",
            new InboxDraftRequest(new string('x', 4_097)));
        var invalidProblem = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        using var unavailable = await client.PostAsync($"{actionPath}/read", content: null);
        var unavailableProblem = await unavailable.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("Invalid inbox draft", invalidProblem.GetProperty("title").GetString());
        Assert.Equal("body", invalidProblem.GetProperty("path").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal(
            "Inbox interaction store unavailable",
            unavailableProblem.GetProperty("title").GetString());
        Assert.Empty(sink.Mutations);
    }

    [Fact]
    public async Task Public_document_describes_all_interaction_operations_and_contracts()
    {
        await using var app = BuildApp(
            new RecordingReadModel(Item()),
            new RecordingInteractionSink(),
            includeOpenApi: true);
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(PublicApiOpenApiExtensions.DocumentPath);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var paths = document.RootElement.GetProperty("paths");
        var itemPath = "/api/v1/organizations/{organizationId}/inbox/{itemId}";

        Assert.Equal(
            "MarkOrganizationInboxItemReadV1",
            paths.GetProperty(itemPath + "/read")
                .GetProperty("post")
                .GetProperty("operationId")
                .GetString());
        Assert.Equal(
            "MarkOrganizationInboxItemUnreadV1",
            paths.GetProperty(itemPath + "/unread")
                .GetProperty("post")
                .GetProperty("operationId")
                .GetString());
        Assert.Equal(
            "SaveOrganizationInboxItemDraftV1",
            paths.GetProperty(itemPath + "/draft")
                .GetProperty("post")
                .GetProperty("operationId")
                .GetString());
        var schemas = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas");
        Assert.True(schemas.TryGetProperty(nameof(InboxDraftRequest), out _));
        Assert.True(schemas.TryGetProperty(nameof(InboxInteractionResponse), out _));
    }

    private static WebApplication BuildApp(
        IInboxReadModel readModel,
        IInboxInteractionCommandSink sink,
        bool includeOpenApi = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Token"] = PersonToken,
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:OrganizationIds:0"] = "acme",
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:PersonId"] = "person-alice",
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Positions:0:OrganizationId"] = "acme",
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Positions:0:PositionId"] = "engineer",
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:1:Token"] = OrganizationOnlyToken,
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:1:OrganizationIds:0"] = "acme",
        });
        builder.Services.AddSingleton(readModel);
        builder.Services.AddSingleton(sink);
        builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        if (includeOpenApi)
        {
            builder.Services.AddHivePublicApiOpenApi();
        }

        builder.Services.AddHiveInboxApi();
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

    private static HttpClient AuthorizedClient(WebApplication app, string token)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static InboxItem Item() => new(
        ItemId,
        MessageId,
        "engineer",
        InboxMessageType.Directive,
        new InboxMessageEndpoint(InboxMessageEndpointType.Position, "delivery-lead"),
        new InboxMessageEndpoint(InboxMessageEndpointType.Position, "engineer"),
        Guid.Parse("72000000-0000-0000-0000-000000000001"),
        InboxPriority.High,
        Now.AddMinutes(-5),
        Now.AddHours(1),
        InboxReadState.Unread,
        InboxResponseState.AwaitingResponse);

    private sealed class RecordingReadModel(InboxItem? item) : IInboxReadModel
    {
        public ValueTask<InboxReadResult<InboxPage>> ListAsync(
            PersonOrganizationScope scope,
            PositionId? positionId,
            InboxListQuery query,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(InboxReadResult<InboxPage>.Available(
                new InboxPage(Now, LastEventAppliedAt, query.PageSize, null, [])));

        public ValueTask<InboxReadResult<InboxItemResponse>> ReadItemAsync(
            PersonOrganizationScope scope,
            string itemId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(InboxReadResult<InboxItemResponse>.Available(
                item is not null && string.Equals(item.ItemId, itemId, StringComparison.Ordinal)
                    ? new InboxItemResponse(Now, LastEventAppliedAt, item)
                    : null));
    }

    private sealed class RecordingInteractionSink(bool isAvailable = true) :
        IInboxInteractionCommandSink
    {
        private InboxInteractionReadState _readState = InboxInteractionReadState.Unread;
        private InboxInteractionReplyState _replyState = InboxInteractionReplyState.NotStarted;
        private string? _draftText;

        public bool IsAvailable => isAvailable;

        public List<InboxInteractionMutation> Mutations { get; } = [];

        public ValueTask<InboxInteractionState> ApplyAsync(
            InboxInteractionMutation mutation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mutations.Add(mutation);
            _readState = mutation.Action switch
            {
                InboxInteractionAction.MarkRead => InboxInteractionReadState.Read,
                InboxInteractionAction.MarkUnread => InboxInteractionReadState.Unread,
                _ => _readState,
            };
            _replyState = mutation.Action switch
            {
                InboxInteractionAction.StartReply or InboxInteractionAction.SaveDraft =>
                    InboxInteractionReplyState.InProgress,
                _ => _replyState,
            };
            _draftText = mutation.Action switch
            {
                InboxInteractionAction.SaveDraft => mutation.DraftText,
                InboxInteractionAction.ClearDraft => null,
                _ => _draftText,
            };
            return ValueTask.FromResult(new InboxInteractionState(
                mutation.ItemKey,
                mutation.PersonId,
                _readState,
                _replyState,
                _draftText,
                mutation.OccurredAtUtc));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private const string PersonToken = "person-token-for-acme";
    private const string OrganizationOnlyToken = "organization-token-for-acme";
}
