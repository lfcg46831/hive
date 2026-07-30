using System.Collections.Immutable;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Application.Directives;

/// <summary>
/// Actor-neutral snapshot handed to the application coordinator after position recovery.
/// </summary>
public sealed record DirectiveExecutionRequest
{
    public DirectiveExecutionRequest(
        PositionEntityId positionEntityId,
        PositionRuntimeConfiguration runtimeConfiguration,
        PositionState recoveredState,
        OccupantId occupant,
        Directive directive)
    {
        PositionEntityId = positionEntityId
            ?? throw new ArgumentNullException(nameof(positionEntityId));
        RuntimeConfiguration = runtimeConfiguration
            ?? throw new ArgumentNullException(nameof(runtimeConfiguration));
        RecoveredState = recoveredState
            ?? throw new ArgumentNullException(nameof(recoveredState));
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));
        Directive = directive ?? throw new ArgumentNullException(nameof(directive));

        if (!runtimeConfiguration.Matches(positionEntityId))
        {
            throw new ArgumentException(
                "Runtime configuration must match the directive execution position.",
                nameof(runtimeConfiguration));
        }

        if (runtimeConfiguration.Occupant.Type != OccupantType.AiAgent)
        {
            throw new ArgumentException(
                "Directive execution coordination requires an AI agent occupant.",
                nameof(runtimeConfiguration));
        }

        if (directive.OrganizationId != positionEntityId.Organization)
        {
            throw new ArgumentException(
                "Directive organization must match the execution position organization.",
                nameof(directive));
        }

        CorrelationId =
            $"directive:{directive.DirectiveId.Value:N}:message:{directive.Id.Value:N}";
    }

    public string CorrelationId { get; }

    public PositionEntityId PositionEntityId { get; }

    public PositionRuntimeConfiguration RuntimeConfiguration { get; }

    public PositionState RecoveredState { get; }

    public OccupantId Occupant { get; }

    public Directive Directive { get; }

    public OrganizationId OrganizationId => PositionEntityId.Organization;

    public PositionId PositionId => PositionEntityId.Position;

    public ThreadId ThreadId => Directive.Thread;

    public MessageId MessageId => Directive.Id;

    public DirectiveId DirectiveId => Directive.DirectiveId;
}

public enum DirectiveExecutionContinuationKind
{
    Inference = 1,
    ConnectorTool = 2,
}

/// <summary>
/// One provider-neutral continuation selected by the coordinator loop.
/// </summary>
public sealed record DirectiveExecutionContinuation
{
    private DirectiveExecutionContinuation(
        DirectiveExecutionContinuationKind kind,
        int iteration,
        AiGatewayRequest? inferenceRequest,
        AiToolCall? toolCall,
        ActingUnderDeclaration actingUnder)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown directive execution continuation kind.");
        }

        if (iteration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iteration),
                iteration,
                "Directive execution continuation iteration must be greater than zero.");
        }

        if ((kind == DirectiveExecutionContinuationKind.Inference) !=
            (inferenceRequest is not null))
        {
            throw new ArgumentException(
                "Only an inference continuation can carry an inference request.",
                nameof(inferenceRequest));
        }

        if ((kind == DirectiveExecutionContinuationKind.ConnectorTool) !=
            (toolCall is not null))
        {
            throw new ArgumentException(
                "Only a connector tool continuation can carry a tool call.",
                nameof(toolCall));
        }

        Kind = kind;
        Iteration = iteration;
        InferenceRequest = inferenceRequest;
        ToolCall = toolCall;
        ActingUnder = actingUnder ?? throw new ArgumentNullException(nameof(actingUnder));
    }

    public DirectiveExecutionContinuationKind Kind { get; }

    public int Iteration { get; }

    public AiGatewayRequest? InferenceRequest { get; }

    public AiToolCall? ToolCall { get; }

    public ActingUnderDeclaration ActingUnder { get; }

    public static DirectiveExecutionContinuation Inference(
        int iteration,
        AiGatewayRequest request) =>
        new(
            DirectiveExecutionContinuationKind.Inference,
            iteration,
            request ?? throw new ArgumentNullException(nameof(request)),
            toolCall: null,
            ActingUnderDeclaration.Missing());

    public static DirectiveExecutionContinuation ConnectorTool(
        int iteration,
        AiToolCall toolCall,
        ActingUnderDeclaration? actingUnder = null) =>
        new(
            DirectiveExecutionContinuationKind.ConnectorTool,
            iteration,
            inferenceRequest: null,
            toolCall ?? throw new ArgumentNullException(nameof(toolCall)),
            actingUnder ?? ActingUnderDeclaration.Missing());
}

