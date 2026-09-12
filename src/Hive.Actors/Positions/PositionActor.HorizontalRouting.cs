using Akka.Actor;
using Akka.Cluster.Sharding;
using Akka.Pattern;
using Hive.Actors.Sharding;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

internal sealed partial class PositionActor
{
    public ITimerScheduler Timers { get; set; } = null!;
    private readonly RoutingAdmissionValidator? _horizontalAdmission;
    private readonly IPeerRequestRegistrar _peerRequestRegistrar;
    private readonly HashSet<MessageId> _registeringPeerRequests = [];
    private readonly HashSet<MessageId> _registeredPeerRequests = [];

    private async Task RejectHorizontalMessageAsync(OrgMessage message, RoutingRejection rejection, IActorRef replyTo)
    {
        try
        {
            if (_peerRejectionPolicy is not null
                && await _peerRejectionPolicy.ShouldEscalateAsync(message, rejection))
            {
                var notification = new RecordPeerRequestRejection((PeerRequest)message, rejection.PublicResult.Errors[0].Reason);
                if (!_state.PeerRejectionEscalations.ContainsKey(notification.EscalationId))
                {
                    PersistEvents([new PeerRejectionEscalationUpdated(notification, null, false, _clock())], () =>
                    {
                        Reply();
                        BeginPeerRejectionDelivery(notification.EscalationId);
                    });
                    return;
                }
                BeginPeerRejectionDelivery(notification.EscalationId);
            }
            Reply();
        }
        catch (Exception failure) { replyTo.Tell(new Status.Failure(failure)); }

        void Reply()
        {
            PublishProjection(new PositionMessageRoutingRejected(EntityId, rejection, _clock()));
            ReplyToAcceptMessageIfRequested(replyTo,
                AcceptMessageResult.Rejected(message.Id, rejection.PublicResult.Errors[0].Reason));
        }
    }

    private void BeginPeerRegistration(PeerRequest request)
    {
        if (_horizontalAdmission is null || _registeredPeerRequests.Contains(request.Id)
            || !_state.Inbox.Any(message => message.Id == request.Id)
            || !_registeringPeerRequests.Add(request.Id))
            return;

        // Never await another position while suspending this mailbox: reciprocal requests
        // must still be able to record their correlation facts here.
        var registration = request.From == new PositionEndpointRef(EntityId.Position)
            ? Self.Ask<PeerRequestLookupResult>(new RecordPeerRequest(request), TimeSpan.FromSeconds(30))
            : _peerRequestRegistrar.RecordAsync(Context.System, request);
        registration.PipeTo(Self,
            success: result => new PeerRegistrationCompleted(request, result, null),
            failure: failure => new PeerRegistrationCompleted(request, null, failure));
    }

    private void HandlePeerRegistrationCompleted(PeerRegistrationCompleted completed)
    {
        _registeringPeerRequests.Remove(completed.Request.Id);
        if (completed.Failure is not null || completed.Result?.Record?.Request != completed.Request)
        {
            Timers.StartSingleTimer(completed.Request.Id,
                new RetryPeerRegistration(completed.Request), TimeSpan.FromSeconds(1));
            return;
        }

        _registeredPeerRequests.Add(completed.Request.Id);
        if (_state.Inbox.Any(message => message.Id == completed.Request.Id)
            && TryGetCurrentOccupantActivation(out var activation))
        {
            DispatchRegisteredMessage(completed.Request, new MessageDispatched(
                completed.Request.Id, completed.Request.Thread, activation.Occupant,
                activation.OccupantType, _clock()));
        }
    }

    private void DispatchRegisteredMessage(OrgMessage message, MessageDispatched dispatch)
    {
        if (_horizontalAdmission is not null && message is PeerRequest
            && !_registeredPeerRequests.Contains(message.Id))
            return;

        if (dispatch.OccupantType == OccupantType.Human)
            PersistHumanOccupantNotificationRequest(message, dispatch);
        else
            DeliverToOccupant(message, dispatch);
    }

    private sealed record PeerRegistrationCompleted(
        PeerRequest Request, PeerRequestLookupResult? Result, Exception? Failure);
    private sealed record RetryPeerRegistration(PeerRequest Request);

    private sealed class PositionTimeProvider(Func<DateTimeOffset> clock) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => clock();
    }

    private sealed class LocalPeerRequestLog(PositionEntityId entity, Func<PositionState> state) : IPeerRequestLog
    {
        public ValueTask<PeerRequestRecord?> FindRequestAsync(OrganizationId organizationId,
            PositionId requester, MessageId requestId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(organizationId == entity.Organization && requester == entity.Position
                ? state().PeerRequests.GetValueOrDefault(requestId) : null);
        }
    }
}

internal interface IPeerRequestRegistrar
{
    Task<PeerRequestLookupResult> RecordAsync(ActorSystem system, PeerRequest request);

    Task<AcceptMessageResult> RecordRejectionAsync(ActorSystem system, RecordPeerRequestRejection rejection);
}

internal sealed class ShardedPeerRequestRegistrar : IPeerRequestRegistrar
{
    public static ShardedPeerRequestRegistrar Instance { get; } = new();

    public Task<AcceptMessageResult> RecordRejectionAsync(ActorSystem system, RecordPeerRequestRejection rejection) =>
        ClusterSharding.Get(system).ShardRegion(PositionEntityId.EntityTypeName)
            .Ask<AcceptMessageResult>(PositionEnvelope.For(
                PositionEntityId.From(rejection.Request.OrganizationId, ((PositionEndpointRef)rejection.Request.From).PositionId),
                rejection), TimeSpan.FromSeconds(30));

    public Task<PeerRequestLookupResult> RecordAsync(ActorSystem system, PeerRequest request) =>
        ClusterSharding.Get(system).ShardRegion(PositionEntityId.EntityTypeName)
            .Ask<PeerRequestLookupResult>(PositionEnvelope.For(
                PositionEntityId.From(request.OrganizationId, ((PositionEndpointRef)request.From).PositionId),
                new RecordPeerRequest(request)), TimeSpan.FromSeconds(30));
}
