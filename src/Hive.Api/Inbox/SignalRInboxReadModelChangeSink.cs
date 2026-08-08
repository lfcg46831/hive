using System.Collections.Concurrent;
using Hive.Api.Organization;
using Hive.Contracts.Inbox;
using Hive.Infrastructure.Inbox.ReadModels;
using Microsoft.AspNetCore.SignalR;

namespace Hive.Api.Inbox;

internal sealed class SignalRInboxReadModelChangeSink(
    IHubContext<OrganizationUpdatesHub, IOrganizationUpdatesClient> hubContext,
    InboxRealtimeSubscriptionRegistry subscriptions,
    ILogger<SignalRInboxReadModelChangeSink> logger) : IInboxReadModelChangeSink
{
    private readonly ConcurrentDictionary<InboxPrincipalGroup, SequenceCounter> _sequences = [];

    public async ValueTask ProjectionChangedAsync(
        InboxProjectionChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var changeType = ProjectionChangeType(change);
        if (changeType is null)
        {
            return;
        }

        var key = change.Item.Key;
        foreach (var principal in subscriptions.PrincipalsFor(
                     key.OrganizationId,
                     key.AssignedPositionId))
        {
            await PublishAsync(
                principal,
                key.AssignedPositionId.Value,
                key.MessageId.Value,
                changeType.Value,
                change.OccurredAtUtc,
                cancellationToken);
        }
    }

    public ValueTask InteractionChangedAsync(
        InboxInteractionMutation mutation,
        InboxInteractionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(state);
        if (mutation.ItemKey != state.ItemKey ||
            !string.Equals(mutation.PersonId, state.PersonId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The committed interaction state must match its mutation.",
                nameof(state));
        }

        var changeType = mutation.Action is
            InboxInteractionAction.MarkRead or InboxInteractionAction.MarkUnread
                ? InboxChangeType.ReadStateChanged
                : InboxChangeType.ResponseStateChanged;
        var principal = new InboxPrincipalGroup(
            mutation.ItemKey.OrganizationId,
            mutation.PersonId);
        if (!subscriptions.IsSubscribed(principal))
        {
            return ValueTask.CompletedTask;
        }

        return PublishAsync(
            principal,
            mutation.ItemKey.AssignedPositionId.Value,
            mutation.ItemKey.MessageId.Value,
            changeType,
            state.UpdatedAtUtc,
            cancellationToken);
    }

    private async ValueTask PublishAsync(
        InboxPrincipalGroup principal,
        string assignedPositionId,
        Guid messageId,
        InboxChangeType changeType,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var sequence = Interlocked.Increment(
                ref _sequences.GetOrAdd(principal, static _ => new SequenceCounter()).Value);
            if (sequence < 1)
            {
                throw new InvalidOperationException(
                    $"Inbox realtime sequence overflowed for principal '{principal.PersonId}'.");
            }

            var notification = new InboxChangedNotification(
                sequence,
                principal.OrganizationId.Value,
                $"{assignedPositionId}/{messageId}",
                assignedPositionId,
                changeType,
                changedAtUtc.ToUniversalTime());
            await hubContext.Clients
                .Group(principal.SignalRGroupName)
                .InboxChanged(notification)
                .WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Realtime delivery is best effort; the REST snapshot remains authoritative.
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not publish inbox change {ChangeType} for person {PersonId} in organization {OrganizationId}; clients will reconcile through REST polling.",
                changeType,
                principal.PersonId,
                principal.OrganizationId.Value);
        }
    }

    private static InboxChangeType? ProjectionChangeType(InboxProjectionChange change)
    {
        if (string.Equals(
                change.FactType,
                "directive-deadline-approaching",
                StringComparison.Ordinal))
        {
            return InboxChangeType.DeadlineApproaching;
        }

        if (change.Item.Type == InboxProjectionMessageType.ApprovalRequest &&
            string.Equals(change.FactType, "ApprovalDecision", StringComparison.Ordinal))
        {
            return InboxChangeType.DecisionIssued;
        }

        if (change.Item.ResponseState == InboxProjectionResponseState.Responded &&
            IsResponseFact(change.FactType))
        {
            return InboxChangeType.ResponseStateChanged;
        }

        if (change.Item.Type == InboxProjectionMessageType.ApprovalRequest &&
            change.Item.Approval?.State == InboxProjectionApprovalState.Pending &&
            string.Equals(change.FactType, "approval-request", StringComparison.Ordinal))
        {
            return InboxChangeType.ApprovalPending;
        }

        if (change.Item.Type == InboxProjectionMessageType.ApprovalDecision &&
            string.Equals(change.FactType, "approval-decision", StringComparison.Ordinal))
        {
            return InboxChangeType.DecisionIssued;
        }

        return IsNewItemManifest(change.FactType)
            ? InboxChangeType.NewItem
            : null;
    }

    private static bool IsNewItemManifest(string factType) =>
        factType is "directive" or "report" or "escalation" or "memo" or "peer-request"
            or "peer-response" or "approval-request" or "approval-decision";

    private static bool IsResponseFact(string factType) =>
        factType is "Report" or "PeerResponse" or "Directive" or "ResultMessageCreated";

    private sealed class SequenceCounter
    {
        public long Value;
    }
}
