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

namespace Hive.Tests;

public sealed class InboxDecisionEndpointTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid RequestId =
        Guid.Parse("71000000-0000-0000-0000-000000000001");
    private static readonly Guid ThreadId =
        Guid.Parse("72000000-0000-0000-0000-000000000001");
    private const string ItemId =
        "ceo/71000000-0000-0000-0000-000000000001";

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "The operational risk is too high.")]
    public async Task Authorized_decision_is_emitted_by_the_occupied_approver_position(
        bool approved,
        string? reason)
    {
        var readModel = new StaticInboxReadModel(Item(canDecide: true));
        var sink = RecordingDecisionSink.Accepting();
        await using var app = BuildApp(readModel, sink);
        await app.StartAsync();
        using var client = AuthorizedClient(app, PersonToken);

        using var response = await client.PostAsJsonAsync(
            $"{InboxEndpointExtensions.BasePath}/acme/inbox/{Uri.EscapeDataString(ItemId)}/decision",
            new InboxDecisionRequest(approved, reason));
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var request = Assert.Single(sink.Requests);
        Assert.Equal("acme/ceo", request.Position.Value);
        Assert.Equal(RequestId, request.Command.RequestId.Value);
        Assert.NotEqual(Guid.Empty, request.Command.DecisionMessageId.Value);
        Assert.NotEqual(RequestId, request.Command.DecisionMessageId.Value);
        Assert.Equal(OccupantReplyAuthorKind.HumanUser, request.Command.Author.Kind);
        Assert.Equal("person-alice", request.Command.Author.SubjectId);
        Assert.Equal("web-inbox", request.Command.Author.Channel);
        Assert.Equal(approved, request.Command.Approved);
        Assert.Equal(reason, request.Command.Reason);
        Assert.Equal(RequestId, payload.GetProperty("request_id").GetGuid());
        Assert.Equal(request.Command.DecisionMessageId.Value, payload.GetProperty("message_id").GetGuid());
        Assert.Equal(approved, payload.GetProperty("approved").GetBoolean());
        Assert.Equal("ceo", payload.GetProperty("from_position_id").GetString());
        Assert.Equal("delivery-lead", payload.GetProperty("to_position_id").GetString());
        Assert.Equal(ThreadId, payload.GetProperty("thread_id").GetGuid());
        if (reason is null)
        {
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("reason").ValueKind);
        }
        else
        {
            Assert.Equal(reason, payload.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task Decision_requires_an_explicit_boolean_and_a_well_formed_optional_reason()
    {
        var readModel = new StaticInboxReadModel(Item(canDecide: true));
        var sink = RecordingDecisionSink.Accepting();
        await using var app = BuildApp(readModel, sink);
        await app.StartAsync();
        using var client = AuthorizedClient(app, PersonToken);
        var path = $"{InboxEndpointExtensions.BasePath}/acme/inbox/{Uri.EscapeDataString(ItemId)}/decision";

        using var missingDecision = await client.PostAsJsonAsync(
            path,
            new InboxDecisionRequest(approved: null));
        using var invalidReason = await client.PostAsJsonAsync(
            path,
            new InboxDecisionRequest(approved: true, " padded "));
        var missingProblem = await missingDecision.Content.ReadFromJsonAsync<ProblemDetails>();
        var reasonProblem = await invalidReason.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, missingDecision.StatusCode);
        Assert.Equal("Invalid inbox decision", missingProblem!.Title);
        Assert.Equal(HttpStatusCode.BadRequest, invalidReason.StatusCode);
        Assert.Equal("Invalid inbox decision", reasonProblem!.Title);
        Assert.Empty(sink.Requests);
    }

    [Theory]
    [InlineData(
        ApprovalValidationCatalog.Codes.UnauthorizedApprover,
        "from",
        "unauthorized",
        InboxApprovalState.Pending,
        false)]
    [InlineData(
        ApprovalValidationCatalog.Codes.ApprovalDecisionDuplicate,
        "requestId",
        "duplicate",
        InboxApprovalState.Approved,
        false)]
    [InlineData(
        ApprovalValidationCatalog.Codes.ApprovalDecisionExpired,
        "requestId",
        "expired",
        InboxApprovalState.Expired,
        false)]
    [InlineData(
        ApprovalValidationCatalog.Codes.ApprovalRequestNotFound,
        "requestId",
        "invalid-route",
        InboxApprovalState.Pending,
        true)]
    public async Task Governance_rejection_is_returned_as_structured_problem_from_the_pipeline(
        string code,
        string path,
        string reason,
        InboxApprovalState state,
        bool canDecide)
    {
        var readModel = new StaticInboxReadModel(Item(canDecide, state));
        var sink = RecordingDecisionSink.Rejecting(code, path, reason);
        await using var app = BuildApp(readModel, sink);
        await app.StartAsync();
        using var client = AuthorizedClient(app, PersonToken);

        using var response = await client.PostAsJsonAsync(
            $"{InboxEndpointExtensions.BasePath}/acme/inbox/{Uri.EscapeDataString(ItemId)}/decision",
            new InboxDecisionRequest(approved: true));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Inbox decision rejected", problem.GetProperty("title").GetString());
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(path, error.GetProperty("path").GetString());
        Assert.Equal(reason, error.GetProperty("reason").GetString());
        Assert.Single(sink.Requests);
    }

    [Fact]
    public async Task Principal_without_person_position_scope_cannot_decide_an_approval()
    {
        var readModel = new StaticInboxReadModel(Item(canDecide: true));
        var sink = RecordingDecisionSink.Accepting();
        await using var app = BuildApp(readModel, sink);
        await app.StartAsync();
        using var client = AuthorizedClient(app, OrganizationOnlyToken);

        using var response = await client.PostAsJsonAsync(
            $"{InboxEndpointExtensions.BasePath}/acme/inbox/{Uri.EscapeDataString(ItemId)}/decision",
            new InboxDecisionRequest(approved: true));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(readModel.ReadRequests);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task Public_document_describes_the_decision_operation_and_contracts()
    {
        var readModel = new StaticInboxReadModel(Item(canDecide: true));
        await using var app = BuildApp(
            readModel,
            RecordingDecisionSink.Accepting(),
            includeOpenApi: true);
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(PublicApiOpenApiExtensions.DocumentPath);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/organizations/{organizationId}/inbox/{itemId}/decision")
            .GetProperty("post");

        Assert.Equal(
            "DecideOrganizationInboxApprovalV1",
            operation.GetProperty("operationId").GetString());
        Assert.True(operation.GetProperty("responses").TryGetProperty("202", out _));
        Assert.True(document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .TryGetProperty(nameof(InboxDecisionRequest), out _));
        Assert.True(document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .TryGetProperty(nameof(InboxDecisionResponse), out _));
    }

    private static WebApplication BuildApp(
        IInboxReadModel readModel,
        IInboxDecisionCommandSink decisionSink,
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
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Positions:0:PositionId"] = "ceo",
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:1:Token"] = OrganizationOnlyToken,
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:1:OrganizationIds:0"] = "acme",
        });
        builder.Services.AddSingleton(readModel);
        builder.Services.AddSingleton(decisionSink);
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

    private static InboxItem Item(
        bool canDecide,
        InboxApprovalState state = InboxApprovalState.Pending) => new(
        ItemId,
        RequestId,
        "ceo",
        InboxMessageType.ApprovalRequest,
        new InboxMessageEndpoint(InboxMessageEndpointType.Position, "delivery-lead"),
        new InboxMessageEndpoint(InboxMessageEndpointType.Position, "ceo"),
        ThreadId,
        InboxPriority.Critical,
        At,
        At.AddHours(2),
        InboxReadState.Unread,
        InboxResponseState.NotApplicable,
        new InboxApprovalMetadata(
            RequestId,
            "publish external release statement",
            "comms.external-official",
            state,
            canDecide,
            state is InboxApprovalState.Approved or InboxApprovalState.Rejected
                ? Guid.Parse("73000000-0000-0000-0000-000000000001")
                : null,
            state is InboxApprovalState.Approved or InboxApprovalState.Rejected
                ? At.AddMinutes(15)
                : null));

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

    private sealed class RecordingDecisionSink(
        Func<PositionEntityId, EmitOccupantApprovalDecision, OccupantReplyEmissionResult> resultFactory)
        : IInboxDecisionCommandSink
    {
        public bool IsAvailable => true;

        public List<(PositionEntityId Position, EmitOccupantApprovalDecision Command)> Requests { get; } = [];

        public static RecordingDecisionSink Accepting() => new((position, command) =>
            OccupantReplyEmissionResult.Accepted(
                command.RequestId,
                new ApprovalDecision(
                    command.DecisionMessageId,
                    position.Organization,
                    new PositionEndpointRef(position.Position),
                    new PositionEndpointRef(PositionId.From("delivery-lead")),
                    Hive.Domain.Identity.ThreadId.From(ThreadId),
                    Priority.Critical,
                    1,
                    At,
                    deadline: null,
                    command.RequestId,
                    command.Approved,
                    command.Reason)));

        public static RecordingDecisionSink Rejecting(
            string code,
            string path,
            string reason) => new((_, command) =>
                OccupantReplyEmissionResult.Rejected(
                    command.RequestId,
                    new OccupantReplyEmissionError(
                        code,
                        path,
                        RejectionReasonContract.ParseWireValue(reason))));

        public ValueTask<OccupantReplyEmissionResult> EmitAsync(
            PositionEntityId sourcePosition,
            EmitOccupantApprovalDecision command,
            CancellationToken cancellationToken)
        {
            Requests.Add((sourcePosition, command));
            return ValueTask.FromResult(resultFactory(sourcePosition, command));
        }
    }

    private const string PersonToken = "person-token-for-acme";
    private const string OrganizationOnlyToken = "organization-token-for-acme";
}
