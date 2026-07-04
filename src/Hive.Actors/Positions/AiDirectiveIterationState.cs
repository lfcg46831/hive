using System.Collections.Immutable;
using Hive.Domain.Ai;

namespace Hive.Actors.Positions;

internal enum AiDirectiveIterationContinuationKind
{
    Inference = 1,
    ConnectorTool = 2,
}

internal enum AiDirectiveIterationStopKind
{
    Completed = 1,
    Timeout = 2,
    BudgetExceeded = 3,
    MaxIterationsReached = 4,
    ToolCallNotAllowed = 5,
}

internal sealed record AiDirectiveIterationHistoryEntry
{
    public AiDirectiveIterationHistoryEntry(int iteration, DateTimeOffset startedAt)
    {
        if (iteration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iteration),
                iteration,
                "AI directive iteration must be greater than zero.");
        }

        if (startedAt == default)
        {
            throw new ArgumentException(
                "AI directive iteration start time must be specified.",
                nameof(startedAt));
        }

        Iteration = iteration;
        StartedAt = startedAt;
    }

    public int Iteration { get; }

    public DateTimeOffset StartedAt { get; }
}

internal sealed record AiDirectiveIterationContinuation
{
    private AiDirectiveIterationContinuation(
        AiDirectiveIterationContinuationKind kind,
        AiToolCall? toolCall)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown AI directive iteration continuation kind.");
        }

        if (kind == AiDirectiveIterationContinuationKind.ConnectorTool && toolCall is null)
        {
            throw new ArgumentNullException(nameof(toolCall));
        }

        if (kind == AiDirectiveIterationContinuationKind.Inference && toolCall is not null)
        {
            throw new ArgumentException(
                "Inference continuation cannot carry a connector tool call.",
                nameof(toolCall));
        }

        Kind = kind;
        ToolCall = toolCall;
    }

    public AiDirectiveIterationContinuationKind Kind { get; }

    public AiToolCall? ToolCall { get; }

    public static AiDirectiveIterationContinuation Inference() =>
        new(AiDirectiveIterationContinuationKind.Inference, toolCall: null);

    public static AiDirectiveIterationContinuation ConnectorTool(AiToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        return new AiDirectiveIterationContinuation(
            AiDirectiveIterationContinuationKind.ConnectorTool,
            toolCall);
    }
}

internal sealed record AiDirectiveIterationStopReason
{
    public AiDirectiveIterationStopReason(
        AiDirectiveIterationStopKind kind,
        string code,
        string auditReason)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown AI directive iteration stop kind.");
        }

        Kind = kind;
        Code = AiAgentGatewayText.Require(code, nameof(code));
        AuditReason = AiAgentGatewayText.Require(auditReason, nameof(auditReason));
    }

    public AiDirectiveIterationStopKind Kind { get; }

    public string Code { get; }

    public string AuditReason { get; }
}

internal sealed record AiDirectiveIterationDecision
{
    private AiDirectiveIterationDecision(
        AiDirectiveIterationStopReason? stopReason,
        ImmutableArray<AiDirectiveIterationContinuation> continuations)
    {
        if (continuations.IsDefault)
        {
            throw new ArgumentException(
                "AI directive iteration continuations cannot be default.",
                nameof(continuations));
        }

        foreach (var continuation in continuations)
        {
            if (continuation is null)
            {
                throw new ArgumentException(
                    "AI directive iteration continuations cannot contain null entries.",
                    nameof(continuations));
            }
        }

        StopReason = stopReason;
        Continuations = continuations;
    }

    public bool CanContinue => StopReason is null;

    public bool ShouldStop => StopReason is not null;

    public AiDirectiveIterationStopReason? StopReason { get; }

    public ImmutableArray<AiDirectiveIterationContinuation> Continuations { get; }

    public static AiDirectiveIterationDecision Continue(
        IEnumerable<AiDirectiveIterationContinuation> continuations)
    {
        ArgumentNullException.ThrowIfNull(continuations);

        var snapshot = continuations.ToImmutableArray();
        if (snapshot.IsEmpty)
        {
            throw new ArgumentException(
                "AI directive iteration continuation requires at least one continuation.",
                nameof(continuations));
        }

        return new AiDirectiveIterationDecision(stopReason: null, snapshot);
    }

