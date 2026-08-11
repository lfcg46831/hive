using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.Auditing;

namespace Hive.Tests;

public sealed class AiDirectiveMessageHandoffTests
{
    [Fact]
    public async Task Rejected_handoff_records_closed_failure_and_does_not_complete_processing()
    {
        var scenario = AiDirectiveIntegrationScenario.Create();
        var request = AiDirectiveProcessingRequest.Create(
            scenario.Entity,
            scenario.RuntimeConfiguration(new AiProviderMetadata("stub", "handoff-rejection")),
            PositionState.Restore(scenario.InitialSnapshot()),
            scenario.Occupant,
            scenario.Directive);
        var auditLog = new RecordingAuditLog();
        var adapter = new RejectingHandoffAdapter();
        var completion = new TaskCompletionSource<PositionOccupantProcessingCompleted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var system = ActorSystem.Create($"ai-handoff-rejection-{Guid.NewGuid():N}");

        try
        {
            var parent = system.ActorOf(
                Props.Create(() => new RejectingHandoffParent(
                    request,
                    auditLog,
                    adapter,
                    completion)),
                "parent");

            parent.Tell(Start.Instance);

            var failure = await WaitForHandoffFailureAsync(auditLog);
            Assert.Equal(1, adapter.AttemptCount);
            Assert.Equal("handoff-rejected-by-test", failure.ReasonCode);
            Assert.Equal("rejected", failure.Payload["handoffState"]);
            Assert.Equal("Report", failure.MessageType);
            Assert.False(completion.Task.IsCompleted);

            var snapshot = await parent.Ask<AiDirectiveProcessingSnapshotQueryResult>(
                new ForwardSnapshot(request.CorrelationId),
                TimeSpan.FromSeconds(10));
            Assert.False(snapshot.Found);
            Assert.DoesNotContain(auditLog.Records, record =>
                record.Stage == JourneyAuditStage.ResultMessageCreated
                && record.Outcome == JourneyAuditOutcome.Succeeded);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static async Task<JourneyAuditRecord> WaitForHandoffFailureAsync(
        RecordingAuditLog auditLog)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var failure = auditLog.Records.LastOrDefault(record =>
                record.Stage == JourneyAuditStage.ResultMessageCreated
                && record.Payload.TryGetValue("handoffState", out var state)
                && state == "rejected");
            if (failure is not null)
            {
                return failure;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The rejected handoff was not audited.");
    }

    private sealed class RejectingHandoffParent : ReceiveActor
    {
        private readonly IActorRef _agent;

        public RejectingHandoffParent(
            AiDirectiveProcessingRequest request,
            IJourneyAuditLog auditLog,
            IPositionOccupantMessageHandoffAdapter adapter,
            TaskCompletionSource<PositionOccupantProcessingCompleted> completion)
        {
            _agent = Context.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new StaticInvoker(),
                    AiDirectiveResultMessageEmissionGate.Instance,
                    AllowingAiAgentActionGate.Instance,
                    auditLog,
                    NoopDirectiveAuditExportStore.Instance,
                    PassthroughAiDirectiveOutcomeResolutionIntegrator.Instance,
                    null,
                    adapter)),
                "agent");

            Receive<Start>(_ => _agent.Tell(request));
            Receive<PositionOccupantProcessingCompleted>(completion.TrySetResult);
            Receive<ForwardSnapshot>(query => _agent.Forward(
                new GetAiDirectiveProcessingSnapshot(query.CorrelationId)));
        }
    }

    private sealed class RejectingHandoffAdapter : IPositionOccupantMessageHandoffAdapter
    {
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public ValueTask<OccupantReplyEmissionResult> HandoffAsync(
            IActorRef parent,
            PositionOccupantMessageHandoff handoff)
        {
            Interlocked.Increment(ref _attemptCount);
            return ValueTask.FromResult(OccupantReplyEmissionResult.Rejected(
                handoff.SourceMessageId,
                new OccupantReplyEmissionError(
                    "handoff-rejected-by-test",
                    "message",
                    RejectionReason.InvalidContract)));
        }
    }

    private sealed class StaticInvoker : IAiAgentGatewayInvoker
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
                    """
                    {
                      "schema_version": 1,
                      "intent": "Report",
                      "report": {
                        "kind": "Done",
                        "body": "Handoff rejection fixture completed."
                      }
                    }
                    """,
                    AiFinishReason.Stop)));
    }

    private sealed class RecordingAuditLog : IJourneyAuditLog
    {
        private readonly object _sync = new();
        private readonly List<JourneyAuditRecord> _records = [];

        public IReadOnlyList<JourneyAuditRecord> Records
        {
            get
            {
                lock (_sync)
                {
                    return _records.ToArray();
                }
            }
        }

        public void Append(JourneyAuditRecord record)
        {
            lock (_sync)
            {
                _records.Add(record);
            }
        }

        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null)
        {
            lock (_sync)
            {
                return _records.Where(record =>
                    record.ThreadId == threadId
                    && (directiveId is null || record.DirectiveId == directiveId)).ToArray();
            }
        }
    }

    private sealed record Start
    {
        public static Start Instance { get; } = new();
    }

    private sealed record ForwardSnapshot(string CorrelationId);
}
