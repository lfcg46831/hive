using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Hive.Api.Authorization;
using Hive.Api.Inbox;
using Hive.Api.Organization;
using Hive.Contracts.Inbox;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Inbox.ReadModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hive.Tests;

public sealed class InboxUpdatesHubTests
{
    private static readonly DateTimeOffset ChangedAt =
        new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId OrganizationId = OrganizationId.From("acme");
    private static readonly PositionId DeliveryLead = PositionId.From("delivery-lead");
    private static readonly PositionId Engineer = PositionId.From("engineer");

    [Fact]
    public async Task Person_groups_receive_scoped_inbox_changes_with_monotonic_sequences()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var alice = await ConnectAsync(app, AliceToken);
        using var bob = await ConnectAsync(app, BobToken);
        await SubscribeAsync(alice, "acme", "subscribe-alice");
        await SubscribeAsync(bob, "acme", "subscribe-bob");
        var sink = app.Services.GetRequiredService<IInboxReadModelChangeSink>();

        await sink.ProjectionChangedAsync(Change(
            Item(Engineer, InboxProjectionMessageType.Memo),
            "memo"));
        await sink.ProjectionChangedAsync(Change(
            Item(DeliveryLead, InboxProjectionMessageType.Memo),
            "memo"));
        AssertNotification(
            await ReceiveInvocationAsync(alice),
            sequence: 1,
            InboxChangeType.NewItem,
            DeliveryLead);
        AssertNotification(
            await ReceiveInvocationAsync(bob),
            sequence: 1,
            InboxChangeType.NewItem,
            Engineer);

        var interactionItem = Item(DeliveryLead, InboxProjectionMessageType.Directive);
        var mutation = new InboxInteractionMutation(
            interactionItem.Key,
            "person-alice",
            InboxInteractionAction.MarkRead,
            ChangedAt.AddMinutes(1));
        await sink.InteractionChangedAsync(
            mutation,
            new InboxInteractionState(
                interactionItem.Key,
                mutation.PersonId,
                InboxInteractionReadState.Read,
                InboxInteractionReplyState.NotStarted,
                draftText: null,
                mutation.OccurredAtUtc));
        AssertNotification(
            await ReceiveInvocationAsync(alice),
            sequence: 2,
            InboxChangeType.ReadStateChanged,
            DeliveryLead);

        var approval = Item(
            DeliveryLead,
            InboxProjectionMessageType.ApprovalRequest,
            approval: new InboxProjectionApproval(
                MessageId.New(),
                "deployment.production",
                ApprovalPolicyRef.From("production-change"),
                InboxProjectionApprovalState.Pending,
                DecisionMessageId: null,
                DecidedAtUtc: null));
        approval = approval with
        {
            Approval = approval.Approval! with { RequestId = approval.Key.MessageId },
        };
        await sink.ProjectionChangedAsync(Change(approval, "approval-request", minute: 2));
        AssertNotification(
            await ReceiveInvocationAsync(alice),
            sequence: 3,
            InboxChangeType.ApprovalPending,
            DeliveryLead);

        await sink.ProjectionChangedAsync(Change(
            approval with
            {
                Approval = approval.Approval! with
                {
                    State = InboxProjectionApprovalState.Approved,
                    DecisionMessageId = MessageId.New(),
                    DecidedAtUtc = ChangedAt.AddMinutes(3),
                },
            },
            "ApprovalDecision",
            minute: 3));
        AssertNotification(
            await ReceiveInvocationAsync(alice),
            sequence: 4,
            InboxChangeType.DecisionIssued,
            DeliveryLead);

        var directive = Item(DeliveryLead, InboxProjectionMessageType.Directive);
        await sink.ProjectionChangedAsync(Change(
            directive with { LastReminderAtUtc = ChangedAt.AddMinutes(4) },
            "directive-deadline-approaching",
            minute: 4));
        AssertNotification(
            await ReceiveInvocationAsync(alice),
            sequence: 5,
            InboxChangeType.DeadlineApproaching,
            DeliveryLead);

