using System.Collections.Immutable;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Actors.Positions;

internal sealed record AiDirectiveAuditSnapshot
{
    public AiDirectiveAuditSnapshot(
        string correlationId,
        DateTimeOffset recordedAt,
        AiDirectiveProcessingStatus status,
        string terminalCode,
        AiDirectiveAuditContextSnapshot context,
        AiDirectiveAuditGatewaySnapshot gateway,
        string? terminalReason = null,
        AiDirectiveAuditDecisionSnapshot? decision = null,
        AiDirectiveAuditResultMessageSnapshot? resultMessage = null,
        AiDirectiveIterationAuditTrail? iterationAudit = null,
        AiDirectiveAuditPositionEffectsSnapshot? positionEffects = null,
        IEnumerable<AiGatewayAuditRedaction>? redactions = null)
    {
        if (recordedAt == default)
        {
            throw new ArgumentException(
                "AI directive audit snapshot timestamp must be specified.",
                nameof(recordedAt));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown AI directive processing status.");
        }

        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        RecordedAt = recordedAt;
        Status = status;
        TerminalCode = AiAgentGatewayText.Require(terminalCode, nameof(terminalCode));
        TerminalReason = terminalReason is null
            ? null
            : AiAgentGatewayText.Require(terminalReason, nameof(terminalReason));
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        Decision = decision;
        ResultMessage = resultMessage;
        IterationAudit = iterationAudit;
        PositionEffects = positionEffects;
        Redactions = SnapshotRedactions(redactions);
    }

    public string CorrelationId { get; }

    public DateTimeOffset RecordedAt { get; }

    public AiDirectiveProcessingStatus Status { get; }

    public string TerminalCode { get; }

    public string? TerminalReason { get; }

    public AiDirectiveAuditContextSnapshot Context { get; }

    public AiDirectiveAuditGatewaySnapshot Gateway { get; }

    public AiDirectiveAuditDecisionSnapshot? Decision { get; }

    public AiDirectiveAuditResultMessageSnapshot? ResultMessage { get; }

    public AiDirectiveIterationAuditTrail? IterationAudit { get; }

    public AiDirectiveAuditPositionEffectsSnapshot? PositionEffects { get; }

    public ImmutableArray<AiGatewayAuditRedaction> Redactions { get; }

    private static ImmutableArray<AiGatewayAuditRedaction> SnapshotRedactions(
        IEnumerable<AiGatewayAuditRedaction>? source)
    {
        if (source is null)
        {
            return ImmutableArray<AiGatewayAuditRedaction>.Empty;
        }

        var snapshot = source.ToImmutableArray();
        if (snapshot.Any(redaction => redaction is null))
        {
            throw new ArgumentException(
                "AI directive audit redactions cannot contain null entries.",
                nameof(source));
        }

        return snapshot;
    }
}

internal sealed record AiDirectiveAuditContextSnapshot
{
    public AiDirectiveAuditContextSnapshot(
        OrganizationId organizationId,
        PositionId positionId,
        OccupantId occupant,
        ThreadId threadId,
        DirectiveId directiveId,
        MessageId messageId,
        string? identityPromptRef,
        string? identityPromptPath,
        int shortMemoryCount,
        int openTaskCount,
        int recentHistoryCount,
        IEnumerable<AiDirectiveAuditToolSnapshot>? authorizedTools = null,
        int canDecideCount = 0,
        int authorityOverrideCount = 0)
    {
        if (shortMemoryCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(shortMemoryCount));
        }

