using Hive.Api.Authorization;
using Hive.Api.Inbox;
using Hive.Contracts.Inbox;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Inbox.ReadModels;

namespace Hive.Tests;

public sealed class ProjectionInboxReadModelTests
{
    private static readonly OrganizationId OrganizationId = OrganizationId.From("acme");

    private static readonly PositionId DeliveryLead = PositionId.From("delivery-lead");

    private static readonly PositionId Engineer = PositionId.From("engineer");

    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Watermark = GeneratedAt.AddSeconds(-3);

    [Fact]
    public async Task Aggregate_read_passes_only_occupied_positions_and_defensively_filters_results()
    {
        var authorized = Item(DeliveryLead, 1);
        var unauthorized = Item(Engineer, 2);
        var reader = new RecordingSnapshotReader([authorized, unauthorized]);
        var interactionReader = new RecordingInteractionReader();
        var readModel = new ProjectionInboxReadModel(
            reader,
            interactionReader,
            new FixedTimeProvider(GeneratedAt));
        var scope = Scope(DeliveryLead);

        var result = await readModel.ListAsync(
            scope,
            positionId: null,
            new InboxListQuery(),
            CancellationToken.None);

        Assert.True(result.IsAvailable);
        var page = Assert.IsType<InboxPage>(result.Value);
        var item = Assert.Single(page.Items);
        Assert.Equal(DeliveryLead.Value, item.AssignedPositionId);
        Assert.Equal(GeneratedAt, page.GeneratedAtUtc);
        Assert.Equal(Watermark, page.LastEventAppliedAtUtc);
        var request = Assert.Single(reader.Requests);
        Assert.Equal(OrganizationId, request.OrganizationId);
        Assert.Equal([DeliveryLead], request.PositionIds);
        var interactionRequest = Assert.Single(interactionReader.Requests);
        Assert.Equal("person-alice", interactionRequest.PersonId);
        Assert.Equal([authorized.Key], interactionRequest.ItemKeys);
    }

    [Fact]
    public async Task Detail_read_hides_an_item_returned_for_a_position_outside_the_scope()
    {
        var unauthorized = Item(Engineer, 2);
        var reader = new RecordingSnapshotReader([unauthorized]);
        var readModel = new ProjectionInboxReadModel(
            reader,
            new RecordingInteractionReader(),
            new FixedTimeProvider(GeneratedAt));

        var result = await readModel.ReadItemAsync(
            Scope(DeliveryLead),
            PublicItemId(unauthorized),
            CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Null(result.Value);
        Assert.Equal([DeliveryLead], Assert.Single(reader.Requests).PositionIds);
    }

    [Fact]
    public async Task Position_read_rejects_an_unoccupied_position_without_querying_the_projection()
    {
        var reader = new RecordingSnapshotReader([Item(Engineer, 2)]);
        var readModel = new ProjectionInboxReadModel(
            reader,
            new RecordingInteractionReader(),
            new FixedTimeProvider(GeneratedAt));

        var result = await readModel.ListAsync(
            Scope(DeliveryLead),
            Engineer,
            new InboxListQuery(),
            CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Null(result.Value);
        Assert.Empty(reader.Requests);
    }

    [Fact]
    public async Task Person_interaction_overlays_read_and_in_progress_without_overriding_derived_response()
    {
        var inProgress = Item(
            DeliveryLead,
            1,
            InboxProjectionResponseState.AwaitingResponse);
        var responded = Item(
            DeliveryLead,
            2,
            InboxProjectionResponseState.Responded);
        var interactionReader = new RecordingInteractionReader(
            new Dictionary<InboxProjectionItemKey, InboxInteractionState>
            {
                [inProgress.Key] = Interaction(inProgress.Key),
                [responded.Key] = Interaction(responded.Key),
            });
        var readModel = new ProjectionInboxReadModel(
            new RecordingSnapshotReader([inProgress, responded]),
            interactionReader,
            new FixedTimeProvider(GeneratedAt));

        var result = await readModel.ListAsync(
            Scope(DeliveryLead),
            positionId: null,
            new InboxListQuery(),
            CancellationToken.None);

        var page = Assert.IsType<InboxPage>(result.Value);
        Assert.All(page.Items, item => Assert.Equal(InboxReadState.Read, item.ReadState));
        Assert.Equal(
            InboxResponseState.InProgress,
            Assert.Single(page.Items, item => item.MessageId == inProgress.Key.MessageId.Value)
                .ResponseState);
        Assert.Equal(
            InboxResponseState.Responded,
            Assert.Single(page.Items, item => item.MessageId == responded.Key.MessageId.Value)
                .ResponseState);
    }

    private static PersonOrganizationScope Scope(params PositionId[] positionIds) =>
        new("person-alice", OrganizationId, positionIds);

    private static InboxProjectionItem Item(
        PositionId assignedPositionId,
        int ordinal,
        InboxProjectionResponseState responseState = InboxProjectionResponseState.NotApplicable)
    {
        var messageId = MessageId.From(Guid.Parse($"10000000-0000-0000-0000-{ordinal:D12}"));
        return new InboxProjectionItem(
            new InboxProjectionItemKey(OrganizationId, assignedPositionId, messageId),
            InboxProjectionMessageType.Memo,
            new PositionEndpointRef(PositionId.From("ceo")),
            new PositionEndpointRef(assignedPositionId),
            ThreadId.From(Guid.Parse($"20000000-0000-0000-0000-{ordinal:D12}")),
            Priority.Normal,
            GeneratedAt.AddMinutes(-ordinal),
            DeadlineAtUtc: null,
            IsExpired: false,
            responseState,
            Approval: null);
    }

    private static InboxInteractionState Interaction(InboxProjectionItemKey itemKey) =>
        new(
            itemKey,
            "person-alice",
            InboxInteractionReadState.Read,
            InboxInteractionReplyState.InProgress,
            draftText: "Work in progress",
            GeneratedAt.AddMinutes(-1));

    private static string PublicItemId(InboxProjectionItem item) =>
        $"{item.Key.AssignedPositionId.Value}/{item.Key.MessageId}";

    private sealed class RecordingSnapshotReader(
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

    private sealed class RecordingInteractionReader(
        IReadOnlyDictionary<InboxProjectionItemKey, InboxInteractionState>? states = null) :
        IInboxInteractionReader
    {
        private readonly IReadOnlyDictionary<InboxProjectionItemKey, InboxInteractionState> _states =
            states ?? new Dictionary<InboxProjectionItemKey, InboxInteractionState>();

        public bool IsAvailable => true;

        public List<(
            OrganizationId OrganizationId,
            string PersonId,
            InboxProjectionItemKey[] ItemKeys)> Requests
        { get; } = [];

        public ValueTask<IReadOnlyDictionary<InboxProjectionItemKey, InboxInteractionState>>
            ReadAsync(
                OrganizationId organizationId,
                string personId,
                IReadOnlyCollection<InboxProjectionItemKey> itemKeys,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((organizationId, personId, itemKeys.ToArray()));
            return ValueTask.FromResult(_states);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
