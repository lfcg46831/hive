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
        var readModel = new ProjectionInboxReadModel(
            reader,
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
    }

    [Fact]
    public async Task Detail_read_hides_an_item_returned_for_a_position_outside_the_scope()
    {
        var unauthorized = Item(Engineer, 2);
        var reader = new RecordingSnapshotReader([unauthorized]);
        var readModel = new ProjectionInboxReadModel(
            reader,
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

    private static PersonOrganizationScope Scope(params PositionId[] positionIds) =>
        new("person-alice", OrganizationId, positionIds);

    private static InboxProjectionItem Item(PositionId assignedPositionId, int ordinal)
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
            InboxProjectionResponseState.NotApplicable,
            Approval: null);
    }

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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
