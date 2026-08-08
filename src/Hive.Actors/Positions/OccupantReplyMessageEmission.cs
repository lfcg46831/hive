using Akka.Actor;
using Akka.Cluster.Sharding;
using Hive.Actors.Sharding;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Actors.Positions;

internal interface IOccupantReplyMessageValidator
{
    ValueTask<ValidationResult> ValidateAsync(
        PositionState state,
        OrgMessage message,
        CancellationToken cancellationToken = default);
}

internal sealed class OccupantReplyMessageValidator : IOccupantReplyMessageValidator
{
    private readonly MessageContractValidator _contractValidator = new();
    private readonly ApprovalRoutingValidator _approvalValidator;
    private readonly RoutingAdmissionValidator _routingValidator;

    public OccupantReplyMessageValidator(IOrganizationRelations relations)
    {
        ArgumentNullException.ThrowIfNull(relations);
        _approvalValidator = new ApprovalRoutingValidator(
            UnsupportedApprovalAuthority.Instance,
            UnsupportedApprovalRequestLog.Instance);
        _routingValidator = new RoutingAdmissionValidator(
            new DirectiveRoutingValidator(relations),
            new ReportRoutingValidator(relations),
            new EscalationRoutingValidator(relations),
            _approvalValidator);
    }

    public async ValueTask<ValidationResult> ValidateAsync(
        PositionState state,
        OrgMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var contract = await _contractValidator.ValidateAsync(
                message,
                new PositionStateMessageValidationContext(state),
                cancellationToken)
            .ConfigureAwait(false);
        if (!contract.IsValid)
        {
            return contract;
        }

        if (message is ApprovalDecision decision)
        {
            var request = state.Inbox
                .Concat(state.MaterializedHistory)
                .OfType<ApprovalRequest>()
                .FirstOrDefault(candidate => candidate.Id == decision.RequestId);
            if (request is null)
            {
                return ValidationResult.Create(
                    [ApprovalValidationCatalog.ApprovalRequestNotFound()]);
            }

            var requestState = state.OccupantReplies.Any(reply =>
                reply.Message is ApprovalDecision existing
                && existing.RequestId == decision.RequestId)
                    ? MessageState.Completed
                    : MessageState.Processing;
            return await _approvalValidator.ValidateAsync(
                    decision,
                    request,
                    requestState,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var admission = await _routingValidator
            .AdmitAsync(message, cancellationToken)
            .ConfigureAwait(false);
        return admission.IsAdmitted
            ? ValidationResult.Valid
            : admission.Rejection!.PublicResult;
    }

    private sealed class PositionStateMessageValidationContext(PositionState state)
        : IMessageValidationContext
    {
        public ValueTask<OrgDirective?> FindDirectiveAsync(
            DirectiveId directiveId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(directiveId);
            cancellationToken.ThrowIfCancellationRequested();
            var directive = state.Inbox
                .Concat(state.MaterializedHistory)
                .OfType<OrgDirective>()
                .FirstOrDefault(candidate => candidate.DirectiveId == directiveId);
            return ValueTask.FromResult(directive);
        }
    }

    private sealed class UnsupportedApprovalAuthority : IApprovalAuthority
    {
        public static UnsupportedApprovalAuthority Instance { get; } = new();

        public ValueTask<ApproverResolution> ResolveApproverAsync(
            ApprovalAuthorityQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ApproverResolution>(
                new InvalidOperationException(
                    "Occupant reply emission does not resolve approval authority."));
    }

    private sealed class UnsupportedApprovalRequestLog : IApprovalRequestLog
    {
        public static UnsupportedApprovalRequestLog Instance { get; } = new();

        public ValueTask<ApprovalRequestRecord?> FindRequestAsync(
            OrganizationId organizationId,
            MessageId requestId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ApprovalRequestRecord?>(
                new InvalidOperationException(
                    "Occupant reply emission does not resolve approval request logs."));
    }
}

internal sealed class UnavailableOccupantReplyMessageValidator : IOccupantReplyMessageValidator
{
    public static UnavailableOccupantReplyMessageValidator Instance { get; } = new();

    public ValueTask<ValidationResult> ValidateAsync(
        PositionState state,
        OrgMessage message,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ValidationResult>(
            new InvalidOperationException(
                "No occupant reply message validator was supplied to the PositionActor."));
}

internal interface IPositionMessageEmitter
{
    void Emit(ActorSystem system, OrgMessage message);
}

internal sealed class ShardedPositionMessageEmitter : IPositionMessageEmitter
{
    public static ShardedPositionMessageEmitter Instance { get; } = new();

    public void Emit(ActorSystem system, OrgMessage message)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(message);
        var destination = message.To as PositionEndpointRef
            ?? throw new InvalidOperationException(
                "An occupant reply destination must be a position.");
        var envelope = PositionEnvelope.For(
            PositionEntityId.From(message.OrganizationId, destination.PositionId),
            new AcceptMessage(message));
        ClusterSharding.Get(system)
            .ShardRegion(PositionEntityId.EntityTypeName)
            .Tell(envelope);
    }
}
