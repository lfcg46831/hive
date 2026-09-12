using Akka.Actor;
using Akka.Pattern;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

internal sealed partial class PositionActor
{
    private readonly PeerRequestRejectionPolicy? _peerRejectionPolicy;
    private readonly HashSet<MessageId> _deliveringPeerRejections = [];

    private async Task RecordPeerRejectionAsync(RecordPeerRequestRejection command)
    {
        var replyTo = Sender;
        var ready = false;
        WhenReady(() => ready = true);
        if (!ready) return;
        try
        {
            if (command.Request.OrganizationId != EntityId.Organization
                || command.Request.From != new PositionEndpointRef(EntityId.Position)
                || _peerRejectionPolicy is null)
                throw new InvalidOperationException("Peer rejection must target its configured requester.");

            if (_state.PeerRejectionEscalations.ContainsKey(command.EscalationId))
            {
                replyTo.Tell(AcceptMessageResult.AlreadyAccepted(command.EscalationId));
                BeginPeerRejectionDelivery(command.EscalationId);
                return;
            }

            var superior = await _peerRejectionPolicy.GetSuperiorAsync(EntityId.Organization, EntityId.Position);
            var now = _clock();
            var escalation = superior is null ? null : new Escalation(command.EscalationId,
                EntityId.Organization, command.Request.From, new PositionEndpointRef(superior),
                command.Request.Thread, command.Request.Priority, 1, now, null,
                "Horizontal peer request rejected.",
                $"Request: {command.Request.Id.Value}; recipient: {((PositionEndpointRef)command.Request.To).PositionId.Value}; reason: {RejectionReasonContract.ToWireValue(command.Reason)}.",
                []);
            PersistEvents([new PeerRejectionEscalationUpdated(command, escalation, superior is null, now)], () =>
            {
                replyTo.Tell(AcceptMessageResult.Accepted(command.EscalationId));
                BeginPeerRejectionDelivery(command.EscalationId);
            });
        }
        catch (Exception failure) { replyTo.Tell(new Status.Failure(failure)); }
    }

    private void BeginPeerRejectionDelivery(MessageId id)
    {
        if (!_state.PeerRejectionEscalations.TryGetValue(id, out var work) || work.Completed
            || !_deliveringPeerRejections.Add(id)) return;
        // Cross-position communication must not suspend this mailbox (reciprocal requests).
        DeliverPeerRejectionAsync(work).PipeTo(Self,
            success: accepted => new PeerRejectionDelivered(id, accepted),
            failure: _ => new PeerRejectionDelivered(id, false));
    }

    private async Task<bool> DeliverPeerRejectionAsync(PeerRejectionEscalationUpdated work)
    {
        var result = work.Escalation is { } escalation
            ? await _messageEmitter.EmitConfirmedAsync(Context.System, escalation)
            : await _peerRequestRegistrar.RecordRejectionAsync(Context.System, work.Rejection);
        return result.MessageId == work.Rejection.EscalationId && result.IsAccepted;
    }

    private void HandlePeerRejectionDelivered(PeerRejectionDelivered delivered)
    {
        _deliveringPeerRejections.Remove(delivered.Id);
        if (!_state.PeerRejectionEscalations.TryGetValue(delivered.Id, out var work) || work.Completed) return;
        if (!delivered.Accepted)
        {
            Timers.StartSingleTimer(new RetryPeerRejection(delivered.Id),
                new RetryPeerRejection(delivered.Id), TimeSpan.FromSeconds(1));
            return;
        }
        PersistEvents([new PeerRejectionEscalationUpdated(work.Rejection, work.Escalation, true, _clock())]);
    }

    private sealed record RetryPeerRejection(MessageId Id);
    private sealed record PeerRejectionDelivered(MessageId Id, bool Accepted);
}