        if (openTaskCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(openTaskCount));
        }

        if (recentHistoryCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recentHistoryCount));
        }

        if (canDecideCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canDecideCount));
        }

        if (authorityOverrideCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(authorityOverrideCount));
        }

        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        PositionId = positionId ?? throw new ArgumentNullException(nameof(positionId));
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        DirectiveId = directiveId ?? throw new ArgumentNullException(nameof(directiveId));
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        IdentityPromptRef = identityPromptRef is null
            ? null
            : AiAgentGatewayText.Require(identityPromptRef, nameof(identityPromptRef));
        IdentityPromptPath = identityPromptPath is null
            ? null
            : AiAgentGatewayText.Require(identityPromptPath, nameof(identityPromptPath));
        ShortMemoryCount = shortMemoryCount;
        OpenTaskCount = openTaskCount;
        RecentHistoryCount = recentHistoryCount;
        AuthorizedTools = SnapshotTools(authorizedTools);
        CanDecideCount = canDecideCount;
        AuthorityOverrideCount = authorityOverrideCount;
    }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public OccupantId Occupant { get; }

    public ThreadId ThreadId { get; }

    public DirectiveId DirectiveId { get; }

    public MessageId MessageId { get; }

    public string? IdentityPromptRef { get; }

    public string? IdentityPromptPath { get; }

    public int ShortMemoryCount { get; }

    public int OpenTaskCount { get; }

    public int RecentHistoryCount { get; }

    public ImmutableArray<AiDirectiveAuditToolSnapshot> AuthorizedTools { get; }

    public int CanDecideCount { get; }

    public int AuthorityOverrideCount { get; }

    private static ImmutableArray<AiDirectiveAuditToolSnapshot> SnapshotTools(
        IEnumerable<AiDirectiveAuditToolSnapshot>? source)
    {
        if (source is null)
        {
            return ImmutableArray<AiDirectiveAuditToolSnapshot>.Empty;
        }

        var snapshot = source.ToImmutableArray();
        if (snapshot.Any(tool => tool is null))
        {
            throw new ArgumentException(
                "AI directive audit tools cannot contain null entries.",
                nameof(source));
        }

        return snapshot;
    }
}

internal sealed record AiDirectiveAuditToolSnapshot
{
    public AiDirectiveAuditToolSnapshot(string connector, int scopeCount)
    {
        if (scopeCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scopeCount));
        }

        Connector = AiAgentGatewayText.Require(connector, nameof(connector));
        ScopeCount = scopeCount;
    }

    public string Connector { get; }

    public int ScopeCount { get; }
}

internal sealed record AiDirectiveAuditGatewaySnapshot
{
    public AiDirectiveAuditGatewaySnapshot(
        bool wasRequested,
        string? providerId = null,
        string? modelId = null,
        AiProcessingMode? processingMode = null,
        int? maxOutputTokens = null,
        TimeSpan? timeout = null,
        int toolCount = 0,
        AiGatewayCallResult? result = null,
        AiFinishReason? finishReason = null,
        string? errorCode = null)
    {
        if (processingMode is { } mode)
        {
            AiProcessingModeContract.RequireDefined(mode, nameof(processingMode));
        }

        if (maxOutputTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        }

        if (timeout is { } timeoutValue && timeoutValue <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (toolCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toolCount));
        }

        if (result is { } callResult)
        {
            AiGatewayCallResultContract.RequireDefined(callResult, nameof(result));
        }

        if (finishReason is { } reason)
        {
            AiFinishReasonContract.RequireDefined(reason, nameof(finishReason));
        }

        WasRequested = wasRequested;
        ProviderId = providerId is null
            ? null
            : AiAgentGatewayText.Require(providerId, nameof(providerId));
        ModelId = modelId is null
            ? null
            : AiAgentGatewayText.Require(modelId, nameof(modelId));
        ProcessingMode = processingMode;
        MaxOutputTokens = maxOutputTokens;
        Timeout = timeout;
        ToolCount = toolCount;
        Result = result;
        FinishReason = finishReason;
        ErrorCode = errorCode is null
            ? null
            : AiAgentGatewayText.Require(errorCode, nameof(errorCode));
    }

    public bool WasRequested { get; }

    public string? ProviderId { get; }

    public string? ModelId { get; }

    public AiProcessingMode? ProcessingMode { get; }

    public int? MaxOutputTokens { get; }

    public TimeSpan? Timeout { get; }

    public int ToolCount { get; }

    public AiGatewayCallResult? Result { get; }

    public AiFinishReason? FinishReason { get; }

    public string? ErrorCode { get; }
}

internal sealed record AiDirectiveAuditDecisionSnapshot
{
    public AiDirectiveAuditDecisionSnapshot(
        AiDirectiveInterpretationOutcomeKind outcome,
        string? decisionKind = null,
        string? failureCode = null,
        int parseErrorCount = 0,
        bool? isRetryable = null)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Unknown AI directive interpretation outcome.");
        }

        if (parseErrorCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parseErrorCount));
        }

        Outcome = outcome;
        DecisionKind = decisionKind is null
            ? null
            : AiAgentGatewayText.Require(decisionKind, nameof(decisionKind));
        FailureCode = failureCode is null
            ? null
            : AiAgentGatewayText.Require(failureCode, nameof(failureCode));
        ParseErrorCount = parseErrorCount;
        IsRetryable = isRetryable;
    }

    public AiDirectiveInterpretationOutcomeKind Outcome { get; }

    public string? DecisionKind { get; }

    public string? FailureCode { get; }

    public int ParseErrorCount { get; }

    public bool? IsRetryable { get; }
}

