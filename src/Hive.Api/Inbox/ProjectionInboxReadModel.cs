using Hive.Api.Authorization;
using Hive.Contracts.Inbox;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Inbox.ReadModels;

namespace Hive.Api.Inbox;

internal sealed class ProjectionInboxReadModel(
    IInboxProjectionSnapshotReader snapshotReader,
    TimeProvider timeProvider) : IInboxReadModel
{
    public async ValueTask<InboxReadResult<InboxPage>> ListAsync(
        PersonOrganizationScope scope,
        PositionId? positionId,
        InboxListQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (!snapshotReader.IsAvailable)
        {
            return InboxReadResult<InboxPage>.Unavailable;
        }

        if (positionId is not null && !scope.Occupies(positionId))
        {
            return InboxReadResult<InboxPage>.Available(null);
        }

        IReadOnlyCollection<PositionId> effectivePositions = positionId is null
            ? scope.PositionIds
            : [positionId];
        var snapshot = await snapshotReader.ReadAsync(
                scope.OrganizationId,
                effectivePositions,
                cancellationToken)
            .ConfigureAwait(false);
        var authorizedPositions = effectivePositions.ToHashSet();
        var filtered = snapshot.Items
            .Where(item => authorizedPositions.Contains(item.Key.AssignedPositionId))
            .Select(MapItem)
            .Where(item => Matches(item, query))
            .ToArray();
        var afterCursor = AfterCursor(filtered, query.Cursor);
        var pageItems = afterCursor.Take(query.PageSize).ToArray();
        var nextCursor = afterCursor.Length > pageItems.Length
            ? pageItems[^1].ItemId
            : null;
        var response = new InboxPage(
            timeProvider.GetUtcNow(),
            snapshot.LastEventAppliedAtUtc,
            query.PageSize,
            nextCursor,
            pageItems);
        return InboxReadResult<InboxPage>.Available(response);
    }

    public async ValueTask<InboxReadResult<InboxItemResponse>> ReadItemAsync(
        PersonOrganizationScope scope,
        string itemId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(itemId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!snapshotReader.IsAvailable)
        {
            return InboxReadResult<InboxItemResponse>.Unavailable;
        }

        var snapshot = await snapshotReader.ReadAsync(
                scope.OrganizationId,
                scope.PositionIds,
                cancellationToken)
            .ConfigureAwait(false);
        var authorizedPositions = scope.PositionIds.ToHashSet();
        var item = snapshot.Items
            .Where(candidate => authorizedPositions.Contains(
                candidate.Key.AssignedPositionId))
            .Select(MapItem)
            .SingleOrDefault(candidate =>
                string.Equals(candidate.ItemId, itemId, StringComparison.Ordinal));
        return InboxReadResult<InboxItemResponse>.Available(
            item is null
                ? null
                : new InboxItemResponse(
                    timeProvider.GetUtcNow(),
                    snapshot.LastEventAppliedAtUtc,
                    item));
    }

    private static InboxItem[] AfterCursor(InboxItem[] items, string? cursor)
    {
        if (cursor is null)
        {
            return items;
        }

        var cursorIndex = Array.FindIndex(
            items,
            item => string.Equals(item.ItemId, cursor, StringComparison.Ordinal));
        return cursorIndex < 0 || cursorIndex == items.Length - 1
            ? []
            : items[(cursorIndex + 1)..];
    }

    private static bool Matches(InboxItem item, InboxListQuery query) =>
        (query.MessageType is null || item.Type == query.MessageType) &&
        (query.ReadState is null || item.ReadState == query.ReadState) &&
        (query.ResponseState is null || item.ResponseState == query.ResponseState) &&
        (query.Priority is null || item.Priority == query.Priority) &&
        (query.DeadlineFromUtc is null || item.DeadlineAtUtc >= query.DeadlineFromUtc) &&
        (query.DeadlineToUtc is null || item.DeadlineAtUtc <= query.DeadlineToUtc) &&
        (query.ApprovalPending is null ||
            (item.Approval?.State == InboxApprovalState.Pending) == query.ApprovalPending);

    private static InboxItem MapItem(InboxProjectionItem item) =>
        new(
            $"{item.Key.AssignedPositionId.Value}/{item.Key.MessageId}",
            item.Key.MessageId.Value,
            item.Key.AssignedPositionId.Value,
            MapEnum<InboxProjectionMessageType, InboxMessageType>(item.Type),
            MapEndpoint(item.Origin),
            MapEndpoint(item.Destination),
            item.ThreadId.Value,
            MapEnum<Priority, InboxPriority>(item.Priority),
            item.SentAtUtc,
            item.DeadlineAtUtc,
            InboxReadState.Unread,
            MapEnum<InboxProjectionResponseState, InboxResponseState>(item.ResponseState),
            item.Approval is null ? null : MapApproval(item.Approval));

    private static InboxApprovalMetadata MapApproval(InboxProjectionApproval approval)
    {
        var state = MapEnum<InboxProjectionApprovalState, InboxApprovalState>(approval.State);
        return new InboxApprovalMetadata(
            approval.RequestId.Value,
            approval.Action,
            approval.Policy.Value,
            state,
            canDecide: state == InboxApprovalState.Pending,
            approval.DecisionMessageId?.Value,
            approval.DecidedAtUtc);
    }

    private static InboxMessageEndpoint MapEndpoint(EndpointRef endpoint) =>
        endpoint switch
        {
            PositionEndpointRef position => new InboxMessageEndpoint(
                InboxMessageEndpointType.Position,
                position.PositionId.Value),
            OrganizationOwnerEndpointRef => new InboxMessageEndpoint(
                InboxMessageEndpointType.OrganizationOwner),
            _ => throw new InvalidOperationException(
                $"Inbox projection endpoint '{endpoint.GetType().Name}' is not public."),
        };

    private static TTarget MapEnum<TSource, TTarget>(TSource value)
        where TSource : struct, Enum
        where TTarget : struct, Enum =>
        Enum.TryParse<TTarget>(value.ToString(), ignoreCase: false, out var mapped) &&
        Enum.IsDefined(mapped)
            ? mapped
            : throw new InvalidOperationException(
                $"Inbox projection value '{typeof(TSource).Name}.{value}' has no public mapping.");
}
