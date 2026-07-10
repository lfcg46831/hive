using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Actors.Positions;

internal sealed record AiDirectiveResultMessageFailure
{
    public AiDirectiveResultMessageFailure(
        string code,
        string auditReason,
        RoutingRejection? routingRejection = null)
    {
        Code = AiAgentGatewayText.Require(code, nameof(code));
        AuditReason = AiAgentGatewayText.Require(auditReason, nameof(auditReason));
        RoutingRejection = routingRejection;
    }

    public string Code { get; }

    public string AuditReason { get; }

    public RoutingRejection? RoutingRejection { get; }
}

internal sealed record AiDirectiveResultMessage
{
    private AiDirectiveResultMessage(
        string correlationId,
        OrgMessage? message,
        AiDirectiveResultMessageFailure? failure,
        ActingUnderDeclaration actingUnder)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Message = message;
        Failure = failure;
        ActingUnder = actingUnder ?? throw new ArgumentNullException(nameof(actingUnder));
    }

    public string CorrelationId { get; }

    public OrgMessage? Message { get; }

    public AiDirectiveResultMessageFailure? Failure { get; }

    public ActingUnderDeclaration ActingUnder { get; }

    public bool IsSuccess => Message is not null;

    public bool IsFailure => !IsSuccess;

    public static AiDirectiveResultMessage Success(
        string correlationId,
        OrgMessage message,
        ActingUnderDeclaration? actingUnder = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new AiDirectiveResultMessage(
            correlationId,
            message,
            failure: null,
            actingUnder ?? ActingUnderDeclaration.Missing());
    }

    public static AiDirectiveResultMessage Rejected(
        string correlationId,
        AiDirectiveResultMessageFailure failure,
        ActingUnderDeclaration? actingUnder = null)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new AiDirectiveResultMessage(
            correlationId,
            message: null,
            failure,
            actingUnder ?? ActingUnderDeclaration.Missing());
    }
}

internal static class AiDirectiveResultMessageFactory
{
    private const int ResultMessageSchemaVersion = 1;

    public static AiDirectiveResultMessage Create(
        AiDirectiveExecutionContext context,
        AiDirectiveDecision decision,
        Func<MessageId>? newMessageId = null,
        Func<DirectiveId>? newDirectiveId = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(decision);

        var messageIdFactory = newMessageId ?? (() => DeterministicResultMessageId(context, decision));
        var directiveIdFactory = newDirectiveId ?? (() => DeterministicChildDirectiveId(context, decision));
        var now = clock ?? (() => DateTimeOffset.UtcNow);

        return decision switch
        {
            AiDirectiveReportDecision report => CreateReport(
                context,
                report,
                messageIdFactory,
                now),
            AiDirectiveEscalationDecision escalation => CreateEscalation(
                context,
                escalation,
                messageIdFactory,
                now),
            AiDirectiveChildDirectiveDecision directive => CreateChildDirective(
                context,
                directive,
                messageIdFactory,
                directiveIdFactory,
                now),
            _ => throw new InvalidOperationException("Unknown AI directive decision type."),
        };
    }

    private static AiDirectiveResultMessage CreateReport(
        AiDirectiveExecutionContext context,
        AiDirectiveReportDecision decision,
        Func<MessageId> newMessageId,
        Func<DateTimeOffset> clock)
    {
        if (context.Relation.ReportsTo is not { } superior)
        {
            return Rejected(
                context,
                decision.ActingUnder,
                "direct-superior-missing",
                "AI directive report could not be materialized because the current position has no direct superior.");
        }

        return AiDirectiveResultMessage.Success(
            context.CorrelationId,
            new Report(
                newMessageId(),
                context.OrganizationId,
                FromCurrentPosition(context),
                new PositionEndpointRef(superior),
                context.Directive.ThreadId,
                context.Directive.Priority,
                ResultMessageSchemaVersion,
                clock(),
                context.Directive.Deadline,
                context.Directive.DirectiveId,
                decision.Kind,
                decision.Body),
            decision.ActingUnder);
    }

    private static AiDirectiveResultMessage CreateEscalation(
        AiDirectiveExecutionContext context,
        AiDirectiveEscalationDecision decision,
        Func<MessageId> newMessageId,
        Func<DateTimeOffset> clock)
    {
        EndpointRef destination = context.Relation.ReportsTo is { } superior
            ? new PositionEndpointRef(superior)
            : new OrganizationOwnerEndpointRef();

        return AiDirectiveResultMessage.Success(
            context.CorrelationId,
            new Escalation(
                newMessageId(),
                context.OrganizationId,
                FromCurrentPosition(context),
                destination,
                context.Directive.ThreadId,
                context.Directive.Priority,
                ResultMessageSchemaVersion,
                clock(),
                context.Directive.Deadline,
                decision.Issue,
                decision.Context,
                decision.OptionsConsidered),
            decision.ActingUnder);
    }