internal sealed record AiDirectiveAuditResultMessageSnapshot
{
    public AiDirectiveAuditResultMessageSnapshot(
        string? messageType,
        MessageId? messageId = null,
        EndpointRef? from = null,
        EndpointRef? to = null,
        string? failureCode = null)
    {
        MessageType = messageType is null
            ? null
            : AiAgentGatewayText.Require(messageType, nameof(messageType));
        MessageId = messageId;
        From = from;
        To = to;
        FailureCode = failureCode is null
            ? null
            : AiAgentGatewayText.Require(failureCode, nameof(failureCode));
    }

    public string? MessageType { get; }

    public MessageId? MessageId { get; }

    public EndpointRef? From { get; }

    public EndpointRef? To { get; }

    public string? FailureCode { get; }
}

internal sealed record AiDirectiveAuditPositionEffectsSnapshot
{
    public AiDirectiveAuditPositionEffectsSnapshot(
        IEnumerable<string>? commandTypes = null,
        string? failureCode = null)
    {
        CommandTypes = SnapshotCommandTypes(commandTypes);
        FailureCode = failureCode is null
            ? null
            : AiAgentGatewayText.Require(failureCode, nameof(failureCode));
    }

    public ImmutableArray<string> CommandTypes { get; }

    public string? FailureCode { get; }

    private static ImmutableArray<string> SnapshotCommandTypes(IEnumerable<string>? source)
    {
        if (source is null)
        {
            return ImmutableArray<string>.Empty;
        }

        return source.Select(commandType => AiAgentGatewayText.Require(
                commandType,
                nameof(source)))
            .ToImmutableArray();
    }
}

internal static class AiDirectiveAuditSnapshotFactory
{
    public static AiDirectiveAuditSnapshot Create(
        AiDirectiveExecutionContext context,
        AiDirectiveProcessingSnapshot processing,
        AiGatewayRequest? gatewayRequest = null,
        AiAgentGatewayInvocationResult? gatewayInvocation = null,
        AiDirectiveInterpretationResult? interpretation = null,
        AiDirectiveResultMessage? resultMessage = null,
        AiDirectiveIterationAuditTrail? iterationAudit = null,
        AiDirectivePositionEffects? positionEffects = null,
        DateTimeOffset? recordedAt = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(processing);

        EnsureCorrelation(context.CorrelationId, processing.CorrelationId, nameof(processing));
        EnsureCorrelation(context.CorrelationId, gatewayInvocation?.CorrelationId, nameof(gatewayInvocation));
        EnsureCorrelation(context.CorrelationId, interpretation?.CorrelationId, nameof(interpretation));
        EnsureCorrelation(context.CorrelationId, resultMessage?.CorrelationId, nameof(resultMessage));
        EnsureCorrelation(context.CorrelationId, iterationAudit?.CorrelationId, nameof(iterationAudit));
        EnsureCorrelation(context.CorrelationId, positionEffects?.CorrelationId, nameof(positionEffects));

        var redactions = new List<AiGatewayAuditRedaction>();
        AddContextRedactions(context, redactions);
        AddGatewayRedactions(gatewayRequest, gatewayInvocation, redactions);
        AddResultMessageRedactions(resultMessage, redactions);
        AddPositionEffectRedactions(positionEffects, redactions);

        return new AiDirectiveAuditSnapshot(
            context.CorrelationId,
            recordedAt ?? DateTimeOffset.UtcNow,
            processing.Status,
            TerminalCode(processing, interpretation, resultMessage, iterationAudit),
            CreateContextSnapshot(context),
            CreateGatewaySnapshot(gatewayRequest, gatewayInvocation),
            TerminalReason(processing, interpretation, resultMessage, iterationAudit),
            CreateDecisionSnapshot(interpretation),
            CreateResultMessageSnapshot(resultMessage),
            iterationAudit,
            CreatePositionEffectsSnapshot(positionEffects),
            redactions);
    }

    private static AiDirectiveAuditContextSnapshot CreateContextSnapshot(
        AiDirectiveExecutionContext context) =>
        new(
            context.OrganizationId,
            context.PositionId,
            context.Occupant,
            context.Directive.ThreadId,
            context.Directive.DirectiveId,
            context.Directive.MessageId,
            context.IdentityPromptRef,
            context.IdentityPrompt?.Path,
            context.ShortMemory.Length,
            context.OpenTasks.Length,
            context.RecentHistory.Length,
            context.AuthorizedTools.Select(tool =>
                new AiDirectiveAuditToolSnapshot(tool.Connector, tool.Scope.Length)),
            context.Authority.CanDecide.Length,
            context.Authority.Overrides.Length);