/// <summary>
/// Base contract for ordered effects described by the coordinator and dispatched by an adapter.
/// </summary>
public abstract record DirectiveExecutionEffect;

public sealed record DirectiveMessageEffect : DirectiveExecutionEffect
{
    public DirectiveMessageEffect(OrgMessage message)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    public OrgMessage Message { get; }
}

public sealed record DirectivePositionCommandEffect : DirectiveExecutionEffect
{
    public DirectivePositionCommandEffect(PositionCommand command)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
    }

    public PositionCommand Command { get; }
}

public sealed record DirectiveJourneyAuditEffect : DirectiveExecutionEffect
{
    public DirectiveJourneyAuditEffect(JourneyAuditRecord record)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
    }

    public JourneyAuditRecord Record { get; }
}

public sealed record DirectiveAuditExportResultEffect : DirectiveExecutionEffect
{
    public DirectiveAuditExportResultEffect(
        DirectiveId directiveId,
        OrgMessage resultMessage)
    {
        DirectiveId = directiveId ?? throw new ArgumentNullException(nameof(directiveId));
        ResultMessage = resultMessage
            ?? throw new ArgumentNullException(nameof(resultMessage));
    }

    public DirectiveId DirectiveId { get; }

    public OrgMessage ResultMessage { get; }
}

public enum DirectiveExecutionStatus
{
    Completed = 1,
    Failed = 2,
    Escalated = 3,
}

/// <summary>
/// Terminal application result returned to the actor adapter.
/// </summary>
public sealed record DirectiveExecutionResult
{
    private DirectiveExecutionResult(
        DirectiveExecutionRequest request,
        DirectiveExecutionStatus status,
        string? failureCode,
        IEnumerable<DirectiveExecutionEffect>? effects)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown directive execution status.");
        }

        if (status == DirectiveExecutionStatus.Completed && failureCode is not null)
        {
            throw new ArgumentException(
                "A completed directive execution cannot carry a failure code.",
                nameof(failureCode));
        }

        if (status != DirectiveExecutionStatus.Completed && failureCode is null)
        {
            throw new ArgumentException(
                "A non-completed directive execution requires a failure code.",
                nameof(failureCode));
        }

        CorrelationId = request.CorrelationId;
        MessageId = request.MessageId;
        ThreadId = request.ThreadId;
        DirectiveId = request.DirectiveId;
        Status = status;
        FailureCode = failureCode is null
            ? null
            : RequireText(failureCode, nameof(failureCode));
        Effects = SnapshotEffects(effects);
    }

    public string CorrelationId { get; }

    public MessageId MessageId { get; }

    public ThreadId ThreadId { get; }

    public DirectiveId DirectiveId { get; }

    public DirectiveExecutionStatus Status { get; }

    public string? FailureCode { get; }

    public ImmutableArray<DirectiveExecutionEffect> Effects { get; }

    public static DirectiveExecutionResult Completed(
        DirectiveExecutionRequest request,
        IEnumerable<DirectiveExecutionEffect>? effects = null) =>
        new(request, DirectiveExecutionStatus.Completed, failureCode: null, effects);

    public static DirectiveExecutionResult Failed(
        DirectiveExecutionRequest request,
        string failureCode,
        IEnumerable<DirectiveExecutionEffect>? effects = null) =>
        new(request, DirectiveExecutionStatus.Failed, failureCode, effects);

    public static DirectiveExecutionResult Escalated(
        DirectiveExecutionRequest request,
        string failureCode,
        IEnumerable<DirectiveExecutionEffect>? effects = null) =>
        new(request, DirectiveExecutionStatus.Escalated, failureCode, effects);

    private static ImmutableArray<DirectiveExecutionEffect> SnapshotEffects(
        IEnumerable<DirectiveExecutionEffect>? effects)
    {
        if (effects is null)
        {
            return [];
        }

        var snapshot = effects.ToImmutableArray();
        if (snapshot.Any(effect => effect is null))
        {
            throw new ArgumentException(
                "Directive execution effects cannot contain null entries.",
                nameof(effects));
        }

        return snapshot;
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Value cannot contain leading or trailing whitespace.",
                parameterName);
        }

        return value;
    }
}
