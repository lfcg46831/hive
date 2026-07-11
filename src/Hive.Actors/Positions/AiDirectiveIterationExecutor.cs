using System.Collections.Immutable;
using System.Globalization;
using Hive.Domain.Ai;

namespace Hive.Actors.Positions;

internal enum AiDirectiveIterationExecutionKind
{
    Inference = 1,
    ConnectorTool = 2,
}

internal sealed record AiDirectiveIterationExecutionFailure
{
    public AiDirectiveIterationExecutionFailure(
        string code,
        string auditReason,
        AiAgentActionGateResult? actionGateResult = null)
    {
        Code = AiAgentGatewayText.Require(code, nameof(code));
        AuditReason = AiAgentGatewayText.Require(auditReason, nameof(auditReason));
        ActionGateResult = actionGateResult;
    }

    public string Code { get; }

    public string AuditReason { get; }

    public AiAgentActionGateResult? ActionGateResult { get; }
}

internal sealed record AiDirectiveConnectorToolExecution
{
    public AiDirectiveConnectorToolExecution(
        AiDirectiveExecutionContext context,
        int iteration,
        AiToolCall toolCall)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        if (iteration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iteration),
                iteration,
                "AI directive connector tool iteration must be greater than zero.");
        }

        Iteration = iteration;
        ToolCall = toolCall ?? throw new ArgumentNullException(nameof(toolCall));
    }

    public AiDirectiveExecutionContext Context { get; }

    public int Iteration { get; }

    public AiToolCall ToolCall { get; }
}

internal sealed record AiDirectiveConnectorToolExecutionResult
{
    private AiDirectiveConnectorToolExecutionResult(
        AiDirectiveConnectorToolExecution execution,
        ImmutableDictionary<string, object?> output,
        AiDirectiveIterationExecutionFailure? failure)
    {
        Execution = execution ?? throw new ArgumentNullException(nameof(execution));
        if (output is null)
        {
            throw new ArgumentException(
                "Connector tool execution output cannot be default.",
                nameof(output));
        }

        Output = output;
        Failure = failure;
    }

    public AiDirectiveConnectorToolExecution Execution { get; }

    public IReadOnlyDictionary<string, object?> Output { get; }

    public AiDirectiveIterationExecutionFailure? Failure { get; }

    public bool IsSuccess => Failure is null;

    public bool IsFailure => !IsSuccess;

    public static AiDirectiveConnectorToolExecutionResult Succeeded(
        AiDirectiveConnectorToolExecution execution,
        IReadOnlyDictionary<string, object?>? output = null) =>
        new(execution, SnapshotData(output, nameof(output)), failure: null);

    public static AiDirectiveConnectorToolExecutionResult Failed(
        AiDirectiveConnectorToolExecution execution,
        AiDirectiveIterationExecutionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new(
            execution,
            ImmutableDictionary<string, object?>.Empty.WithComparers(StringComparer.Ordinal),
            failure);
    }

    private static ImmutableDictionary<string, object?> SnapshotData(
        IReadOnlyDictionary<string, object?>? data,
        string parameterName)
    {
        if (data is null)
        {
            return ImmutableDictionary<string, object?>.Empty.WithComparers(StringComparer.Ordinal);
        }

        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in data)
        {
            builder[AiAgentGatewayText.Require(key, parameterName)] = value;
        }

        return builder.ToImmutable();
    }
}

internal sealed record AiDirectiveIterationExecutionResult
{
    private AiDirectiveIterationExecutionResult(
        string correlationId,
        AiDirectiveIterationExecutionKind? kind,
        AiAgentGatewayInvocationResult? inferenceResult,
        AiDirectiveConnectorToolExecutionResult? toolResult,
        AiDirectiveIterationExecutionFailure? failure)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Kind = kind;
        InferenceResult = inferenceResult;
        ToolResult = toolResult;
        Failure = failure;
    }

    public string CorrelationId { get; }

    public AiDirectiveIterationExecutionKind? Kind { get; }

    public AiAgentGatewayInvocationResult? InferenceResult { get; }

    public AiDirectiveConnectorToolExecutionResult? ToolResult { get; }

    public AiDirectiveIterationExecutionFailure? Failure { get; }

    public bool IsSuccess => Failure is null;

    public bool IsFailure => !IsSuccess;

    public static AiDirectiveIterationExecutionResult InferenceSucceeded(
        AiAgentGatewayInvocationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new AiDirectiveIterationExecutionResult(
            result.CorrelationId,
            AiDirectiveIterationExecutionKind.Inference,
            result,
            toolResult: null,
            failure: null);
    }

    public static AiDirectiveIterationExecutionResult ConnectorToolSucceeded(
        string correlationId,
        AiDirectiveConnectorToolExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new AiDirectiveIterationExecutionResult(
            correlationId,
            AiDirectiveIterationExecutionKind.ConnectorTool,
            inferenceResult: null,
            result,
            failure: null);
    }

    public static AiDirectiveIterationExecutionResult Failed(
        string correlationId,
        AiDirectiveIterationExecutionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new AiDirectiveIterationExecutionResult(
            correlationId,
            kind: null,
            inferenceResult: null,
            toolResult: null,
            failure);
    }
}

