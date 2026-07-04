using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;

namespace Hive.Actors.Positions;

internal interface IAiDirectiveResultMessageGate
{
    ValueTask<AiDirectiveResultMessageGateResult> ValidateAsync(
        AiDirectiveExecutionContext context,
        OrgMessage message,
        CancellationToken cancellationToken = default);
}

internal sealed record AiDirectiveResultMessageGateResult
{
    private AiDirectiveResultMessageGateResult(
        OrgMessage? message,
        AiDirectiveResultMessageFailure? failure)
    {
        Message = message;
        Failure = failure;
    }

    public OrgMessage? Message { get; }

    public AiDirectiveResultMessageFailure? Failure { get; }

    public bool IsAllowed => Failure is null;

    public bool IsRejected => !IsAllowed;

    public static AiDirectiveResultMessageGateResult Allowed(OrgMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new AiDirectiveResultMessageGateResult(message, failure: null);
    }

    public static AiDirectiveResultMessageGateResult Rejected(
        AiDirectiveResultMessageFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new AiDirectiveResultMessageGateResult(message: null, failure);
    }
}

internal sealed class AiDirectiveResultMessageEmissionGate : IAiDirectiveResultMessageGate
{
    public static AiDirectiveResultMessageEmissionGate Instance { get; } = new();

    private AiDirectiveResultMessageEmissionGate()
    {
    }

    public async ValueTask<AiDirectiveResultMessageGateResult> ValidateAsync(
        AiDirectiveExecutionContext context,
        OrgMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        if (message is not (Report or Escalation or Directive))
        {
            return AiDirectiveResultMessageGateResult.Rejected(
                UnsupportedResultMessageFailure(message));
        }

        var admission = await CreateAdmissionValidator(context)
            .AdmitAsync(message, cancellationToken)
            .ConfigureAwait(false);

        if (admission.IsAdmitted)
        {
            return AiDirectiveResultMessageGateResult.Allowed(message);
        }

        var rejection = admission.Rejection
            ?? throw new InvalidOperationException(
                "Rejected routing admission did not carry a routing rejection.");

        return AiDirectiveResultMessageGateResult.Rejected(
            new AiDirectiveResultMessageFailure(
                "routing-rejected",
                RoutingAuditReason(rejection),
                rejection));
    }

    private static RoutingAdmissionValidator CreateAdmissionValidator(
        AiDirectiveExecutionContext context)
    {
        var relations = new MaterializedOrganizationRelations(BuildRelationsSnapshot(context));
        return new RoutingAdmissionValidator(
            new DirectiveRoutingValidator(relations),
            new ReportRoutingValidator(relations),
            new EscalationRoutingValidator(relations),
            new ApprovalRoutingValidator(
                UnsupportedApprovalAuthority.Instance,
                UnsupportedApprovalRequestLog.Instance));
    }

    private static OrganizationRelationsSnapshot BuildRelationsSnapshot(
        AiDirectiveExecutionContext context)
    {
        var builder = OrganizationRelationsSnapshot.CreateBuilder(
            context.OrganizationId,
            new OrganizationOwnerEndpointRef());
        var unit = context.Relation.Unit;

        if (context.Relation.ReportsTo is { } superior)
        {
            builder.AddPosition(superior, unit);
            builder.AddPosition(context.PositionId, unit, superior);
        }
        else
        {
            builder.AddPosition(context.PositionId, unit);
        }

        foreach (var subordinate in context.Relation.DirectSubordinates)
        {
            builder.AddPosition(subordinate, unit, context.PositionId);
        }

        return builder.Build();
    }

    private static AiDirectiveResultMessageFailure UnsupportedResultMessageFailure(
        OrgMessage message)
    {
        var code = message is ApprovalRequest or ApprovalDecision
            ? "implicit-approval-not-authorized"
            : "result-message-type-not-permitted";
        var reason = message is ApprovalRequest or ApprovalDecision
            ? "AI directive result message cannot imply human approval or authorization."
            : $"AI directive result message type '{message.GetType().Name}' is outside the accepted result contract.";

        return new AiDirectiveResultMessageFailure(code, reason);
    }

    private static string RoutingAuditReason(RoutingRejection rejection)
    {
        var errors = string.Join(
            ", ",
            rejection.AuditResult.Errors.Select(
                error => $"{error.Code}@{error.Path}"));

        return $"AI directive result message was rejected by routing validation: {errors}.";
    }

    private sealed class UnsupportedApprovalAuthority : IApprovalAuthority
    {
        public static UnsupportedApprovalAuthority Instance { get; } = new();

        private UnsupportedApprovalAuthority()
        {
        }

        public ValueTask<ApproverResolution> ResolveApproverAsync(
            ApprovalAuthorityQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ApproverResolution>(
                new InvalidOperationException(
                    "AI directive result emission gate does not resolve approval authority."));
    }

    private sealed class UnsupportedApprovalRequestLog : IApprovalRequestLog
    {
        public static UnsupportedApprovalRequestLog Instance { get; } = new();

        private UnsupportedApprovalRequestLog()
        {
        }

        public ValueTask<ApprovalRequestRecord?> FindRequestAsync(
            OrganizationId organizationId,
            MessageId requestId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ApprovalRequestRecord?>(
                new InvalidOperationException(
                    "AI directive result emission gate does not resolve approval request logs."));
    }
}
