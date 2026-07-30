using System.Collections.Immutable;
using Hive.Domain.Ai;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Outcomes;

namespace Hive.Application.Directives;

public sealed record DirectiveInferenceRequest
{
    public DirectiveInferenceRequest(
        string correlationId,
        ExecutionBudgetOperation operation,
        AiGatewayRequest request)
    {
        if (operation is not (
            ExecutionBudgetOperation.PrimaryInference or
            ExecutionBudgetOperation.ContinuationInference))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Directive inference requires a primary or continuation inference operation.");
        }

        CorrelationId = RequireText(correlationId, nameof(correlationId));
        Operation = operation;
        Request = request ?? throw new ArgumentNullException(nameof(request));
    }

    public string CorrelationId { get; }

    public ExecutionBudgetOperation Operation { get; }

    public AiGatewayRequest Request { get; }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Value must be non-empty and contain no surrounding whitespace.",
                parameterName);
        }

        return value;
    }
}

public interface IDirectiveInferencePort
{
    ValueTask<AiGatewayResponse> InferAsync(
        DirectiveInferenceRequest request,
        ExecutionBudget budget,
        CancellationToken cancellationToken = default);
}

public sealed record DirectiveExecutionFailure
{
    public DirectiveExecutionFailure(string code, string auditReason)
    {
        Code = RequireText(code, nameof(code));
        AuditReason = RequireText(auditReason, nameof(auditReason));
    }

    public string Code { get; }

    public string AuditReason { get; }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Value must be non-empty and contain no surrounding whitespace.",
                parameterName);
        }

        return value;
    }
}

public sealed record DirectiveToolExecutionResult
{
    private DirectiveToolExecutionResult(
        IReadOnlyDictionary<string, object?>? output,
        DirectiveExecutionFailure? failure)
    {
        Output = SnapshotData(output);
        Failure = failure;
    }

    public ImmutableDictionary<string, object?> Output { get; }

    public DirectiveExecutionFailure? Failure { get; }

    public bool IsSuccess => Failure is null;

    public static DirectiveToolExecutionResult Succeeded(
        IReadOnlyDictionary<string, object?>? output = null) =>
        new(output, failure: null);

    public static DirectiveToolExecutionResult Failed(
        DirectiveExecutionFailure failure) =>
        new(
            output: null,
            failure ?? throw new ArgumentNullException(nameof(failure)));

    private static ImmutableDictionary<string, object?> SnapshotData(
        IReadOnlyDictionary<string, object?>? source)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(
            StringComparer.Ordinal);
        if (source is null)
        {
            return builder.ToImmutable();
        }

        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key) ||
                !string.Equals(key, key.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Tool output keys must be non-empty and contain no surrounding whitespace.",
                    nameof(source));
            }

            builder.Add(key, value);
        }

        return builder.ToImmutable();
    }
}

public interface IDirectiveToolPort
{
    ValueTask<DirectiveToolExecutionResult> ExecuteAsync(
        DirectiveExecutionRequest execution,
        DirectiveExecutionContinuation continuation,
        ExecutionBudget budget,
        CancellationToken cancellationToken = default);
}

public enum DirectiveActionGateOutcome
{
    Allowed = 1,
    RetainedForEscalation = 2,
    RetainedForHumanApproval = 3,
}

public sealed record DirectiveActionCandidate
{
    private DirectiveActionCandidate(
        ActionDomainActionKind kind,
        string selector,
        ActingUnderDeclaration actingUnder,
        AiToolCall? toolCall,
        OrgMessage? message)
    {
        Kind = kind;
        Selector = selector;
        ActingUnder = actingUnder;
        ToolCall = toolCall;
        Message = message;
    }

    public ActionDomainActionKind Kind { get; }

    public string Selector { get; }

    public ActingUnderDeclaration ActingUnder { get; }

    public AiToolCall? ToolCall { get; }

    public OrgMessage? Message { get; }

    public static DirectiveActionCandidate ForTool(
        AiToolCall toolCall,
        ActingUnderDeclaration? actingUnder = null)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        return new DirectiveActionCandidate(
            ActionDomainActionKind.Tool,
            toolCall.Name,
            actingUnder ?? ActingUnderDeclaration.Missing(),
            toolCall,
            message: null);
    }

    public static DirectiveActionCandidate ForMessage(
        OrgMessage message,
        ActingUnderDeclaration? actingUnder = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new DirectiveActionCandidate(
            ActionDomainActionKind.OrganizationalMessage,
            MessageSelector(message),
            actingUnder ?? ActingUnderDeclaration.Missing(),
            toolCall: null,
            message);
    }

    private static string MessageSelector(OrgMessage message) =>
        message switch
        {
            Report => nameof(Report),
            Escalation => nameof(Escalation),
            Directive => nameof(Directive),
            ApprovalRequest => nameof(ApprovalRequest),
            ApprovalDecision => nameof(ApprovalDecision),
            AuthorizationGrant => nameof(AuthorizationGrant),
            _ => throw new ArgumentException(
                $"Organizational message type '{message.GetType().Name}' has no action selector.",
                nameof(message)),
        };
}