    private static AiDirectiveAuditGatewaySnapshot CreateGatewaySnapshot(
        AiGatewayRequest? request,
        AiAgentGatewayInvocationResult? invocation)
    {
        var response = invocation?.Response;
        var provider = request?.Provider ?? response?.Provider;
        var error = response?.Error;

        return new AiDirectiveAuditGatewaySnapshot(
            wasRequested: request is not null,
            provider?.ProviderId,
            provider?.ModelId,
            request?.ProcessingMode,
            request?.ModelParameters.MaxOutputTokens,
            request?.Timeout,
            request?.Tools.Count ?? 0,
            response is null
                ? null
                : response.IsSuccess
                    ? AiGatewayCallResult.Succeeded
                    : AiGatewayCallResult.Failed,
            response?.FinishReason,
            error is null ? null : AiGatewayErrorCodeContract.ToWireValue(error.Code));
    }

    private static AiDirectiveAuditDecisionSnapshot? CreateDecisionSnapshot(
        AiDirectiveInterpretationResult? interpretation)
    {
        if (interpretation is null)
        {
            return null;
        }

        return new AiDirectiveAuditDecisionSnapshot(
            interpretation.Outcome,
            DecisionKind(interpretation.Decision),
            interpretation.Failure?.Code,
            interpretation.Failure?.ParseErrors.Length ?? 0,
            interpretation.Failure?.IsRetryable);
    }

    private static AiDirectiveAuditResultMessageSnapshot? CreateResultMessageSnapshot(
        AiDirectiveResultMessage? resultMessage)
    {
        if (resultMessage is null)
        {
            return null;
        }

        var message = resultMessage.Message;
        return new AiDirectiveAuditResultMessageSnapshot(
            message?.GetType().Name,
            message?.Id,
            message?.From,
            message?.To,
            resultMessage.Failure?.Code);
    }

    private static AiDirectiveAuditPositionEffectsSnapshot? CreatePositionEffectsSnapshot(
        AiDirectivePositionEffects? positionEffects)
    {
        if (positionEffects is null)
        {
            return null;
        }

        return new AiDirectiveAuditPositionEffectsSnapshot(
            positionEffects.Commands.Select(command => command.GetType().Name),
            positionEffects.Failure?.Code);
    }

    private static string? DecisionKind(AiDirectiveDecision? decision) =>
        decision switch
        {
            AiDirectiveReportDecision => "Report",
            AiDirectiveEscalationDecision => "Escalation",
            AiDirectiveChildDirectiveDecision => "Directive",
            null => null,
            _ => decision.GetType().Name,
        };

