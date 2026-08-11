using Hive.Api.Authorization;
using Hive.Contracts.Inbox;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Inbox.ReadModels;

namespace Hive.Api.Inbox;

internal sealed class ProjectionInboxReadModel(
    IInboxProjectionSnapshotReader snapshotReader,
    IInboxInteractionReader interactionReader,
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
        if (!snapshotReader.IsAvailable || !interactionReader.IsAvailable)
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
        var scopedItems = snapshot.Items
            .Where(item => authorizedPositions.Contains(item.Key.AssignedPositionId))
            .ToArray();
        var interactions = await interactionReader.ReadAsync(
                scope.OrganizationId,
                scope.PersonId,
                scopedItems.Select(static item => item.Key).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        var filtered = scopedItems
            .Select(item => MapItem(
                item,
                interactions.GetValueOrDefault(item.Key)))
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
        if (!snapshotReader.IsAvailable || !interactionReader.IsAvailable)
        {
            return InboxReadResult<InboxItemResponse>.Unavailable;
        }

        var snapshot = await snapshotReader.ReadAsync(
                scope.OrganizationId,
                scope.PositionIds,
                cancellationToken)
            .ConfigureAwait(false);
        var authorizedPositions = scope.PositionIds.ToHashSet();
        var scopedItems = snapshot.Items
            .Where(candidate => authorizedPositions.Contains(
                candidate.Key.AssignedPositionId))
            .ToArray();
        var interactions = await interactionReader.ReadAsync(
                scope.OrganizationId,
                scope.PersonId,
                scopedItems.Select(static item => item.Key).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        var projectedItem = scopedItems.SingleOrDefault(candidate =>
            string.Equals(PublicItemId(candidate), itemId, StringComparison.Ordinal));
        var item = projectedItem is null
            ? null
            : MapItem(projectedItem, interactions.GetValueOrDefault(projectedItem.Key));
        return InboxReadResult<InboxItemResponse>.Available(
            item is null
                ? null
                : new InboxItemResponse(
                    timeProvider.GetUtcNow(),
                    snapshot.LastEventAppliedAtUtc,
                    item,
                    interactions
                        .GetValueOrDefault(projectedItem!.Key)
                        ?.DraftText,
                    MapContent(projectedItem!.Content)));
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

    private static InboxItem MapItem(
        InboxProjectionItem item,
        InboxInteractionState? interaction) =>
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
            interaction is null
                ? InboxReadState.Unread
                : MapEnum<InboxInteractionReadState, InboxReadState>(interaction.ReadState),
            MapResponseState(item.ResponseState, interaction),
            item.Approval is null ? null : MapApproval(item.Approval),
            item.IsExpired,
            item.LastReminderAtUtc is null
                ? InboxReminderState.None
                : InboxReminderState.Sent,
            item.LastReminderAtUtc,
            item.IsDelegated);

    private static string PublicItemId(InboxProjectionItem item) =>
        $"{item.Key.AssignedPositionId.Value}/{item.Key.MessageId}";

    private static InboxMessageContent MapContent(InboxProjectionMessageContent content) =>
        content switch
        {
            InboxProjectionDirectiveContent directive => new InboxDirectiveMessageContent(
                directive.Objective,
                directive.Context),
            InboxProjectionReportContent report => new InboxReportMessageContent(
                report.Body,
                MapEnum<ReportKind, InboxReportKind>(report.Kind)),
            InboxProjectionEscalationContent escalation => new InboxEscalationMessageContent(
                escalation.Issue,
                escalation.Context),
            InboxProjectionMemoContent memo => new InboxMemoMessageContent(memo.Body),
            InboxProjectionPeerRequestContent request => new InboxPeerRequestMessageContent(
                request.Ask),
            InboxProjectionPeerResponseContent response => new InboxPeerResponseMessageContent(
                response.Body),
            InboxProjectionApprovalRequestContent request =>
                new InboxApprovalRequestMessageContent(
                    request.Action,
                    request.Justification),
            InboxProjectionApprovalDecisionContent decision =>
                new InboxApprovalDecisionMessageContent(decision.Reason),
            _ => throw new InvalidOperationException(
                $"Inbox projection content '{content.GetType().Name}' has no public mapping."),
        };

    private static InboxResponseState MapResponseState(
        InboxProjectionResponseState derivedState,
        InboxInteractionState? interaction) =>
        derivedState == InboxProjectionResponseState.AwaitingResponse &&
        interaction?.ReplyState == InboxInteractionReplyState.InProgress
            ? InboxResponseState.InProgress
            : MapEnum<InboxProjectionResponseState, InboxResponseState>(derivedState);

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