public sealed record DirectiveActionGateDecision
{
    private DirectiveActionGateDecision(
        DirectiveActionGateOutcome outcome,
        string code,
        ActionFacts? facts,
        ActionGateResolution? resolution,
        IEnumerable<OrgMessage>? governanceMessages)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Unknown directive action gate outcome.");
        }

        Outcome = outcome;
        Code = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException(
                "Directive action gate code is required.",
                nameof(code))
            : code;
        Facts = facts;
        Resolution = resolution;
        GovernanceMessages = governanceMessages is null
            ? []
            : governanceMessages.ToImmutableArray();
        if (GovernanceMessages.Any(message => message is null))
        {
            throw new ArgumentException(
                "Governance messages cannot contain null entries.",
                nameof(governanceMessages));
        }

        if (outcome == DirectiveActionGateOutcome.Allowed &&
            !GovernanceMessages.IsEmpty)
        {
            throw new ArgumentException(
                "An allowed action cannot carry governance messages.",
                nameof(governanceMessages));
        }

        if (outcome != DirectiveActionGateOutcome.Allowed &&
            GovernanceMessages.IsEmpty)
        {
            throw new ArgumentException(
                "A retained action must carry at least one governance message.",
                nameof(governanceMessages));
        }
    }

    public DirectiveActionGateOutcome Outcome { get; }

    public string Code { get; }

    public ActionFacts? Facts { get; }

    public ActionGateResolution? Resolution { get; }

    public ImmutableArray<OrgMessage> GovernanceMessages { get; }

    public bool IsAllowed => Outcome == DirectiveActionGateOutcome.Allowed;

    public static DirectiveActionGateDecision Allowed(
        string code,
        ActionFacts? facts = null,
        ActionGateResolution? resolution = null) =>
        new(
            DirectiveActionGateOutcome.Allowed,
            code,
            facts,
            resolution,
            governanceMessages: null);

    public static DirectiveActionGateDecision Retained(
        DirectiveActionGateOutcome outcome,
        string code,
        IEnumerable<OrgMessage> governanceMessages,
        ActionFacts? facts = null,
        ActionGateResolution? resolution = null)
    {
        if (outcome == DirectiveActionGateOutcome.Allowed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Retained action outcome cannot be Allowed.");
        }

        return new DirectiveActionGateDecision(
            outcome,
            code,
            facts,
            resolution,
            governanceMessages);
    }
}

public interface IDirectiveActionGatePort
{
    ValueTask<DirectiveActionGateDecision> EvaluateAsync(
        DirectiveExecutionRequest execution,
        DirectiveActionCandidate candidate,
        CancellationToken cancellationToken = default);
}

public sealed record DirectiveResultMessageGateDecision
{
    private DirectiveResultMessageGateDecision(
        OrgMessage? message,
        DirectiveExecutionFailure? failure,
        RoutingRejection? rejection)
    {
        if ((message is null) == (failure is null))
        {
            throw new ArgumentException(
                "Result message gate decision must contain either a message or a failure.");
        }

        Message = message;
        Failure = failure;
        Rejection = rejection;
    }

    public OrgMessage? Message { get; }

    public DirectiveExecutionFailure? Failure { get; }

    public RoutingRejection? Rejection { get; }

    public bool IsAllowed => Failure is null;

    public static DirectiveResultMessageGateDecision Allowed(OrgMessage message) =>
        new(
            message ?? throw new ArgumentNullException(nameof(message)),
            failure: null,
            rejection: null);

    public static DirectiveResultMessageGateDecision Rejected(
        DirectiveExecutionFailure failure,
        RoutingRejection? rejection = null) =>
        new(
            message: null,
            failure ?? throw new ArgumentNullException(nameof(failure)),
            rejection);
}

public interface IDirectiveResultMessageGatePort
{
    ValueTask<DirectiveResultMessageGateDecision> ValidateAsync(
        DirectiveExecutionRequest execution,
        OrgMessage message,
        CancellationToken cancellationToken = default);
}

public interface IDirectiveOutcomeVerifierPort
{
    ValueTask<OutcomeVerifierResult> VerifyAsync(
        OutcomeVerificationRequest request,
        ExecutionBudget budget,
        CancellationToken cancellationToken = default);
}
