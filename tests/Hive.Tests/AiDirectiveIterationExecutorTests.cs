using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class AiDirectiveIterationExecutorTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001020"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001020"));

    [Fact]
    public async Task ExecuteAsync_invokes_gateway_for_authorized_inference_and_propagates_cancellation()
    {
        var context = Context(maxIterations: 3);
        var state = AiDirectiveIterationState.Start(context, At);
        var decision = state.EvaluateInference(At.AddSeconds(1), hasAvailableBudget: true);
        var response = Response("Still working.", AiFinishReason.Stop);
        var invoker = new RecordingInvoker(response);
        var toolExecutor = new RecordingToolExecutor();
        var executor = new AiDirectiveIterationExecutor(invoker, toolExecutor);
        using var cancellation = new CancellationTokenSource();

        var result = await executor.ExecuteAsync(
            context,
            state,
            decision,
            hasAvailableBudget: true,
            cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(AiDirectiveIterationExecutionKind.Inference, result.Kind);
        Assert.Same(response, result.InferenceResult!.Response);
        Assert.Null(result.ToolResult);
        Assert.Null(result.Failure);
        Assert.Equal(1, invoker.CallCount);
        Assert.Equal(cancellation.Token, invoker.CancellationToken);
        Assert.Equal(context.CorrelationId, invoker.Invocation!.CorrelationId);
        Assert.Equal(context.OrganizationId, invoker.Invocation.Request.OrganizationId);
        Assert.Equal(context.PositionId, invoker.Invocation.Request.PositionId);
        Assert.Equal("2", invoker.Invocation.Request.Metadata["iteration"]);
        Assert.NotNull(invoker.Invocation.Request.OutputConstraint);
        Assert.Equal(0, toolExecutor.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_unavailable_budget_without_effects()
    {
        var context = Context(maxIterations: 3);
        var state = AiDirectiveIterationState.Start(context, At);
        var decision = state.EvaluateInference(At.AddSeconds(1), hasAvailableBudget: true);
        var invoker = new RecordingInvoker(Response("Should not run.", AiFinishReason.Stop));
        var toolExecutor = new RecordingToolExecutor();
        var executor = new AiDirectiveIterationExecutor(invoker, toolExecutor);

        var result = await executor.ExecuteAsync(
            context,
            state,
            decision,
            hasAvailableBudget: false);

        Assert.True(result.IsFailure);
        Assert.Equal("budget-exceeded", result.Failure!.Code);
        Assert.Equal(0, invoker.CallCount);
        Assert.Equal(0, toolExecutor.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_stop_decision_without_effects()
    {
        var context = Context(maxIterations: 3);
        var state = AiDirectiveIterationState.Start(context, At);
        var decision = state.Evaluate(
            Response("Done.", AiFinishReason.Stop),
            At.AddSeconds(1),
            hasAvailableBudget: true);
        var invoker = new RecordingInvoker(Response("Should not run.", AiFinishReason.Stop));
        var toolExecutor = new RecordingToolExecutor();
        var executor = new AiDirectiveIterationExecutor(invoker, toolExecutor);

        var result = await executor.ExecuteAsync(
            context,
            state,
            decision,
            hasAvailableBudget: true);

        Assert.True(result.IsFailure);
        Assert.Equal("iteration-stop-decision", result.Failure!.Code);
        Assert.Equal(0, invoker.CallCount);
        Assert.Equal(0, toolExecutor.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_calls_authorized_connector_tool_with_preserved_arguments()
    {
        var context = Context(maxIterations: 3);
        var state = AiDirectiveIterationState.Start(context, At);
        var decision = state.Evaluate(
            Response(
                text: null,
                AiFinishReason.ToolCalls,
                [
                    new AiToolCall(
                        "call-1",
                        "files",
                        new Dictionary<string, object?> { ["ticket"] = "HIVE-123" }),
                ]),
            At.AddSeconds(1),
            hasAvailableBudget: true);
        var invoker = new RecordingInvoker(Response("Should not run.", AiFinishReason.Stop));
        var toolExecutor = new RecordingToolExecutor();
        var executor = new AiDirectiveIterationExecutor(invoker, toolExecutor);

        var result = await executor.ExecuteAsync(
            context,
            state,
            decision,
            hasAvailableBudget: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(AiDirectiveIterationExecutionKind.ConnectorTool, result.Kind);
        Assert.NotNull(result.ToolResult);
        Assert.Null(result.InferenceResult);
        Assert.Equal(0, invoker.CallCount);
        Assert.Equal(1, toolExecutor.CallCount);
        Assert.Same(context, toolExecutor.Execution!.Context);
        Assert.Equal(2, toolExecutor.Execution.Iteration);
        Assert.Equal("files", toolExecutor.Execution.ToolCall.Name);
        Assert.Equal("HIVE-123", toolExecutor.Execution.ToolCall.Arguments["ticket"]);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_unauthorized_tool_without_effects()
    {
        var context = Context(maxIterations: 3);
        var state = AiDirectiveIterationState.Start(context, At);
        var decision = AiDirectiveIterationDecision.Continue(
            [
                AiDirectiveIterationContinuation.ConnectorTool(
                    new AiToolCall("call-1", "email")),
            ]);
        var invoker = new RecordingInvoker(Response("Should not run.", AiFinishReason.Stop));
        var toolExecutor = new RecordingToolExecutor();
        var executor = new AiDirectiveIterationExecutor(invoker, toolExecutor);

        var result = await executor.ExecuteAsync(
            context,
            state,
            decision,
            hasAvailableBudget: true);

        Assert.True(result.IsFailure);
        Assert.Equal("tool-call-not-allowed", result.Failure!.Code);
        Assert.Equal(0, invoker.CallCount);
        Assert.Equal(0, toolExecutor.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_connector_tool_when_executor_is_unavailable()
    {
        var context = Context(maxIterations: 3);
        var state = AiDirectiveIterationState.Start(context, At);
        var decision = state.Evaluate(
            Response(
                text: null,
                AiFinishReason.ToolCalls,
                [new AiToolCall("call-1", "files")]),
            At.AddSeconds(1),
            hasAvailableBudget: true);
        var invoker = new RecordingInvoker(Response("Should not run.", AiFinishReason.Stop));
        var executor = new AiDirectiveIterationExecutor(invoker);

        var result = await executor.ExecuteAsync(
            context,
            state,
            decision,
            hasAvailableBudget: true);

        Assert.True(result.IsFailure);
        Assert.Equal("connector-tool-executor-unavailable", result.Failure!.Code);
        Assert.Equal(0, invoker.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_multiple_continuations_without_effects()
    {
        var context = Context(maxIterations: 3);
        var state = AiDirectiveIterationState.Start(context, At);
        var decision = AiDirectiveIterationDecision.Continue(
            [
                AiDirectiveIterationContinuation.Inference(),
                AiDirectiveIterationContinuation.ConnectorTool(new AiToolCall("call-1", "files")),
            ]);
        var invoker = new RecordingInvoker(Response("Should not run.", AiFinishReason.Stop));
        var toolExecutor = new RecordingToolExecutor();
        var executor = new AiDirectiveIterationExecutor(invoker, toolExecutor);

        var result = await executor.ExecuteAsync(
            context,
            state,
            decision,
            hasAvailableBudget: true);

        Assert.True(result.IsFailure);
        Assert.Equal("single-continuation-required", result.Failure!.Code);
        Assert.Equal(0, invoker.CallCount);
        Assert.Equal(0, toolExecutor.CallCount);
    }

    private static AiDirectiveExecutionContext Context(
        int? maxIterations = null,
        IEnumerable<ToolConfiguration>? tools = null)
    {
        var entity = PositionEntityId.From(Organization, Position);
        var directive = new OrgDirective(
            Message,
            Organization,
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(Position),
            Thread,
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: null,
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001020")),
            parentDirectiveId: null,
            objective: "Triage checkout regression",
            context: "Customer reports checkout failures.");
        var configuration = new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(15, "sha256:t10b"),
            Organization,
            Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: PositionId.From("delivery-lead"),
                name: "Bug triage",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "triage-v1",
                tools: tools ?? [new ToolConfiguration("files", ["bugs/read"])],
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "triage"),
                    new AiModelParameters(maxOutputTokens: 256),
                    timeout: TimeSpan.FromSeconds(15),
                    maxIterations: maxIterations),
                identityPrompt: new IdentityPromptRuntimeConfiguration(
                    "triage-v1",
                    "prompts/triage-v1.md",
                    "You are responsible for triaging incoming bugs.")),
            new PositionAuthorityRuntimeConfiguration());

        var request = AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            PositionState.Restore(new PositionSnapshot(At)),
            OccupantId.From("agent-7"),
            directive);

        return AiDirectiveExecutionContext.From(request);
    }

    private static AiGatewayResponse Response(
        string? text,
        AiFinishReason finishReason,
        IEnumerable<AiToolCall>? toolCalls = null) =>
        AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            text,
            finishReason,
            toolCalls: toolCalls);

    private sealed class RecordingInvoker(AiGatewayResponse response) : IAiAgentGatewayInvoker
    {
        public int CallCount { get; private set; }

        public AiAgentGatewayInvocation? Invocation { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Invocation = invocation;
            CancellationToken = cancellationToken;

            return Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                response));
        }
    }

    private sealed class RecordingToolExecutor : IAiDirectiveConnectorToolExecutor
    {
        public int CallCount { get; private set; }

        public AiDirectiveConnectorToolExecution? Execution { get; private set; }

        public ValueTask<AiDirectiveConnectorToolExecutionResult> ExecuteAsync(
            AiDirectiveConnectorToolExecution execution,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Execution = execution;
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(
                AiDirectiveConnectorToolExecutionResult.Succeeded(
                    execution,
                    new Dictionary<string, object?> { ["ok"] = true }));
        }
    }
}
