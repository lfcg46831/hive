using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class AiDirectiveIterationAuditTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 4, 14, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001030"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001030"));

    [Fact]
    public void RecordDecision_stops_completed_and_rejects_late_entries()
    {
        var context = Context(maxIterations: 3);
        var state = AiDirectiveIterationState.Start(context, At);
        var decision = state.Evaluate(
            Response("Done.", AiFinishReason.Stop),
            At.AddSeconds(1),
            hasAvailableBudget: true);

        var audit = AiDirectiveIterationAuditTrail
            .Start(state)
            .RecordDecision(state, decision, At.AddSeconds(1));

        Assert.True(audit.IsTerminal);
        Assert.Equal(context.CorrelationId, audit.CorrelationId);
        Assert.Equal(AiDirectiveIterationStopKind.Completed, audit.TerminalStopKind);
        Assert.Equal("completed", audit.TerminalCode);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AiDirectiveIterationAuditEventKind.Decision, entry.Kind);
        Assert.Equal(1, entry.Iteration);
        Assert.Equal(At.AddSeconds(1), entry.ObservedAt);
        Assert.Equal("completed", entry.Code);
        Assert.Null(entry.ContinuationKind);
        Assert.Null(entry.ExecutionKind);

        Assert.Throws<InvalidOperationException>(() =>
            audit.RecordDecision(state, decision, At.AddSeconds(2)));
    }

    [Fact]
    public void RecordDecision_distinguishes_timeout_budget_and_max_iterations_without_execution()
    {
        var toolResponse = Response(
            text: null,
            AiFinishReason.ToolCalls,
            [new AiToolCall("call-1", "files")]);
        var timedOut = AiDirectiveIterationState.Start(
            Context(timeout: TimeSpan.FromSeconds(5), maxIterations: 3),
            At);
        var budgeted = AiDirectiveIterationState.Start(Context(maxIterations: 3), At);
        var maxed = AiDirectiveIterationState.Start(Context(maxIterations: 1), At);

        var timeoutAudit = AiDirectiveIterationAuditTrail
            .Start(timedOut)
            .RecordDecision(
                timedOut,
                timedOut.Evaluate(toolResponse, At.AddSeconds(5), hasAvailableBudget: true),
                At.AddSeconds(5));
        var budgetAudit = AiDirectiveIterationAuditTrail
            .Start(budgeted)
            .RecordDecision(
                budgeted,
                budgeted.Evaluate(toolResponse, At.AddSeconds(1), hasAvailableBudget: false),
                At.AddSeconds(1));
        var maxAudit = AiDirectiveIterationAuditTrail
            .Start(maxed)
            .RecordDecision(
                maxed,
                maxed.Evaluate(toolResponse, At.AddSeconds(1), hasAvailableBudget: true),
                At.AddSeconds(1));

        Assert.Equal(AiDirectiveIterationStopKind.Timeout, timeoutAudit.TerminalStopKind);
        Assert.Equal("timeout", timeoutAudit.TerminalCode);
        Assert.Equal(AiDirectiveIterationStopKind.BudgetExceeded, budgetAudit.TerminalStopKind);
        Assert.Equal("budget-exceeded", budgetAudit.TerminalCode);
        Assert.Equal(AiDirectiveIterationStopKind.MaxIterationsReached, maxAudit.TerminalStopKind);
        Assert.Equal("max-iterations-reached", maxAudit.TerminalCode);
        Assert.All(
            [timeoutAudit, budgetAudit, maxAudit],
            audit =>
            {
                Assert.True(audit.IsTerminal);
                Assert.Single(audit.Entries);
                Assert.All(audit.Entries, entry => Assert.Null(entry.ExecutionKind));
            });
    }

    [Fact]
    public void RecordExecution_records_success_and_terminal_failure()
    {
        var context = Context(maxIterations: 3);
        var state = AiDirectiveIterationState.Start(context, At);
        var toolCall = new AiToolCall("call-1", "files");
        var decision = state.Evaluate(
            Response(text: null, AiFinishReason.ToolCalls, [toolCall]),
            At.AddSeconds(1),
            hasAvailableBudget: true);
        var execution = new AiDirectiveConnectorToolExecution(context, iteration: 2, toolCall);
        var success = AiDirectiveIterationExecutionResult.ConnectorToolSucceeded(
            context.CorrelationId,
            AiDirectiveConnectorToolExecutionResult.Succeeded(execution));
        var failure = AiDirectiveIterationExecutionResult.Failed(
            context.CorrelationId,
            new AiDirectiveIterationExecutionFailure(
                "connector-tool-execution-failed",
                "Connector tool failed."));

        var successAudit = AiDirectiveIterationAuditTrail
            .Start(state)
            .RecordDecision(state, decision, At.AddSeconds(1))
            .RecordExecution(state, success, At.AddSeconds(2));
        var failureAudit = AiDirectiveIterationAuditTrail
            .Start(state)
            .RecordDecision(state, decision, At.AddSeconds(1))
            .RecordExecution(state, failure, At.AddSeconds(2));

        Assert.False(successAudit.IsTerminal);
        Assert.Null(successAudit.TerminalCode);
        Assert.Equal(2, successAudit.Entries.Length);
        Assert.Equal(AiDirectiveIterationContinuationKind.ConnectorTool, successAudit.Entries[0].ContinuationKind);
        Assert.Equal(AiDirectiveIterationExecutionKind.ConnectorTool, successAudit.Entries[1].ExecutionKind);
        Assert.Equal("connector-tool-succeeded", successAudit.Entries[1].Code);

        Assert.True(failureAudit.IsTerminal);
        Assert.Equal("connector-tool-execution-failed", failureAudit.TerminalCode);
        Assert.Equal("Connector tool failed.", failureAudit.TerminalAuditReason);
        Assert.Equal(2, failureAudit.Entries.Length);
    }

    [Fact]
    public async Task AiAgentActor_stores_completed_iteration_audit_and_returns_missing_for_unknown_correlation()
    {
        var request = Request();
        var system = ActorSystem.Create($"ai-agent-iteration-audit-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new StaticResponseInvoker(ValidReportOutput()))),
                "agent");

            actor.Tell(request);

            var audit = await WaitForAuditAsync(actor, request.CorrelationId);
            var missing = await actor.Ask<AiDirectiveIterationAuditSnapshotQueryResult>(
                new GetAiDirectiveIterationAuditSnapshot("directive:unknown"),
                Timeout());

            Assert.True(audit.Found);
            Assert.Equal(request.CorrelationId, audit.CorrelationId);
            Assert.True(audit.Snapshot!.IsTerminal);
            Assert.Equal(AiDirectiveIterationStopKind.Completed, audit.Snapshot.TerminalStopKind);
            Assert.Equal("completed", audit.Snapshot.TerminalCode);
            var entry = Assert.Single(audit.Snapshot.Entries);
            Assert.Equal(1, entry.Iteration);
            Assert.Equal(AiDirectiveIterationAuditEventKind.Decision, entry.Kind);
            Assert.False(missing.Found);
            Assert.Equal("directive:unknown", missing.CorrelationId);
            Assert.Null(missing.Snapshot);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static async Task<AiDirectiveIterationAuditSnapshotQueryResult> WaitForAuditAsync(
        IActorRef actor,
        string correlationId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await actor.Ask<AiDirectiveIterationAuditSnapshotQueryResult>(
                new GetAiDirectiveIterationAuditSnapshot(correlationId),
                TimeSpan.FromSeconds(1));
            if (result.Found)
            {
                return result;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("AI directive iteration audit snapshot was not recorded.");
    }

    private static AiDirectiveExecutionContext Context(
        TimeSpan? timeout = null,
        int? maxIterations = null)
    {
        var request = Request(timeout, maxIterations);

        return AiDirectiveExecutionContext.From(request);
    }

    private static AiDirectiveProcessingRequest Request(
        TimeSpan? timeout = null,
        int? maxIterations = null)
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
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001030")),
            parentDirectiveId: null,
            objective: "Triage checkout regression",
            context: "Customer reports checkout failures.");
        var configuration = new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(16, "sha256:t10c"),
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
                tools: [new ToolConfiguration("files", ["bugs/read"])],
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "triage"),
                    new AiModelParameters(maxOutputTokens: 256),
                    timeout: timeout ?? TimeSpan.FromSeconds(15),
                    maxIterations: maxIterations),
                identityPrompt: new IdentityPromptRuntimeConfiguration(
                    "triage-v1",
                    "prompts/triage-v1.md",
                    "You are responsible for triaging incoming bugs.")),
            new PositionAuthorityRuntimeConfiguration());

        return AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            PositionState.Restore(new PositionSnapshot(At)),
            OccupantId.From("agent-7"),
            directive);
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

    private static string ValidReportOutput() =>
        """
        {
          "schema_version": 1,
          "intent": "Report",
          "report": {
            "kind": "Progress",
            "body": "Working."
          }
        }
        """;

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class StaticResponseInvoker(string output) : IAiAgentGatewayInvoker
    {
        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                AiGatewayResponse.Succeeded(
                    invocation.Request.OrganizationId,
                    invocation.Request.PositionId,
                    invocation.Request.ThreadId,
                    invocation.Request.MessageId,
                    output,
                    AiFinishReason.Stop)));
    }
}