        await sink.ProjectionChangedAsync(Change(
            directive with { ResponseState = InboxProjectionResponseState.Responded },
            "Report",
            minute: 5));
        AssertNotification(
            await ReceiveInvocationAsync(alice),
            sequence: 6,
            InboxChangeType.ResponseStateChanged,
            DeliveryLead);
    }

    [Theory]
    [InlineData(OrganizationOnlyToken, "acme")]
    [InlineData(AliceToken, "globex")]
    [InlineData(AliceToken, " invalid")]
    public async Task Inbox_subscription_hides_non_person_and_out_of_scope_resources(
        string token,
        string organizationId)
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var socket = await ConnectAsync(app, token);

        await SendInvocationAsync(
            socket,
            "SubscribeToInbox",
            organizationId,
            "hidden-inbox");
        using var completion = JsonDocument.Parse(await ReceiveInvocationMessageAsync(socket));

        Assert.Equal(3, completion.RootElement.GetProperty("type").GetInt32());
        Assert.Contains(
            "Organization not found",
            completion.RootElement.GetProperty("error").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hub_negotiate_keeps_the_shared_bearer_policy_for_inbox_clients()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(
            $"{OrganizationUpdatesEndpointExtensions.HubPath}/negotiate?negotiateVersion=1",
            content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    private static InboxProjectionItem Item(
        PositionId assignedPositionId,
        InboxProjectionMessageType type,
        InboxProjectionApproval? approval = null)
    {
        var messageId = MessageId.New();
        return new InboxProjectionItem(
            new InboxProjectionItemKey(OrganizationId, assignedPositionId, messageId),
            type,
            new PositionEndpointRef(Engineer),
            new PositionEndpointRef(assignedPositionId),
            ThreadId.New(),
            Priority.Normal,
            ChangedAt.AddMinutes(-10),
            ChangedAt.AddHours(1),
            IsExpired: false,
            InboxProjectionResponseState.NotApplicable,
            approval);
    }

    private static InboxProjectionChange Change(
        InboxProjectionItem item,
        string factType,
        int minute = 0) =>
        new(item, factType, ChangedAt.AddMinutes(minute));

    private static void AssertNotification(
        JsonDocument message,
        long sequence,
        InboxChangeType changeType,
        PositionId positionId)
    {
        using (message)
        {
            Assert.Equal(1, message.RootElement.GetProperty("type").GetInt32());
            Assert.Equal(
                "InboxChanged",
                message.RootElement.GetProperty("target").GetString());
            var payload = message.RootElement.GetProperty("arguments")[0];
            Assert.Equal(sequence, payload.GetProperty("sequence").GetInt64());
            Assert.Equal("acme", payload.GetProperty("organization_id").GetString());
            Assert.Equal(
                positionId.Value,
                payload.GetProperty("assigned_position_id").GetString());
            Assert.StartsWith(
                positionId.Value + "/",
                payload.GetProperty("item_id").GetString(),
                StringComparison.Ordinal);
            Assert.Equal(
                changeType.ToString(),
                payload.GetProperty("change_type").GetString());
        }
    }

    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Token"] = AliceToken,
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:OrganizationIds:0"] = "acme",
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:PersonId"] = "person-alice",
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Positions:0:OrganizationId"] = "acme",
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Positions:0:PositionId"] = "delivery-lead",
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:1:Token"] = BobToken,
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:1:OrganizationIds:0"] = "acme",
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:1:PersonId"] = "person-bob",
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:1:Positions:0:OrganizationId"] = "acme",
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:1:Positions:0:PositionId"] = "engineer",
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:2:Token"] = OrganizationOnlyToken,
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:2:OrganizationIds:0"] = "acme",
        });
        builder.Services.AddHiveInboxApi();
        builder.Services.AddHiveOrganizationApi();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHiveOrganizationUpdatesHub();
        return app;
    }

    private static async Task<WebSocket> ConnectAsync(WebApplication app, string token)
    {
        var client = app.GetTestServer().CreateWebSocketClient();
        var uri = new Uri(
            $"ws://localhost{OrganizationUpdatesEndpointExtensions.HubPath}" +
            $"?access_token={Uri.EscapeDataString(token)}");
        var socket = await client.ConnectAsync(uri, CancellationToken.None);
        await SendTextAsync(socket, "{\"protocol\":\"json\",\"version\":1}\u001e");
        Assert.Equal("{}", await ReceiveMessageAsync(socket));
        return socket;
    }

    private static async Task SubscribeAsync(
        WebSocket socket,
        string organizationId,
        string invocationId)
    {
        await SendInvocationAsync(socket, "SubscribeToInbox", organizationId, invocationId);
        using var completion = JsonDocument.Parse(await ReceiveInvocationMessageAsync(socket));
        Assert.Equal(3, completion.RootElement.GetProperty("type").GetInt32());
        Assert.False(completion.RootElement.TryGetProperty("error", out _));
    }

    private static Task SendInvocationAsync(
        WebSocket socket,
        string target,
        string organizationId,
        string invocationId) =>
        SendTextAsync(
            socket,
            JsonSerializer.Serialize(new
            {
                type = 1,
                invocationId,
                target,
                arguments = new[] { organizationId },
            }) + "\u001e");

    private static Task SendTextAsync(WebSocket socket, string payload) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(payload),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);

    private static async Task<JsonDocument> ReceiveInvocationAsync(WebSocket socket) =>
        JsonDocument.Parse(await ReceiveInvocationMessageAsync(socket));

    private static async Task<string> ReceiveInvocationMessageAsync(WebSocket socket)
    {
        while (true)
        {
            var payload = await ReceiveMessageAsync(socket);
            using var message = JsonDocument.Parse(payload);
            if (message.RootElement.GetProperty("type").GetInt32() != 6)
            {
                return payload;
            }
        }
    }

    private static async Task<string> ReceiveMessageAsync(WebSocket socket)
    {
        var buffer = new byte[8_192];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray()).TrimEnd('\u001e');
    }

    private const string AliceToken = "person-token-for-alice";
    private const string BobToken = "person-token-for-bob";
    private const string OrganizationOnlyToken = "organization-token-for-acme";
}