internal interface IAiDirectiveConnectorToolExecutor
{
    ValueTask<AiDirectiveConnectorToolExecutionResult> ExecuteAsync(
        AiDirectiveConnectorToolExecution execution,
        CancellationToken cancellationToken = default);
}

internal sealed class UnavailableAiDirectiveConnectorToolExecutor : IAiDirectiveConnectorToolExecutor
{
    public static UnavailableAiDirectiveConnectorToolExecutor Instance { get; } = new();

    private UnavailableAiDirectiveConnectorToolExecutor()
    {
    }

    public ValueTask<AiDirectiveConnectorToolExecutionResult> ExecuteAsync(
        AiDirectiveConnectorToolExecution execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            AiDirectiveConnectorToolExecutionResult.Failed(
                execution,
                new AiDirectiveIterationExecutionFailure(
                    "connector-tool-executor-unavailable",
                    "AI directive connector tool executor is not configured for this actor.")));
    }
}

internal sealed class AiDirectiveIterationExecutor
{
    private readonly IAiAgentGatewayInvoker _gatewayInvoker;
    private readonly IAiDirectiveConnectorToolExecutor _toolExecutor;
    private readonly IAiAgentActionGate _actionGate;

    public AiDirectiveIterationExecutor(IAiAgentGatewayInvoker gatewayInvoker)
        : this(
            gatewayInvoker,
            UnavailableAiDirectiveConnectorToolExecutor.Instance,
            AllowingAiAgentActionGate.Instance)
    {
    }

    public AiDirectiveIterationExecutor(
        IAiAgentGatewayInvoker gatewayInvoker,
        IAiDirectiveConnectorToolExecutor toolExecutor)
        : this(gatewayInvoker, toolExecutor, AllowingAiAgentActionGate.Instance)
    {
    }

    public AiDirectiveIterationExecutor(
        IAiAgentGatewayInvoker gatewayInvoker,
        IAiDirectiveConnectorToolExecutor toolExecutor,
        IAiAgentActionGate actionGate)
    {
        _gatewayInvoker = gatewayInvoker ?? throw new ArgumentNullException(nameof(gatewayInvoker));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _actionGate = actionGate ?? throw new ArgumentNullException(nameof(actionGate));
    }

    public async ValueTask<AiDirectiveIterationExecutionResult> ExecuteAsync(
        AiDirectiveExecutionContext context,
        AiDirectiveIterationState state,
        AiDirectiveIterationDecision decision,
        bool hasAvailableBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(context.CorrelationId, state.CorrelationId, StringComparison.Ordinal))
        {
            return Failed(
                state.CorrelationId,
                "correlation-mismatch",
                $"AI directive iteration state correlation '{state.CorrelationId}' does not match context correlation '{context.CorrelationId}'.");
        }

        if (decision.ShouldStop)
        {
            return Failed(
                state.CorrelationId,
                "iteration-stop-decision",
                "AI directive iteration execution cannot run a stop decision.");
        }

        if (decision.Continuations.Length != 1)
        {
            return Failed(
                state.CorrelationId,
                "single-continuation-required",
                "AI directive iteration execution requires exactly one continuation.");
        }

        if (!hasAvailableBudget)
        {
            return Failed(
                state.CorrelationId,
                "budget-exceeded",
                "AI directive iteration execution cannot continue because budget is unavailable.");
        }