    public static AiDirectiveIterationDecision Stop(AiDirectiveIterationStopReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        return new AiDirectiveIterationDecision(reason, ImmutableArray<AiDirectiveIterationContinuation>.Empty);
    }
}

internal sealed record AiDirectiveIterationState
{
    private AiDirectiveIterationState(
        string correlationId,
        int currentIteration,
        DateTimeOffset startedAt,
        DateTimeOffset? deadline,
        int? maxIterations,
        ImmutableArray<string> authorizedToolNames,
        ImmutableArray<AiDirectiveIterationHistoryEntry> history)
    {
        if (currentIteration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentIteration),
                currentIteration,
                "AI directive iteration must be greater than zero.");
        }

        if (startedAt == default)
        {
            throw new ArgumentException(
                "AI directive iteration start time must be specified.",
                nameof(startedAt));
        }

        if (deadline is { } deadlineValue && deadlineValue <= startedAt)
        {
            throw new ArgumentException(
                "AI directive iteration deadline must be after start time.",
                nameof(deadline));
        }

        if (maxIterations is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxIterations),
                maxIterations,
                "AI directive max iterations must be greater than zero.");
        }

        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        CurrentIteration = currentIteration;
        StartedAt = startedAt;
        Deadline = deadline;
        MaxIterations = maxIterations;
        AuthorizedToolNames = RequireToolNames(authorizedToolNames, nameof(authorizedToolNames));
        History = RequireHistory(history, nameof(history));
    }

    public string CorrelationId { get; }

    public int CurrentIteration { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? Deadline { get; }

    public int? MaxIterations { get; }

    public ImmutableArray<string> AuthorizedToolNames { get; }

    public ImmutableArray<AiDirectiveIterationHistoryEntry> History { get; }

    public static AiDirectiveIterationState Start(
        AiDirectiveExecutionContext context,
        DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(context);

        var deadline = context.Limits.Timeout is { } timeout
            ? startedAt.Add(timeout)
            : (DateTimeOffset?)null;

        return new AiDirectiveIterationState(
            context.CorrelationId,
            currentIteration: 1,
            startedAt,
            deadline,
            context.Limits.MaxIterations,
            AuthorizedToolNamesFrom(context),
            ImmutableArray.Create(new AiDirectiveIterationHistoryEntry(1, startedAt)));
    }

    public AiDirectiveIterationDecision Evaluate(
        AiGatewayResponse response,
        DateTimeOffset observedAt,
        bool hasAvailableBudget)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.ToolCalls.Count == 0)
        {
            return StopCompleted();
        }

        return EvaluateContinuations(
            response.ToolCalls.Select(AiDirectiveIterationContinuation.ConnectorTool),
            observedAt,
            hasAvailableBudget);
    }

    public AiDirectiveIterationDecision EvaluateInference(
        DateTimeOffset observedAt,
        bool hasAvailableBudget) =>
        EvaluateContinuations(
            [AiDirectiveIterationContinuation.Inference()],
            observedAt,
            hasAvailableBudget);

    public AiDirectiveIterationState Advance(
        AiDirectiveIterationDecision decision,
        DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (!decision.CanContinue)
        {
            throw new InvalidOperationException(
                "Cannot advance AI directive iteration from a stop decision.");
        }

        var nextIteration = CurrentIteration + 1;
        return new AiDirectiveIterationState(
            CorrelationId,
            nextIteration,
            StartedAt,
            Deadline,
            MaxIterations,
            AuthorizedToolNames,
            History.Add(new AiDirectiveIterationHistoryEntry(nextIteration, startedAt)));
    }

    private AiDirectiveIterationDecision EvaluateContinuations(
        IEnumerable<AiDirectiveIterationContinuation> continuations,
        DateTimeOffset observedAt,
        bool hasAvailableBudget)
    {
        if (observedAt == default)
        {
            throw new ArgumentException(
                "AI directive iteration observation time must be specified.",
                nameof(observedAt));
        }

        if (Deadline is { } deadline && observedAt >= deadline)
        {
            return Stop(
                AiDirectiveIterationStopKind.Timeout,
                "timeout",
                $"AI directive loop reached timeout deadline '{deadline:O}'.");
        }

        if (!hasAvailableBudget)
        {
            return Stop(
                AiDirectiveIterationStopKind.BudgetExceeded,
                "budget-exceeded",
                "AI directive loop cannot continue because budget is unavailable.");
        }

        if (MaxIterations is { } maxIterations && CurrentIteration >= maxIterations)
        {
            return Stop(
                AiDirectiveIterationStopKind.MaxIterationsReached,
                "max-iterations-reached",
                $"AI directive loop reached the configured maximum of {maxIterations} iteration(s).");
        }

        var snapshot = continuations.ToImmutableArray();
        var validationFailure = ValidateContinuations(snapshot);
        return validationFailure is null
            ? AiDirectiveIterationDecision.Continue(snapshot)
            : AiDirectiveIterationDecision.Stop(validationFailure);
    }

    private AiDirectiveIterationStopReason? ValidateContinuations(
        ImmutableArray<AiDirectiveIterationContinuation> continuations)
    {
        if (continuations.IsDefaultOrEmpty)
        {
            return ToolCallNotAllowed("AI directive loop received no continuation to execute.");
        }

        var seenConnectorTools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var continuation in continuations)
        {
            if (continuation.Kind == AiDirectiveIterationContinuationKind.Inference)
            {
                continue;
            }

            var name = continuation.ToolCall?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return ToolCallNotAllowed("AI directive loop received a connector tool call without a valid name.");
            }

            if (!AuthorizedToolNames.Contains(name, StringComparer.Ordinal))
            {
                return ToolCallNotAllowed(
                    $"AI directive loop rejected unauthorized tool call '{name}'.");
            }

            if (!seenConnectorTools.Add(name))
            {
                return ToolCallNotAllowed(
                    $"AI directive loop rejected duplicate tool call '{name}'.");
            }
        }

        return null;
    }

    private static AiDirectiveIterationDecision StopCompleted() =>
        Stop(
            AiDirectiveIterationStopKind.Completed,
            "completed",
            "AI directive loop completed without requesting another tool call.");

    private static AiDirectiveIterationDecision Stop(
        AiDirectiveIterationStopKind kind,
        string code,
        string auditReason) =>
        AiDirectiveIterationDecision.Stop(
            new AiDirectiveIterationStopReason(kind, code, auditReason));

    private static AiDirectiveIterationStopReason ToolCallNotAllowed(string auditReason) =>
        new(
            AiDirectiveIterationStopKind.ToolCallNotAllowed,
            "tool-call-not-allowed",
            auditReason);

    private static ImmutableArray<string> AuthorizedToolNamesFrom(
        AiDirectiveExecutionContext context)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tool in context.AuthorizedTools.OrderBy(
            tool => tool.Connector,
            StringComparer.Ordinal))
        {
            if (!seen.Add(tool.Connector))
            {
                throw new ArgumentException(
                    $"Authorized tool '{tool.Connector}' was supplied more than once.",
                    nameof(context));
            }

            builder.Add(tool.Connector);
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<string> RequireToolNames(
        ImmutableArray<string> values,
        string parameterName)
    {
        if (values.IsDefault)
        {
            throw new ArgumentException(
                "Authorized tool names cannot be default.",
                parameterName);
        }

        foreach (var value in values)
        {
            AiAgentGatewayText.Require(value, parameterName);
        }

        return values;
    }

    private static ImmutableArray<AiDirectiveIterationHistoryEntry> RequireHistory(
        ImmutableArray<AiDirectiveIterationHistoryEntry> values,
        string parameterName)
    {
        if (values.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "AI directive iteration history cannot be empty.",
                parameterName);
        }

        foreach (var value in values)
        {
            if (value is null)
            {
                throw new ArgumentException(
                    "AI directive iteration history cannot contain null entries.",
                    parameterName);
            }
        }

        return values;
    }
}