    private static AiDirectiveResultMessage CreateChildDirective(
        AiDirectiveExecutionContext context,
        AiDirectiveChildDirectiveDecision decision,
        Func<MessageId> newMessageId,
        Func<DirectiveId> newDirectiveId,
        Func<DateTimeOffset> clock)
    {
        if (!context.Relation.DirectSubordinates.Contains(decision.TargetPositionId))
        {
            return Rejected(
                context,
                decision.ActingUnder,
                "child-directive-target-not-permitted",
                $"AI directive child directive target '{decision.TargetPositionId.Value}' is not a permitted direct subordinate of '{context.PositionId.Value}'.");
        }

        return AiDirectiveResultMessage.Success(
            context.CorrelationId,
            new Directive(
                newMessageId(),
                context.OrganizationId,
                FromCurrentPosition(context),
                new PositionEndpointRef(decision.TargetPositionId),
                context.Directive.ThreadId,
                context.Directive.Priority,
                ResultMessageSchemaVersion,
                clock(),
                context.Directive.Deadline,
                newDirectiveId(),
                context.Directive.DirectiveId,
                decision.Objective,
                decision.Context),
            decision.ActingUnder);
    }

    private static PositionEndpointRef FromCurrentPosition(AiDirectiveExecutionContext context) =>
        new(context.PositionId);

    private static MessageId DeterministicResultMessageId(
        AiDirectiveExecutionContext context,
        AiDirectiveDecision decision) =>
        MessageId.From(DeterministicGuid.FromName(
            $"{ResultKeyPrefix(context, decision)}|identity=message"));

    private static DirectiveId DeterministicChildDirectiveId(
        AiDirectiveExecutionContext context,
        AiDirectiveDecision decision)
    {
        if (decision is not AiDirectiveChildDirectiveDecision)
        {
            throw new InvalidOperationException(
                "Only child directive decisions can request a deterministic child directive id.");
        }

        return DirectiveId.From(DeterministicGuid.FromName(
            $"{ResultKeyPrefix(context, decision)}|identity=directive"));
    }

    private static string ResultKeyPrefix(
        AiDirectiveExecutionContext context,
        AiDirectiveDecision decision) =>
        string.Join(
            "|",
            "ai-directive-result:v1",
            $"organization={context.OrganizationId.Value}",
            $"position={context.PositionId.Value}",
            $"thread={context.Directive.ThreadId.Value:N}",
            $"directive={context.Directive.DirectiveId.Value:N}",
            $"source-message={context.Directive.MessageId.Value:N}",
            $"result={ResultSlot(decision)}");

    private static string ResultSlot(AiDirectiveDecision decision) =>
        decision switch
        {
            AiDirectiveReportDecision => "Report",
            AiDirectiveEscalationDecision => "Escalation",
            AiDirectiveChildDirectiveDecision child => $"Directive:{child.TargetPositionId.Value}",
            _ => throw new InvalidOperationException("Unknown AI directive decision type."),
        };

    private static AiDirectiveResultMessage Rejected(
        AiDirectiveExecutionContext context,
        ActingUnderDeclaration actingUnder,
        string code,
        string auditReason) =>
        AiDirectiveResultMessage.Rejected(
            context.CorrelationId,
            new AiDirectiveResultMessageFailure(code, auditReason),
            actingUnder);
}

internal sealed record GetAiDirectiveResultMessage
{
    public GetAiDirectiveResultMessage(string correlationId)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
    }

    public string CorrelationId { get; }
}

internal sealed record AiDirectiveResultMessageQueryResult
{
    private AiDirectiveResultMessageQueryResult(
        string correlationId,
        AiDirectiveResultMessage? result)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Result = result;
    }

    public string CorrelationId { get; }

    public AiDirectiveResultMessage? Result { get; }

    public bool Found => Result is not null;

    public static AiDirectiveResultMessageQueryResult FoundResult(
        AiDirectiveResultMessage result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new AiDirectiveResultMessageQueryResult(
            result.CorrelationId,
            result);
    }

    public static AiDirectiveResultMessageQueryResult Missing(string correlationId) =>
        new(correlationId, result: null);
}