    private static string TerminalCode(
        AiDirectiveProcessingSnapshot processing,
        AiDirectiveInterpretationResult? interpretation,
        AiDirectiveResultMessage? resultMessage,
        AiDirectiveIterationAuditTrail? iterationAudit)
    {
        if (resultMessage?.Failure is { } resultFailure)
        {
            return resultFailure.Code;
        }

        if (interpretation?.Failure is { } interpretationFailure)
        {
            return interpretationFailure.Code;
        }

        if (processing.Status == AiDirectiveProcessingStatus.ResultEmitted)
        {
            return "result-emitted";
        }

        if (iterationAudit?.TerminalCode is { } iterationCode)
        {
            return iterationCode;
        }

        if (processing.TerminalReason?.Contains(
                "Identity prompt",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            return "identity-prompt-unresolved";
        }

        return processing.Status switch
        {
            AiDirectiveProcessingStatus.Failed => "processing-failed",
            AiDirectiveProcessingStatus.Escalated => "processing-escalated",
            _ => "processing-terminal",
        };
    }

    private static string? TerminalReason(
        AiDirectiveProcessingSnapshot processing,
        AiDirectiveInterpretationResult? interpretation,
        AiDirectiveResultMessage? resultMessage,
        AiDirectiveIterationAuditTrail? iterationAudit) =>
        resultMessage?.Failure?.AuditReason
        ?? interpretation?.Failure?.AuditReason
        ?? iterationAudit?.TerminalAuditReason
        ?? processing.TerminalReason;

    private static void AddContextRedactions(
        AiDirectiveExecutionContext context,
        List<AiGatewayAuditRedaction> redactions)
    {
        Add(redactions, "context.directive.objective", "free-text");
        Add(redactions, "context.directive.context", "free-text");

        if (context.IdentityPrompt is not null)
        {
            Add(redactions, "context.identityPrompt.content", "free-text");
        }

        if (!context.ShortMemory.IsEmpty)
        {
            Add(redactions, "context.shortMemory.values", "free-text");
        }

        if (!context.OpenTasks.IsEmpty)
        {
            Add(redactions, "context.openTasks.titles", "free-text");
        }
    }

    private static void AddGatewayRedactions(
        AiGatewayRequest? request,
        AiAgentGatewayInvocationResult? invocation,
        List<AiGatewayAuditRedaction> redactions)
    {
        if (request is not null)
        {
            Add(redactions, "gateway.request.content", "free-text");
            if (request.SystemInstruction is not null)
            {
                Add(redactions, "gateway.request.systemInstruction", "free-text");
            }
        }

        var response = invocation?.Response;
        if (response?.Text is not null)
        {
            Add(redactions, "gateway.response.text", "provider-output");
        }

        if (response?.Error?.Message is not null)
        {
            Add(redactions, "gateway.error.message", "free-text");
        }

        if (response?.ToolCalls.Count > 0)
        {
            Add(redactions, "gateway.response.toolCalls.arguments", "provider-output");
        }
    }

    private static void AddResultMessageRedactions(
        AiDirectiveResultMessage? resultMessage,
        List<AiGatewayAuditRedaction> redactions)
    {
        switch (resultMessage?.Message)
        {
            case Report:
                Add(redactions, "resultMessage.report.body", "free-text");
                break;
            case Escalation:
                Add(redactions, "resultMessage.escalation.issue", "free-text");
                Add(redactions, "resultMessage.escalation.context", "free-text");
                Add(redactions, "resultMessage.escalation.optionsConsidered", "free-text");
                break;
            case OrgDirective:
                Add(redactions, "resultMessage.directive.objective", "free-text");
                Add(redactions, "resultMessage.directive.context", "free-text");
                break;
        }
    }

    private static void AddPositionEffectRedactions(
        AiDirectivePositionEffects? positionEffects,
        List<AiGatewayAuditRedaction> redactions)
    {
        if (positionEffects is null)
        {
            return;
        }

        for (var index = 0; index < positionEffects.Commands.Count; index++)
        {
            switch (positionEffects.Commands[index])
            {
                case UpdateShortMemory:
                    Add(redactions, $"positionEffects.commands[{index}].value", "free-text");
                    break;
                case UpdateTask:
                    Add(redactions, $"positionEffects.commands[{index}].note", "free-text");
                    break;
                case CompleteTask:
                    Add(redactions, $"positionEffects.commands[{index}].summary", "free-text");
                    break;
                case OpenTask:
                    Add(redactions, $"positionEffects.commands[{index}].title", "free-text");
                    break;
            }
        }
    }

    private static void EnsureCorrelation(
        string expected,
        string? actual,
        string parameterName)
    {
        if (actual is null)
        {
            return;
        }

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "AI directive audit artifact correlation must match the execution context.",
                parameterName);
        }
    }

    private static void Add(
        List<AiGatewayAuditRedaction> redactions,
        string path,
        string reason)
    {
        if (redactions.Any(redaction =>
            string.Equals(redaction.Path, path, StringComparison.Ordinal)
            && string.Equals(redaction.Reason, reason, StringComparison.Ordinal)))
        {
            return;
        }

        redactions.Add(new AiGatewayAuditRedaction(path, reason));
    }
}

internal sealed record GetAiDirectiveAuditSnapshot
{
    public GetAiDirectiveAuditSnapshot(string correlationId)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
    }

    public string CorrelationId { get; }
}

internal sealed record AiDirectiveAuditSnapshotQueryResult
{
    private AiDirectiveAuditSnapshotQueryResult(
        string correlationId,
        AiDirectiveAuditSnapshot? snapshot)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Snapshot = snapshot;
    }

    public string CorrelationId { get; }

    public AiDirectiveAuditSnapshot? Snapshot { get; }

    public bool Found => Snapshot is not null;

    public static AiDirectiveAuditSnapshotQueryResult FoundSnapshot(
        AiDirectiveAuditSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new AiDirectiveAuditSnapshotQueryResult(
            snapshot.CorrelationId,
            snapshot);
    }

    public static AiDirectiveAuditSnapshotQueryResult Missing(string correlationId) =>
        new(correlationId, snapshot: null);
}
