using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class AiDirectiveRecoveryGuardTests
{
    [Fact]
    public async Task Terminal_journey_result_suppresses_gateway_and_returns_completion()
    {
        var scenario = AiDirectiveIntegrationScenario.Create();
        var auditLog = new RecordingJourneyAuditLog();
        auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.ResultMessageCreated,
            JourneyAuditOutcome.Succeeded,
            scenario.Entity.Organization,
            scenario.Directive.Thread,
            scenario.Directive.Id,
            scenario.Directive.DirectiveId,
            scenario.Entity.Position,
            messageType: "Report"));
        var invoker = new RecordingInvoker();
        var completion = new TaskCompletionSource<PositionOccupantProcessingCompleted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new TaskCompletionSource<PositionCommand>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var system = ActorSystem.Create($"ai-directive-recovery-{Guid.NewGuid():N}");

        try
        {
            var parent = system.ActorOf(
                Props.Create(() => new AgentParentProbe(invoker, auditLog, completion, command)),
                "parent");
            var request = AiDirectiveProcessingRequest.Create(
                scenario.Entity,
                scenario.RuntimeConfiguration(new AiProviderMetadata("stub", "guard")),
                PositionState.Restore(scenario.InitialSnapshot()),
                scenario.Occupant,
                scenario.Directive);

            parent.Tell(request);

            var completed = await completion.Task.WaitAsync(Timeout());

            Assert.Equal(PositionOccupantProcessingStatus.Completed, completed.Status);
            Assert.Equal(request.CorrelationId, completed.CorrelationId);
            Assert.Equal(request.MessageId, completed.MessageId);
            Assert.Equal(0, invoker.InvocationCount);
            Assert.False(command.Task.IsCompleted);
            var suppression = Assert.Single(
                auditLog.Records.Where(record => record.Stage.ToString() == "DuplicateSuppressed"));
            Assert.Equal(JourneyAuditOutcome.Rejected, suppression.Outcome);
            Assert.Equal("terminal-result-already-materialized", suppression.ReasonCode);
            Assert.Equal(scenario.Entity.Organization, suppression.OrganizationId);
            Assert.Equal(scenario.Directive.Thread, suppression.ThreadId);
            Assert.Equal(scenario.Directive.DirectiveId, suppression.DirectiveId);
            Assert.Equal(scenario.Directive.Id, suppression.MessageId);
            Assert.Equal(scenario.Entity.Position, suppression.PositionId);
            Assert.Equal("ResultMessageCreated", suppression.Payload["suppressedStage"]);
            Assert.DoesNotContain(
                "Recovered report should not be recomputed",
                string.Join(" ", suppression.Payload.Values),
                StringComparison.Ordinal);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Gateway_call_without_terminal_decision_fails_closed_without_reinvoking_gateway()
    {
        var scenario = AiDirectiveIntegrationScenario.Create();
        var auditLog = new RecordingJourneyAuditLog();
        auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.GatewayCalled,
            JourneyAuditOutcome.Succeeded,
            scenario.Entity.Organization,
            scenario.Directive.Thread,
            scenario.Directive.Id,
            scenario.Directive.DirectiveId,
            scenario.Entity.Position,
            provider: new AiProviderMetadata("stub", "guard")));
        var invoker = new RecordingInvoker();
        var completion = new TaskCompletionSource<PositionOccupantProcessingCompleted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new TaskCompletionSource<PositionCommand>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var system = ActorSystem.Create($"ai-directive-recovery-{Guid.NewGuid():N}");

        try
        {
            var parent = system.ActorOf(
                Props.Create(() => new AgentParentProbe(invoker, auditLog, completion, command)),
                "parent");
            var request = AiDirectiveProcessingRequest.Create(
                scenario.Entity,
                scenario.RuntimeConfiguration(new AiProviderMetadata("stub", "guard")),
                PositionState.Restore(scenario.InitialSnapshot()),
                scenario.Occupant,
                scenario.Directive);

            parent.Tell(request);

            var completed = await completion.Task.WaitAsync(Timeout());

            Assert.Equal(PositionOccupantProcessingStatus.Failed, completed.Status);
            Assert.Equal(
                "gateway-call-already-recorded-without-terminal-result",
                completed.FailureCode);
            Assert.Equal(0, invoker.InvocationCount);
            Assert.False(command.Task.IsCompleted);
            var suppression = Assert.Single(
                auditLog.Records.Where(record => record.Stage.ToString() == "DuplicateSuppressed"));
            Assert.Equal(JourneyAuditOutcome.Rejected, suppression.Outcome);
            Assert.Equal("gateway-call-already-materialized", suppression.ReasonCode);
            Assert.Equal(scenario.Entity.Organization, suppression.OrganizationId);
            Assert.Equal(scenario.Directive.Thread, suppression.ThreadId);
            Assert.Equal(scenario.Directive.DirectiveId, suppression.DirectiveId);
            Assert.Equal(scenario.Directive.Id, suppression.MessageId);
            Assert.Equal(scenario.Entity.Position, suppression.PositionId);
            Assert.Equal("GatewayCalled", suppression.Payload["suppressedStage"]);
            Assert.DoesNotContain(
                "provider output",
                string.Join(" ", suppression.Payload.Values),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Terminal_failed_decision_suppresses_gateway_and_reuses_failure_completion()
    {
        var scenario = AiDirectiveIntegrationScenario.Create();
        var auditLog = new RecordingJourneyAuditLog();
        var provider = new AiProviderMetadata("openai", "gpt-timeout-test");
        auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.GatewayCalled,
            JourneyAuditOutcome.Failed,
            scenario.Entity.Organization,
            scenario.Directive.Thread,
            scenario.Directive.Id,
            scenario.Directive.DirectiveId,
            scenario.Entity.Position,
            reasonCode: "timeout",
            provider: provider));
        auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.GatewayCostRecorded,
            JourneyAuditOutcome.Failed,
            scenario.Entity.Organization,
            scenario.Directive.Thread,
            scenario.Directive.Id,
            scenario.Directive.DirectiveId,
            scenario.Entity.Position,
            reasonCode: "timeout",
            provider: provider,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["costStatus"] = "cost-unavailable",
            }));
        auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.AgentDecided,
            JourneyAuditOutcome.Failed,
            scenario.Entity.Organization,
            scenario.Directive.Thread,
            scenario.Directive.Id,
            scenario.Directive.DirectiveId,
            scenario.Entity.Position,
            reasonCode: "ai-gateway-failure",
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["terminalCode"] = "ai-gateway-failure",
            }));
        var invoker = new RecordingInvoker();
        var completion = new TaskCompletionSource<PositionOccupantProcessingCompleted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new TaskCompletionSource<PositionCommand>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var system = ActorSystem.Create($"ai-directive-recovery-{Guid.NewGuid():N}");

        try
        {
            var parent = system.ActorOf(
                Props.Create(() => new AgentParentProbe(invoker, auditLog, completion, command)),
                "parent");
            var request = AiDirectiveProcessingRequest.Create(
                scenario.Entity,
                scenario.RuntimeConfiguration(provider),
                PositionState.Restore(scenario.InitialSnapshot()),
                scenario.Occupant,
                scenario.Directive);

            parent.Tell(request);

            var completed = await completion.Task.WaitAsync(Timeout());

            Assert.Equal(PositionOccupantProcessingStatus.Failed, completed.Status);
            Assert.Equal("ai-gateway-failure", completed.FailureCode);
            Assert.Equal(0, invoker.InvocationCount);
            Assert.False(command.Task.IsCompleted);
            var suppression = Assert.Single(
                auditLog.Records.Where(record => record.Stage == JourneyAuditStage.DuplicateSuppressed));
            Assert.Equal(JourneyAuditOutcome.Rejected, suppression.Outcome);
            Assert.Equal("terminal-agent-decision-already-materialized", suppression.ReasonCode);
            Assert.Equal("AgentDecided", suppression.Payload["suppressedStage"]);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class AgentParentProbe : ReceiveActor
    {
        private readonly IAiAgentGatewayInvoker _invoker;
        private readonly IJourneyAuditLog _auditLog;
        private readonly TaskCompletionSource<PositionOccupantProcessingCompleted> _completion;
        private readonly TaskCompletionSource<PositionCommand> _command;
        private IActorRef? _agent;

        public AgentParentProbe(
            IAiAgentGatewayInvoker invoker,
            IJourneyAuditLog auditLog,
            TaskCompletionSource<PositionOccupantProcessingCompleted> completion,
            TaskCompletionSource<PositionCommand> command)
        {
            _invoker = invoker;
            _auditLog = auditLog;
            _completion = completion;
            _command = command;

            Receive<AiDirectiveProcessingRequest>(request => _agent!.Tell(request, Sender));
            Receive<PositionOccupantProcessingCompleted>(message => _completion.TrySetResult(message));
            Receive<PositionCommand>(message => _command.TrySetResult(message));
        }

        protected override void PreStart()
        {
            _agent = Context.ActorOf(
                Props.Create(() => new AiAgentActor(
                    OccupantId.From("agent-14a"),
                    _invoker,
                    AiDirectiveResultMessageEmissionGate.Instance,
                    _auditLog)),
                "agent");
        }
    }

    private sealed class RecordingInvoker : IAiAgentGatewayInvoker
    {
        public int InvocationCount { get; private set; }

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                AiGatewayResponse.Succeeded(
                    invocation.Request.OrganizationId,
                    invocation.Request.PositionId,
                    invocation.Request.ThreadId,
                    invocation.Request.MessageId,
                    """
                    {
                      "schema_version": 1,
                      "intent": "Report",
                      "report": {
                        "kind": "Done",
                        "body": "Recovered report should not be recomputed."
                      }
                    }
                    """,
                    AiFinishReason.Stop,
                    invocation.Request.Provider)));
        }
    }

    private sealed class RecordingJourneyAuditLog : IJourneyAuditLog
    {
        private readonly List<JourneyAuditRecord> _records = [];

        public IReadOnlyList<JourneyAuditRecord> Records => _records;

        public void Append(JourneyAuditRecord record)
        {
            _records.Add(record);
        }

        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null) =>
            _records
                .Where(record => record.ThreadId == threadId)
                .Where(record => directiveId is null || record.DirectiveId == directiveId)
                .ToArray();
    }
}