        var continuation = decision.Continuations[0];
        return continuation.Kind switch
        {
            AiDirectiveIterationContinuationKind.Inference =>
                await ExecuteInferenceAsync(context, state, cancellationToken).ConfigureAwait(false),
            AiDirectiveIterationContinuationKind.ConnectorTool =>
                await ExecuteConnectorToolAsync(
                    context,
                    state,
                    continuation,
                    cancellationToken).ConfigureAwait(false),
            _ => Failed(
                state.CorrelationId,
                "continuation-kind-unknown",
                "AI directive iteration execution received an unknown continuation kind."),
        };
    }

    private async ValueTask<AiDirectiveIterationExecutionResult> ExecuteInferenceAsync(
        AiDirectiveExecutionContext context,
        AiDirectiveIterationState state,
        CancellationToken cancellationToken)
    {
        try
        {
            var invocation = new AiAgentGatewayInvocation(
                state.CorrelationId,
                CreateInferenceRequest(context, state));
            var result = await _gatewayInvoker
                .InvokeAsync(invocation, cancellationToken)
                .ConfigureAwait(false);

            return AiDirectiveIterationExecutionResult.InferenceSucceeded(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Failed(
                state.CorrelationId,
                "inference-execution-failed",
                "AI directive iteration inference failed before returning a structured response.");
        }
    }

    private async ValueTask<AiDirectiveIterationExecutionResult> ExecuteConnectorToolAsync(
        AiDirectiveExecutionContext context,
        AiDirectiveIterationState state,
        AiDirectiveIterationContinuation continuation,
        CancellationToken cancellationToken)
    {
        var toolCall = continuation.ToolCall
            ?? throw new InvalidOperationException(
                "Connector tool continuation did not carry a tool call.");

        if (!IsToolAuthorized(context, state, toolCall.Name))
        {
            return Failed(
                state.CorrelationId,
                "tool-call-not-allowed",
                $"AI directive iteration rejected unauthorized tool call '{toolCall.Name}'.");
        }

        var gateResult = await _actionGate
            .EvaluateAsync(
                context,
                AiAgentActionCandidate.ForTool(toolCall, continuation.ActingUnder),
                cancellationToken)
            .ConfigureAwait(false);
        if (gateResult.IsRetained)
        {
            return AiDirectiveIterationExecutionResult.Failed(
                state.CorrelationId,
                new AiDirectiveIterationExecutionFailure(
                    gateResult.Code,
                    $"AI connector tool action was retained by the authority gate with code '{gateResult.Code}'.",
                    gateResult));
        }

        var execution = new AiDirectiveConnectorToolExecution(
            context,
            state.CurrentIteration + 1,
            toolCall);

        try
        {
            var result = await _toolExecutor
                .ExecuteAsync(execution, cancellationToken)
                .ConfigureAwait(false);

            return result.IsSuccess
                ? AiDirectiveIterationExecutionResult.ConnectorToolSucceeded(
                    state.CorrelationId,
                    result)
                : AiDirectiveIterationExecutionResult.Failed(
                    state.CorrelationId,
                    result.Failure!);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Failed(
                state.CorrelationId,
                "connector-tool-execution-failed",
                $"AI directive connector tool '{toolCall.Name}' failed before returning a structured response.");
        }
    }

    private static AiGatewayRequest CreateInferenceRequest(
        AiDirectiveExecutionContext context,
        AiDirectiveIterationState state)
    {
        var request = AiDirectivePrompt.CreateInitialRequest(context);
        var metadata = new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal)
        {
            ["iteration"] = (state.CurrentIteration + 1).ToString(CultureInfo.InvariantCulture),
        };

        return new AiGatewayRequest(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            request.Content,
            request.SystemInstruction,
            request.ContextMessages,
            request.Tools,
            request.ModelParameters,
            metadata,
            request.Provider,
            request.ProcessingMode,
            request.Timeout,
            request.Policy);
    }

    private static bool IsToolAuthorized(
        AiDirectiveExecutionContext context,
        AiDirectiveIterationState state,
        string toolName) =>
        state.AuthorizedToolNames.Contains(toolName, StringComparer.Ordinal)
        && context.AuthorizedTools.Any(
            tool => string.Equals(tool.Connector, toolName, StringComparison.Ordinal));

    private static AiDirectiveIterationExecutionResult Failed(
        string correlationId,
        string code,
        string auditReason) =>
        AiDirectiveIterationExecutionResult.Failed(
            correlationId,
            new AiDirectiveIterationExecutionFailure(code, auditReason));
}
