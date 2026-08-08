using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Api.Authorization;
using Hive.Api.Inbox;
using Hive.Api.OpenApi;
using Hive.Contracts.Inbox;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class InboxReplyEndpointTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid SourceMessageId =
        Guid.Parse("61000000-0000-0000-0000-000000000001");
    private static readonly Guid ThreadId =
        Guid.Parse("62000000-0000-0000-0000-000000000001");
    private const string ItemId =
        "engineer/61000000-0000-0000-0000-000000000001";

    [Fact]
    public async Task Authorized_reply_dispatches_generated_identifiers_to_the_occupied_position()
    {
        var readModel = new StaticInboxReadModel(Item(InboxMessageType.Directive));
        var sink = RecordingReplySink.Accepting();
        await using var app = BuildApp(readModel, sink);
        await app.StartAsync();
        using var client = AuthorizedClient(app, PersonToken);

        using var response = await client.PostAsJsonAsync(
            $"{InboxEndpointExtensions.BasePath}/acme/inbox/{Uri.EscapeDataString(ItemId)}/reply",
            new InboxReplyRequest("The fix is deployed and verified.", "done"));
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var request = Assert.Single(sink.Requests);
        Assert.Equal("acme/engineer", request.Position.Value);
        Assert.Equal(SourceMessageId, request.Command.SourceMessageId.Value);
        Assert.NotEqual(Guid.Empty, request.Command.ReplyMessageId.Value);
        Assert.NotEqual(SourceMessageId, request.Command.ReplyMessageId.Value);
        Assert.Equal(OccupantReplyAuthorKind.HumanUser, request.Command.Author.Kind);
        Assert.Equal("person-alice", request.Command.Author.SubjectId);
        Assert.Equal("web-inbox", request.Command.Author.Channel);
        Assert.Equal("The fix is deployed and verified.", request.Command.Body);
        Assert.Equal(ReportKind.Done, request.Command.ReportKind);
        Assert.Null(request.Command.ReplyDirectiveId);
        Assert.Equal(SourceMessageId, payload.GetProperty("source_message_id").GetGuid());
        Assert.Equal(request.Command.ReplyMessageId.Value, payload.GetProperty("message_id").GetGuid());
        Assert.Equal("Report", payload.GetProperty("type").GetString());
        Assert.Equal("engineer", payload.GetProperty("from_position_id").GetString());
        Assert.Equal("delivery-lead", payload.GetProperty("to_position_id").GetString());
        Assert.Equal(ThreadId, payload.GetProperty("thread_id").GetGuid());
        Assert.Equal(
            Guid.Parse("63000000-0000-0000-0000-000000000001"),
            payload.GetProperty("directive_id").GetGuid());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("complete")]
    [InlineData(" progress")]
    public async Task Directive_reply_requires_a_canonical_report_kind(string? reportKind)
    {
        var readModel = new StaticInboxReadModel(Item(InboxMessageType.Directive));
        var sink = RecordingReplySink.Accepting();
        await using var app = BuildApp(readModel, sink);
        await app.StartAsync();
        using var client = AuthorizedClient(app, PersonToken);

        using var response = await client.PostAsJsonAsync(
            $"{InboxEndpointExtensions.BasePath}/acme/inbox/{Uri.EscapeDataString(ItemId)}/reply",
            new InboxReplyRequest("Reply text.", reportKind));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid inbox reply", problem!.Title);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task Principal_without_a_person_position_scope_cannot_trigger_reply_emission()
    {
        var readModel = new StaticInboxReadModel(Item(InboxMessageType.Directive));
        var sink = RecordingReplySink.Accepting();
        await using var app = BuildApp(readModel, sink);
        await app.StartAsync();
        using var client = AuthorizedClient(app, OrganizationOnlyToken);

        using var response = await client.PostAsJsonAsync(
            $"{InboxEndpointExtensions.BasePath}/acme/inbox/{Uri.EscapeDataString(ItemId)}/reply",
            new InboxReplyRequest("Reply text.", "done"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(readModel.ReadRequests);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task Actor_rejection_is_returned_as_structured_problem_details()
    {
        var readModel = new StaticInboxReadModel(Item(InboxMessageType.Report));
        var sink = new RecordingReplySink((_, command) =>
            OccupantReplyEmissionResult.Rejected(
                command.SourceMessageId,
                new OccupantReplyEmissionError(
                    "reply-not-supported",
                    "sourceMessageId",
                    RejectionReason.InvalidContract)));
        await using var app = BuildApp(readModel, sink);
        await app.StartAsync();
        using var client = AuthorizedClient(app, PersonToken);

        using var response = await client.PostAsJsonAsync(
            $"{InboxEndpointExtensions.BasePath}/acme/inbox/{Uri.EscapeDataString(ItemId)}/reply",
            new InboxReplyRequest("Reply text."));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Inbox reply rejected", problem.GetProperty("title").GetString());
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal("reply-not-supported", error.GetProperty("code").GetString());
        Assert.Equal("sourceMessageId", error.GetProperty("path").GetString());
        Assert.Equal("invalid-contract", error.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Public_document_describes_the_reply_operation_and_contracts()
    {
        var readModel = new StaticInboxReadModel(Item(InboxMessageType.Directive));
        await using var app = BuildApp(readModel, RecordingReplySink.Accepting(), includeOpenApi: true);
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(PublicApiOpenApiExtensions.DocumentPath);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/organizations/{organizationId}/inbox/{itemId}/reply")
            .GetProperty("post");

        Assert.Equal("ReplyToOrganizationInboxItemV1", operation.GetProperty("operationId").GetString());
        Assert.True(operation.GetProperty("responses").TryGetProperty("202", out _));
        Assert.True(document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .TryGetProperty(nameof(InboxReplyRequest), out _));
        Assert.True(document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .TryGetProperty(nameof(InboxReplyResponse), out _));
    }

    private static WebApplication BuildApp(
        IInboxReadModel readModel,
        IInboxReplyCommandSink? replySink,
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
        if (replySink is not null)
        {
            builder.Services.AddSingleton(replySink);
        }

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

    private static InboxItem Item(InboxMessageType type) => new(
        ItemId,
        SourceMessageId,
        "engineer",
        type,
        new InboxMessageEndpoint(InboxMessageEndpointType.Position, "delivery-lead"),
        new InboxMessageEndpoint(InboxMessageEndpointType.Position, "engineer"),
        ThreadId,
        InboxPriority.High,
        At,
        At.AddHours(2),
        InboxReadState.Unread,
        type is InboxMessageType.Directive or InboxMessageType.PeerRequest or InboxMessageType.Escalation
            ? InboxResponseState.AwaitingResponse
            : InboxResponseState.NotApplicable);

    private sealed class StaticInboxReadModel(InboxItem item) : IInboxReadModel
    {
        public List<string> ReadRequests { get; } = [];

        public ValueTask<InboxReadResult<InboxPage>> ListAsync(
            PersonOrganizationScope scope,
            PositionId? positionId,
            InboxListQuery query,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(InboxReadResult<InboxPage>.Available(
                new InboxPage(At, At, query.PageSize, nextCursor: null, [item])));

        public ValueTask<InboxReadResult<InboxItemResponse>> ReadItemAsync(
            PersonOrganizationScope scope,
            string itemId,
            CancellationToken cancellationToken)
        {
            ReadRequests.Add(itemId);
            return ValueTask.FromResult(InboxReadResult<InboxItemResponse>.Available(
                string.Equals(item.ItemId, itemId, StringComparison.Ordinal)
                    ? new InboxItemResponse(At, At, item)
                    : null));
        }
    }

    private sealed class RecordingReplySink(
        Func<PositionEntityId, EmitOccupantReply, OccupantReplyEmissionResult> resultFactory)
        : IInboxReplyCommandSink
    {
        public bool IsAvailable => true;

        public List<(PositionEntityId Position, EmitOccupantReply Command)> Requests { get; } = [];

        public static RecordingReplySink Accepting() => new((position, command) =>
            OccupantReplyEmissionResult.Accepted(
                command.SourceMessageId,
                new Report(
                    command.ReplyMessageId,
                    position.Organization,
                    new PositionEndpointRef(position.Position),
                    new PositionEndpointRef(PositionId.From("delivery-lead")),
                    Hive.Domain.Identity.ThreadId.From(ThreadId),
                    Priority.High,
                    1,
                    At,
                    deadline: null,
                    DirectiveId.From(Guid.Parse("63000000-0000-0000-0000-000000000001")),
                    command.ReportKind ?? ReportKind.Done,
                    command.Body)));

        public ValueTask<OccupantReplyEmissionResult> EmitAsync(
            PositionEntityId sourcePosition,
            EmitOccupantReply command,
            CancellationToken cancellationToken)
        {
            Requests.Add((sourcePosition, command));
            return ValueTask.FromResult(resultFactory(sourcePosition, command));
        }
    }

    private const string PersonToken = "person-token-for-acme";
    private const string OrganizationOnlyToken = "organization-token-for-acme";
}
