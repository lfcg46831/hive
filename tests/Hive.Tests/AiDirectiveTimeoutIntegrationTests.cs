using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Positions;
using Hive.Infrastructure.Ai;
using Hive.Infrastructure.Auditing;
using Microsoft.Extensions.AI;

namespace Hive.Tests;

public sealed class AiDirectiveTimeoutIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Provider_deadline_produces_one_auditable_terminal_for_cooperative_and_non_cooperative_clients(
        bool cooperatesWithCancellation)
    {
        var scenario = AiDirectiveIntegrationScenario.Create();
        var providerMetadata = new AiProviderMetadata("openai", "gpt-timeout-test");
        var request = AiDirectiveProcessingRequest.Create(
            scenario.Entity,
            scenario.RuntimeConfiguration(providerMetadata),
            PositionState.Restore(scenario.InitialSnapshot()),
            scenario.Occupant,
            scenario.Directive);
        var providerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var providerCompletion = new TaskCompletionSource<ChatResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var providerToken = CancellationToken.None;
        var chatClient = new FakeChatClient((_, _, cancellationToken) =>
        {
            providerToken = cancellationToken;
            providerStarted.TrySetResult();
            return cooperatesWithCancellation
                ? WaitForCancellationAsync(cancellationToken)
                : providerCompletion.Task;
        });
        var timeProvider = new TriggerableTimeProvider();
        var auditLog = new RecordingJourneyAuditLog();
        var auditPublisher = new JourneyAuditAiGatewayPublisher(auditLog);
        var realProvider = new RealAiGatewayProvider(
            chatClient,
            new RealAiGatewayProviderSettings(
                "test-key",
                providerMetadata,
                new AiModelParameters(),
                outputCapabilities: new AiOutputProviderCapabilities(
                    [AiOutputConstraintMode.Text])),
            timeProvider);
        var gateway = new AiGateway(
            realProvider,
            auditPublisher,
            timeProvider,
            auditPublisher);
        var invoker = new CapturingGatewayInvoker(new AiAgentGatewayInvoker(gateway));
        var completions = new CompletionCapture();
        var system = ActorSystem.Create($"ai-timeout-terminal-{Guid.NewGuid():N}");

        try
        {
            var parent = system.ActorOf(
                Props.Create(() => new AgentParentProbe(
                    request,
                    invoker,
                    auditLog,
                    completions)),
                "parent");

            parent.Tell(StartProcessing.Instance);
            await providerStarted.Task.WaitAsync(TestTimeout());

            Assert.False(invoker.InvocationToken.CanBeCanceled);
            Assert.False(completions.Task.IsCompleted);

            timeProvider.ExpireDeadline();

            var completion = await completions.Task.WaitAsync(TestTimeout());

            Assert.Equal(1, invoker.InvocationCount);
            Assert.True(providerToken.IsCancellationRequested);
            Assert.Equal(PositionOccupantProcessingStatus.Failed, completion.Status);
            Assert.Equal(request.CorrelationId, completion.CorrelationId);
            Assert.Equal(request.MessageId, completion.MessageId);
            Assert.Equal(request.ThreadId, completion.ThreadId);
            Assert.Equal(request.DirectiveId, completion.DirectiveId);

            var gatewayCalled = Assert.Single(auditLog.Records.Where(
                record => record.Stage == JourneyAuditStage.GatewayCalled));
            Assert.Equal(JourneyAuditOutcome.Failed, gatewayCalled.Outcome);
            Assert.Equal("timeout", gatewayCalled.ReasonCode);
            Assert.Equal("timeout", gatewayCalled.Payload["errorCode"]);
            Assert.Equal(bool.TrueString, gatewayCalled.Payload["isRetryable"]);
            Assert.DoesNotContain("errorMessage", gatewayCalled.Payload.Keys);

            var cost = Assert.Single(auditLog.Records.Where(
                record => record.Stage == JourneyAuditStage.GatewayCostRecorded));
            Assert.Equal(JourneyAuditOutcome.Failed, cost.Outcome);
            Assert.Equal("timeout", cost.ReasonCode);
            Assert.Null(cost.Usage);
            Assert.Null(cost.Cost);
            Assert.Equal("cost-unavailable", cost.Payload["costStatus"]);
            Assert.Equal(bool.TrueString, cost.Payload["isRetryable"]);

            var decision = Assert.Single(auditLog.Records.Where(
                record => record.Stage == JourneyAuditStage.AgentDecided));
            Assert.Equal(JourneyAuditOutcome.Failed, decision.Outcome);
            Assert.Equal(decision.Payload["terminalCode"], completion.FailureCode);
            Assert.All(
                new[] { gatewayCalled, cost, decision },
                record =>
                {
                    Assert.Equal(request.OrganizationId, record.OrganizationId);
                    Assert.Equal(request.PositionId, record.PositionId);
                    Assert.Equal(request.ThreadId, record.ThreadId);
                    Assert.Equal(request.MessageId, record.MessageId);
                    Assert.Equal(request.DirectiveId, record.DirectiveId);
                });
            Assert.DoesNotContain(
                auditLog.Records,
                record => record.Stage == JourneyAuditStage.ResultMessageCreated);

            if (!cooperatesWithCancellation)
            {
                providerCompletion.SetResult(SuccessResponse());
                await Task.Delay(50);

                Assert.Equal(1, completions.Count);
                Assert.Single(auditLog.Records.Where(
                    record => record.Stage == JourneyAuditStage.GatewayCalled));
                Assert.Single(auditLog.Records.Where(
                    record => record.Stage == JourneyAuditStage.GatewayCostRecorded));
                Assert.Single(auditLog.Records.Where(
                    record => record.Stage == JourneyAuditStage.AgentDecided));
            }
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static async Task<ChatResponse> WaitForCancellationAsync(
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The provider cancellation token was not honored.");
    }

    private static ChatResponse SuccessResponse() =>
        new(new ChatMessage(ChatRole.Assistant, "late response"))
        {
            FinishReason = ChatFinishReason.Stop,
        };

    private static TimeSpan TestTimeout() => TimeSpan.FromSeconds(10);

    private sealed class CapturingGatewayInvoker(IAiAgentGatewayInvoker inner)
        : IAiAgentGatewayInvoker
    {
        public int InvocationCount { get; private set; }

        public CancellationToken InvocationToken { get; private set; }

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            InvocationToken = cancellationToken;
            return inner.InvokeAsync(invocation, cancellationToken);
        }
    }

    private sealed class AgentParentProbe : ReceiveActor
    {
        private readonly IActorRef _agent;

        public AgentParentProbe(
            AiDirectiveProcessingRequest request,
            IAiAgentGatewayInvoker invoker,
            IJourneyAuditLog auditLog,
            CompletionCapture completions)
        {
            _agent = Context.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    invoker,
                    AiDirectiveResultMessageEmissionGate.Instance,
                    auditLog)),
                "agent");

            Receive<StartProcessing>(_ => _agent.Tell(request));
            Receive<PositionOccupantProcessingCompleted>(completions.Record);
            Receive<PositionCommand>(_ => { });
        }
    }

    private sealed class CompletionCapture
    {
        private int _count;

        public TaskCompletionSource<PositionOccupantProcessingCompleted> Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PositionOccupantProcessingCompleted> Task => Source.Task;

        public int Count => Volatile.Read(ref _count);

        public void Record(PositionOccupantProcessingCompleted completion)
        {
            Interlocked.Increment(ref _count);
            Source.TrySetResult(completion);
        }
    }

    private sealed class RecordingJourneyAuditLog : IJourneyAuditLog
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
            Hive.Domain.Identity.ThreadId threadId,
            Hive.Domain.Identity.DirectiveId? directiveId = null)
        {
            lock (_sync)
            {
                return _records
                    .Where(record => record.ThreadId == threadId)
                    .Where(record => directiveId is null || record.DirectiveId == directiveId)
                    .ToArray();
            }
        }
    }

    private sealed class FakeChatClient(
        Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> handler)
        : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            handler(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed record StartProcessing
    {
        public static StartProcessing Instance { get; } = new();

        private StartProcessing()
        {
        }
    }

    private sealed class TriggerableTimeProvider : TimeProvider
    {
        private TriggerableTimer? _timer;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new TriggerableTimer(callback, state, dueTime, period);
            if (Interlocked.CompareExchange(ref _timer, timer, null) is not null)
            {
                throw new InvalidOperationException("Only the provider deadline timer is allowed.");
            }

            return timer;
        }

        public void ExpireDeadline()
        {
            var timer = Volatile.Read(ref _timer)
                ?? throw new InvalidOperationException("The provider deadline was not scheduled.");
            timer.Fire();
        }

        private sealed class TriggerableTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private readonly object _sync = new();
            private TimeSpan _dueTime = dueTime;
            private TimeSpan _period = period;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    _dueTime = dueTime;
                    _period = period;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (_sync)
                {
                    _disposed = true;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                lock (_sync)
                {
                    if (_disposed || _dueTime == Timeout.InfiniteTimeSpan)
                    {
                        return;
                    }

                    _dueTime = _period;
                }

                callback(state);
            }
        }
    }
}
