using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class AiDirectiveIterationStateTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001010"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001010"));

    [Fact]
    public void Start_rejects_null_context()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AiDirectiveIterationState.Start(null!, At));
    }

    [Fact]
    public void Start_preserves_limits_deadline_and_authorized_tools()
    {
        var context = Context(
            timeout: TimeSpan.FromSeconds(30),
            maxIterations: 3,
            tools: [new ToolConfiguration("files", ["bugs/read"])]);

        var state = AiDirectiveIterationState.Start(context, At);

        Assert.Equal(context.CorrelationId, state.CorrelationId);
        Assert.Equal(1, state.CurrentIteration);
        Assert.Equal(At, state.StartedAt);
        Assert.Equal(At.AddSeconds(30), state.Deadline);
        Assert.Equal(3, state.MaxIterations);
        Assert.Equal(["files"], state.AuthorizedToolNames.ToArray());
        var entry = Assert.Single(state.History);
        Assert.Equal(1, entry.Iteration);
        Assert.Equal(At, entry.StartedAt);
    }

    [Fact]
    public void Start_preserves_absent_max_iterations_without_inventing_limit()
    {
        var context = Context(
            timeout: TimeSpan.FromSeconds(30),
            maxIterations: null,
            tools: [new ToolConfiguration("files", ["bugs/read"])]);

        var state = AiDirectiveIterationState.Start(context, At);

        Assert.Null(state.MaxIterations);
        Assert.Equal(At.AddSeconds(30), state.Deadline);
    }

    [Fact]
    public void Evaluate_marks_response_without_tool_calls_completed()
    {
        var state = AiDirectiveIterationState.Start(Context(maxIterations: 3), At);
        var response = Response("Done.", AiFinishReason.Stop);

        var decision = state.Evaluate(response, At.AddSeconds(1), hasAvailableBudget: true);

        Assert.True(decision.ShouldStop);
        Assert.False(decision.CanContinue);
        Assert.Equal(AiDirectiveIterationStopKind.Completed, decision.StopReason!.Kind);
        Assert.Equal("completed", decision.StopReason.Code);
        Assert.Empty(decision.Continuations);
    }

    [Fact]
    public void Evaluate_stops_before_continuing_when_max_iterations_timeout_or_budget_blocks()
    {
        var toolResponse = Response(
            text: null,
            AiFinishReason.ToolCalls,
            [new AiToolCall("call-1", "files")]);
        var maxed = AiDirectiveIterationState.Start(Context(maxIterations: 1), At);
        var timedOut = AiDirectiveIterationState.Start(
            Context(timeout: TimeSpan.FromSeconds(5), maxIterations: 3),
            At);
        var budgeted = AiDirectiveIterationState.Start(Context(maxIterations: 3), At);

        Assert.Equal(
            AiDirectiveIterationStopKind.MaxIterationsReached,
            maxed.Evaluate(toolResponse, At.AddSeconds(1), hasAvailableBudget: true)
                .StopReason!.Kind);
        Assert.Equal(
            AiDirectiveIterationStopKind.Timeout,
            timedOut.Evaluate(toolResponse, At.AddSeconds(5), hasAvailableBudget: true)
                .StopReason!.Kind);
        Assert.Equal(
            AiDirectiveIterationStopKind.BudgetExceeded,
            budgeted.Evaluate(toolResponse, At.AddSeconds(1), hasAvailableBudget: false)
                .StopReason!.Kind);
    }

    [Fact]
    public void Evaluate_allows_authorized_tool_call_and_advance_to_next_iteration()
    {
        var state = AiDirectiveIterationState.Start(Context(maxIterations: 3), At);
        var response = Response(
            text: null,
            AiFinishReason.ToolCalls,
            [new AiToolCall("call-1", "files", new Dictionary<string, object?> { ["ticket"] = "HIVE-123" })]);

        var decision = state.Evaluate(response, At.AddSeconds(1), hasAvailableBudget: true);
        var next = state.Advance(decision, At.AddSeconds(1));

        Assert.True(decision.CanContinue);
        var continuation = Assert.Single(decision.Continuations);
        Assert.Equal(AiDirectiveIterationContinuationKind.ConnectorTool, continuation.Kind);
        Assert.Equal("call-1", continuation.ToolCall!.Id);
        Assert.Equal("files", continuation.ToolCall.Name);
        Assert.Equal(2, next.CurrentIteration);
        Assert.Equal([1, 2], next.History.Select(entry => entry.Iteration).ToArray());
        Assert.Equal(1, state.CurrentIteration);
    }

    [Fact]
    public void Evaluate_allows_new_inference_when_limits_and_budget_allow_it()
    {
        var state = AiDirectiveIterationState.Start(Context(maxIterations: 3), At);

        var decision = state.EvaluateInference(At.AddSeconds(1), hasAvailableBudget: true);

        Assert.True(decision.CanContinue);
        var continuation = Assert.Single(decision.Continuations);
        Assert.Equal(AiDirectiveIterationContinuationKind.Inference, continuation.Kind);
        Assert.Null(continuation.ToolCall);
    }

    [Fact]
    public void Evaluate_rejects_duplicate_or_unauthorized_tool_calls()
    {
        var state = AiDirectiveIterationState.Start(Context(maxIterations: 3), At);
        var unauthorized = Response(
            text: null,
            AiFinishReason.ToolCalls,
            [new AiToolCall("call-1", "email")]);
        var duplicate = Response(
            text: null,
            AiFinishReason.ToolCalls,
            [
                new AiToolCall("call-1", "files"),
                new AiToolCall("call-2", "files"),
            ]);

        Assert.Equal(
            AiDirectiveIterationStopKind.ToolCallNotAllowed,
            state.Evaluate(unauthorized, At.AddSeconds(1), hasAvailableBudget: true)
                .StopReason!.Kind);
        Assert.Equal(
            AiDirectiveIterationStopKind.ToolCallNotAllowed,
            state.Evaluate(duplicate, At.AddSeconds(1), hasAvailableBudget: true)
                .StopReason!.Kind);
    }

    private static AiDirectiveExecutionContext Context(
        TimeSpan? timeout = null,
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
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001010")),
            parentDirectiveId: null,
            objective: "Triage checkout regression",
            context: "Customer reports checkout failures.");
        var configuration = new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(14, "sha256:t10a"),
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
                    timeout: timeout,
                    costLimits: new AiCostLimits(maxCallsPerHour: 5),
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
}
