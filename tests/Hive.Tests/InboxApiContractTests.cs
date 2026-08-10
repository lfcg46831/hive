using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Api.Authorization;
using Hive.Api.Inbox;
using Hive.Contracts.Inbox;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Inbox.ReadModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests;

public sealed class InboxApiContractTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Engineer = PositionId.From("engineer");
    private static readonly PositionId FinanceLead = PositionId.From("finance-lead");
    private static readonly PositionId ChiefExecutive = PositionId.From("ceo");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 10, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Watermark = Now.AddSeconds(-3);
    private static readonly Guid EngineerMessageId =
        Guid.Parse("91000000-0000-0000-0000-000000000001");
    private static readonly Guid FinanceMessageId =
        Guid.Parse("91000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task Public_reads_defensively_expose_only_the_persons_occupied_positions()
    {
        var snapshotReader = new StaticSnapshotReader(
        [
            Item(Engineer, EngineerMessageId),
            Item(FinanceLead, FinanceMessageId),
        ]);
        var interactionStore = new InMemoryInteractionStore();
        await using var app = BuildApp(snapshotReader, interactionStore);
        await app.StartAsync();
        using var client = AuthorizedClient(app);
        var basePath = $"{InboxEndpointExtensions.BasePath}/{Organization.Value}";

        var aggregate = await client.GetFromJsonAsync<JsonElement>($"{basePath}/inbox");
        var exposedItem = Assert.Single(aggregate.GetProperty("items").EnumerateArray());

        Assert.Equal(Engineer.Value, exposedItem.GetProperty("assigned_position_id").GetString());
        Assert.Equal(EngineerMessageId, exposedItem.GetProperty("message_id").GetGuid());

        var requestsBeforePositionChecks = snapshotReader.Requests.Count;
        using var unoccupiedPosition = await client.GetAsync(
            $"{basePath}/positions/{FinanceLead.Value}/inbox");
        using var unknownPosition = await client.GetAsync(
            $"{basePath}/positions/unknown-position/inbox");

        await AssertEquivalentNotFoundAsync(unoccupiedPosition, unknownPosition);
        Assert.Equal(requestsBeforePositionChecks, snapshotReader.Requests.Count);

        using var unoccupiedItem = await client.GetAsync(
            $"{basePath}/inbox/{Uri.EscapeDataString(PublicItemId(FinanceLead, FinanceMessageId))}");
        var unknownMessageId = Guid.Parse("91000000-0000-0000-0000-000000000099");
        using var unknownItem = await client.GetAsync(
            $"{basePath}/inbox/" +
            Uri.EscapeDataString(PublicItemId(FinanceLead, unknownMessageId)));

        await AssertEquivalentNotFoundAsync(unoccupiedItem, unknownItem);
        Assert.All(snapshotReader.Requests, request =>
        {
            Assert.Equal(Organization, request.OrganizationId);
            Assert.Equal([Engineer], request.PositionIds);
        });
    }

    [Fact]
    public async Task Interaction_writes_are_observed_by_subsequent_list_and_detail_reads()
    {
        var snapshotReader = new StaticSnapshotReader([Item(Engineer, EngineerMessageId)]);
        var interactionStore = new InMemoryInteractionStore();
        await using var app = BuildApp(snapshotReader, interactionStore);
        await app.StartAsync();
        using var client = AuthorizedClient(app);
        var itemId = PublicItemId(Engineer, EngineerMessageId);
        var itemPath =
            $"{InboxEndpointExtensions.BasePath}/{Organization.Value}/inbox/" +
            Uri.EscapeDataString(itemId);

        using var markRead = await client.PostAsync($"{itemPath}/read", content: null);
        using var saveDraft = await client.PostAsJsonAsync(
            $"{itemPath}/draft",
            new InboxDraftRequest("Investigating the dependency failure."));
        var filtered = await client.GetFromJsonAsync<JsonElement>(
            $"{InboxEndpointExtensions.BasePath}/{Organization.Value}/inbox" +
            "?read_state=Read&response_state=InProgress");
        var detail = await client.GetFromJsonAsync<JsonElement>(itemPath);

        Assert.Equal(HttpStatusCode.OK, markRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, saveDraft.StatusCode);
        var listedItem = Assert.Single(filtered.GetProperty("items").EnumerateArray());
        Assert.Equal(itemId, listedItem.GetProperty("item_id").GetString());
        Assert.Equal("Read", listedItem.GetProperty("read_state").GetString());
        Assert.Equal("InProgress", listedItem.GetProperty("response_state").GetString());
        Assert.Equal("Read", detail.GetProperty("item").GetProperty("read_state").GetString());
        Assert.Equal(
            "InProgress",
            detail.GetProperty("item").GetProperty("response_state").GetString());
        Assert.Equal(
            "Investigating the dependency failure.",
            detail.GetProperty("draft_text").GetString());
        Assert.Equal(
            [InboxInteractionAction.MarkRead, InboxInteractionAction.SaveDraft],
            interactionStore.Mutations.Select(static mutation => mutation.Action));
        Assert.All(interactionStore.Mutations, mutation =>
        {
            Assert.Equal("person-alice", mutation.PersonId);
            Assert.Equal(Organization, mutation.ItemKey.OrganizationId);
            Assert.Equal(Engineer, mutation.ItemKey.AssignedPositionId);
            Assert.Equal(EngineerMessageId, mutation.ItemKey.MessageId.Value);
        });
    }

    private static async Task AssertEquivalentNotFoundAsync(
        HttpResponseMessage existing,
        HttpResponseMessage unknown)
    {
        Assert.Equal(HttpStatusCode.NotFound, existing.StatusCode);
        Assert.Equal(existing.StatusCode, unknown.StatusCode);
        Assert.Equal(
            existing.Content.Headers.ContentType?.ToString(),
            unknown.Content.Headers.ContentType?.ToString());
        Assert.Equal(
            await existing.Content.ReadAsStringAsync(),
            await unknown.Content.ReadAsStringAsync());
    }

    private static WebApplication BuildApp(
        StaticSnapshotReader snapshotReader,
        InMemoryInteractionStore interactionStore)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Token"] = PersonToken,
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:OrganizationIds:0"] =
                Organization.Value,
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:PersonId"] =
                "person-alice",
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Positions:0:OrganizationId"] =
                Organization.Value,
            [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Positions:0:PositionId"] =
                Engineer.Value,
        });
        builder.Services.AddSingleton<IInboxProjectionSnapshotReader>(snapshotReader);
        builder.Services.AddSingleton<IInboxInteractionStore>(interactionStore);
        builder.Services.AddSingleton<IInboxInteractionReader>(interactionStore);
        builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        builder.Services.AddHiveInboxApi();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHiveInboxApi();
        return app;
    }

    private static HttpClient AuthorizedClient(WebApplication app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", PersonToken);
        return client;
    }

    private static InboxProjectionItem Item(PositionId assignedPosition, Guid messageId) => new(
        new InboxProjectionItemKey(
            Organization,
            assignedPosition,
            MessageId.From(messageId)),
        InboxProjectionMessageType.Directive,
        new PositionEndpointRef(ChiefExecutive),
        new PositionEndpointRef(assignedPosition),
        ThreadId.From(Guid.Parse(
            assignedPosition == Engineer
                ? "92000000-0000-0000-0000-000000000001"
                : "92000000-0000-0000-0000-000000000002")),
        Priority.High,
        Now.AddMinutes(-5),
        Now.AddHours(2),
        IsExpired: false,
        InboxProjectionResponseState.AwaitingResponse,
        Approval: null);

    private static string PublicItemId(PositionId positionId, Guid messageId) =>
        $"{positionId.Value}/{messageId:D}";

    private sealed class StaticSnapshotReader(
        IReadOnlyList<InboxProjectionItem> items) : IInboxProjectionSnapshotReader
    {
        public bool IsAvailable => true;

        public List<(OrganizationId OrganizationId, PositionId[] PositionIds)> Requests { get; } = [];

        public ValueTask<InboxProjectionSnapshot> ReadAsync(
            OrganizationId organizationId,
            IReadOnlyCollection<PositionId> assignedPositionIds,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((organizationId, assignedPositionIds.ToArray()));
            return ValueTask.FromResult(new InboxProjectionSnapshot(
                organizationId,
                Watermark,
                items));
        }
    }

    private sealed class InMemoryInteractionStore : IInboxInteractionStore
    {
        private readonly Dictionary<
            (InboxProjectionItemKey ItemKey, string PersonId),
            InboxInteractionState> _states = [];

        public bool IsAvailable => true;

        public List<InboxInteractionMutation> Mutations { get; } = [];

        public ValueTask<IReadOnlyDictionary<InboxProjectionItemKey, InboxInteractionState>>
            ReadAsync(
                OrganizationId organizationId,
                string personId,
                IReadOnlyCollection<InboxProjectionItemKey> itemKeys,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestedKeys = itemKeys.ToHashSet();
            IReadOnlyDictionary<InboxProjectionItemKey, InboxInteractionState> result = _states
                .Where(entry =>
                    entry.Key.PersonId == personId &&
                    entry.Key.ItemKey.OrganizationId == organizationId &&
                    requestedKeys.Contains(entry.Key.ItemKey))
                .ToDictionary(entry => entry.Key.ItemKey, entry => entry.Value);
            return ValueTask.FromResult(result);
        }

        public ValueTask<InboxInteractionState> ApplyAsync(
            InboxInteractionMutation mutation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (mutation.ItemKey, mutation.PersonId);
            _states.TryGetValue(key, out var current);
            var readState = mutation.Action switch
            {
                InboxInteractionAction.MarkRead => InboxInteractionReadState.Read,
                InboxInteractionAction.MarkUnread => InboxInteractionReadState.Unread,
                _ => current?.ReadState ?? InboxInteractionReadState.Unread,
            };
            var replyState = mutation.Action is
                InboxInteractionAction.StartReply or
                InboxInteractionAction.SaveDraft or
                InboxInteractionAction.ClearDraft
                    ? InboxInteractionReplyState.InProgress
                    : current?.ReplyState ?? InboxInteractionReplyState.NotStarted;
            var draftText = mutation.Action switch
            {
                InboxInteractionAction.SaveDraft => mutation.DraftText,
                InboxInteractionAction.ClearDraft => null,
                _ => current?.DraftText,
            };
            var state = new InboxInteractionState(
                mutation.ItemKey,
                mutation.PersonId,
                readState,
                replyState,
                draftText,
                mutation.OccurredAtUtc);
            _states[key] = state;
            Mutations.Add(mutation);
            return ValueTask.FromResult(state);
        }

        public ValueTask<IReadOnlyList<InboxInteractionAuditEntry>> ReadAuditAsync(
            InboxProjectionItemKey itemKey,
            string personId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<InboxInteractionAuditEntry>>([]);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private const string PersonToken = "api-contract-person-token";
}
