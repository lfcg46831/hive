using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Hive.Api.Authorization;
using Hive.Api.Organization;
using Hive.Contracts.Organization;
using Hive.Domain.Identity;
using Hive.Infrastructure.Organization.ReadModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hive.Tests;

public sealed class OrganizationUpdatesHubTests
{
    private static readonly DateTimeOffset ChangedAt =
        new(2026, 8, 3, 11, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Authorized_browser_connection_receives_grouped_organogram_and_state_events()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var socket = await ConnectAsync(app, OrganizationToken);
        await SubscribeAsync(socket, "acme", "subscribe-acme");
        var sink = app.Services.GetRequiredService<IOrganizationReadModelChangeSink>();

        await sink.OrganogramChangedAsync(
            OrganizationId.From("globex"),
            3,
            Fingerprint,
            ChangedAt.AddMinutes(-1));
        await sink.OrganogramChangedAsync(
            OrganizationId.From("acme"),
            7,
            Fingerprint,
            ChangedAt);
        using var organogramEvent = JsonDocument.Parse(await ReceiveMessageAsync(socket));
        await sink.PositionStateChangedAsync(
            OrganizationId.From("acme"),
            PositionId.From("delivery-lead"));
        using var stateEvent = JsonDocument.Parse(await ReceiveMessageAsync(socket));

        Assert.Equal(1, organogramEvent.RootElement.GetProperty("type").GetInt32());
        Assert.Equal(
            "OrganogramChanged",
            organogramEvent.RootElement.GetProperty("target").GetString());
        var organogramPayload = organogramEvent.RootElement
            .GetProperty("arguments")[0];
        Assert.Equal("acme", organogramPayload.GetProperty("organization_id").GetString());
        Assert.Equal(7, organogramPayload.GetProperty("registry").GetProperty("version").GetInt64());
        Assert.Equal(ChangedAt, organogramPayload.GetProperty("changed_at_utc").GetDateTimeOffset());

        Assert.Equal(
            "PositionStateChanged",
            stateEvent.RootElement.GetProperty("target").GetString());
        var statePayload = stateEvent.RootElement.GetProperty("arguments")[0];
        Assert.Equal("acme", statePayload.GetProperty("organization_id").GetString());
        Assert.Equal(
            "delivery-lead",
            statePayload.GetProperty("state").GetProperty("position_id").GetString());
        Assert.Equal(12, statePayload.GetProperty("state").GetProperty("sequence").GetInt64());
    }

    [Theory]
    [InlineData("acme")]
    [InlineData(" invalid")]
    public async Task Subscription_hides_invalid_or_out_of_scope_organizations(
        string organizationId)
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var socket = await ConnectAsync(app, OtherOrganizationToken);

        await SendInvocationAsync(
            socket,
            "SubscribeToOrganization",
            organizationId,
            "hidden-organization");
        using var completion = JsonDocument.Parse(await ReceiveInvocationMessageAsync(socket));

        Assert.Equal(3, completion.RootElement.GetProperty("type").GetInt32());
        Assert.Equal(
            "hidden-organization",
            completion.RootElement.GetProperty("invocationId").GetString());
        Assert.Contains(
            "Organization not found",
            completion.RootElement.GetProperty("error").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hub_negotiate_requires_the_shared_bearer_policy()
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

    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Token"] =
                    OrganizationToken,
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:OrganizationIds:0"] =
                    "acme",
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:1:Token"] =
                    OtherOrganizationToken,
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:1:OrganizationIds:0"] =
                    "globex",
            });
        builder.Services.AddSingleton<IOrganizationReadModel>(new HubReadModel());
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
        await SendInvocationAsync(
            socket,
            "SubscribeToOrganization",
            organizationId,
            invocationId);
        using var completion = JsonDocument.Parse(await ReceiveInvocationMessageAsync(socket));
        Assert.Equal(3, completion.RootElement.GetProperty("type").GetInt32());
        Assert.Equal(
            invocationId,
            completion.RootElement.GetProperty("invocationId").GetString());
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

        var payload = Encoding.UTF8.GetString(stream.ToArray());
        return payload.TrimEnd('\u001e');
    }

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

    private sealed class HubReadModel : IOrganizationReadModel
    {
        public ValueTask<OrganizationReadResult<OrganogramResponse>> ReadOrganogramAsync(
            OrganizationId organizationId,
            UnitId? rootUnitId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OrganizationReadResult<OrganogramResponse>.Available(null));

        public ValueTask<OrganizationReadResult<PositionDetailResponse>> ReadPositionAsync(
            OrganizationId organizationId,
            PositionId positionId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OrganizationReadResult<PositionDetailResponse>.Available(
                new PositionDetailResponse(
                    new RegistryVersion(7, Fingerprint),
                    ChangedAt,
                    new OrganizationPosition(
                        positionId.Value,
                        "Delivery Lead",
                        "delivery",
                        new OrganizationOccupant(
                            $"configured-ai:{organizationId.Value}/{positionId.Value}",
                            OrganizationOccupantType.AiAgent),
                        new PositionHierarchy(null, []),
                        new OrganizationPositionState(
                            positionId.Value,
                            PositionOperationalState.Working,
                            12,
                            ChangedAt)))));

        public ValueTask<OrganizationReadResult<PositionStatesResponse>> ReadPositionStatesAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OrganizationReadResult<PositionStatesResponse>.Available(null));
    }

    private const string OrganizationToken = "test-token-for-acme";

    private const string OtherOrganizationToken = "test-token-for-globex";

    private const string Fingerprint =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
}
