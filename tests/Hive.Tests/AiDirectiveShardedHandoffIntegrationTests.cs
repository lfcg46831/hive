using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Sharding;
using Akka.Configuration;
using Hive.Actors.Positions;
using Hive.Actors.Serialization;
using Hive.Actors.Sharding;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Auditing;
using Hive.Infrastructure.Configuration;

namespace Hive.Tests;

public sealed class AiDirectiveShardedHandoffIntegrationTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Source = PositionId.From("delivery-lead");
    private static readonly PositionId Superior = PositionId.From("engineering-director");
    private static readonly PositionId Child = PositionId.From("bug-triage");
    private static readonly OccupantId SourceOccupant = OccupantId.From("delivery-lead-agent");

    [Fact]
    public async Task Accepted_results_cross_sharding_and_persist_at_each_destination()
    {
        var report = Directive(
            "10000000-0000-0000-0000-000000000016",
            "20000000-0000-0000-0000-000000000016",
            "30000000-0000-0000-0000-000000000016",
            "report");
        var escalation = Directive(
            "10000000-0000-0000-0000-000000000017",
            "20000000-0000-0000-0000-000000000017",
            "30000000-0000-0000-0000-000000000017",
            "escalate");
        var childDirective = Directive(
            "10000000-0000-0000-0000-000000000018",
            "20000000-0000-0000-0000-000000000018",
            "30000000-0000-0000-0000-000000000018",
            "delegate");
        var invoker = new ResponseByMessageInvoker(new Dictionary<MessageId, string>
        {
            [report.Id] = ReportOutput(),
            [escalation.Id] = EscalationOutput(),
            [childDirective.Id] = ChildDirectiveOutput(),
        });
        var publisher = new RecordingProjectionPublisher();
        using var system = CreateActorSystem();

        try
        {
            var region = await StartShardingAsync(system, invoker, publisher);

            var reportResult = await SendAndWaitAsync(region, publisher, report);
            var escalationResult = await SendAndWaitAsync(region, publisher, escalation);
            var directiveResult = await SendAndWaitAsync(region, publisher, childDirective);

            var emittedReport = Assert.IsType<Report>(reportResult.Result);
            Assert.Equal(report.DirectiveId, emittedReport.AboutDirectiveId);
            Assert.Equal(new PositionEndpointRef(Superior), emittedReport.To);

            var emittedEscalation = Assert.IsType<Escalation>(escalationResult.Result);
            Assert.Equal(new PositionEndpointRef(Superior), emittedEscalation.To);

            var emittedDirective = Assert.IsType<Hive.Domain.Messaging.Directive>(
                directiveResult.Result);
            Assert.Equal(childDirective.DirectiveId, emittedDirective.ParentDirectiveId);
            Assert.Equal(new PositionEndpointRef(Child), emittedDirective.To);

            Assert.Equal(3, invoker.InvocationCount);
            Assert.All(
                new[] { reportResult, escalationResult, directiveResult },
                capture =>
                {
                    Assert.Equal(capture.Result.Id, capture.Received.Message.Id);
                    Assert.Equal(capture.Source.Thread, capture.Received.Message.Thread);
                    Assert.Equal(capture.Result, capture.Received.Message);
                    Assert.Equal(OccupantReplyAuthorKind.AiAgent, capture.Handoff.Author.Kind);
                    Assert.Equal(SourceOccupant.Value, capture.Handoff.Author.SubjectId);
                });
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static async Task<ShardedResultCapture> SendAndWaitAsync(
        IActorRef region,
        RecordingProjectionPublisher publisher,
        Hive.Domain.Messaging.Directive directive)
    {
        var sourceEntity = PositionEntityId.From(Organization, Source);
        region.Tell(PositionEnvelope.For(sourceEntity, new AcceptMessage(directive)));

        var handoffCommitted = await publisher.WaitForAsync<OccupantReplyEmitted>(
            sourceEntity,
            handoff => handoff.SourceMessageId == directive.Id);
        var destination = Assert.IsType<PositionEndpointRef>(handoffCommitted.Event.Message.To);
        var destinationEntity = PositionEntityId.From(Organization, destination.PositionId);
        var received = await publisher.WaitForAsync<MessageReceived>(
            destinationEntity,
            candidate => candidate.Message.Id == handoffCommitted.Event.Message.Id);
        await publisher.WaitForAsync<MessageProcessingCompleted>(
            sourceEntity,
            completed => completed.Message == directive.Id);

        return new ShardedResultCapture(
            directive,
            handoffCommitted.Event,
            handoffCommitted.Event.Message,
            received.Event);
    }

    private static async Task<IActorRef> StartShardingAsync(
        ActorSystem system,
        IAiAgentGatewayInvoker invoker,
        RecordingProjectionPublisher publisher)
    {
        var cluster = Cluster.Get(system);
        cluster.Join(cluster.SelfAddress);
        await WaitForAsync(
            () => cluster.SelfMember.Status == MemberStatus.Up,
            TimeSpan.FromSeconds(20));

        var factory = new PositionOccupantFactory(
            invoker,
            AiDirectiveResultMessageEmissionGate.Instance,
            AllowingAiAgentActionGate.Instance,
            NoopJourneyAuditLog.Instance,
            NoopDirectiveAuditExportStore.Instance);
        var provider = new RuntimeConfigurationProvider();
        var sharding = ClusterSharding.Get(system);
        var settings = ClusterShardingSettings.Create(system)
            .WithRole(NodeRoleNames.Agents)
            .WithRememberEntities(false);

        return await sharding.StartAsync(
            PositionEntityId.EntityTypeName,
            entityId => Props.Create(() => new PositionActor(
                entityId,
                provider,
                factory,
                publisher,
                () => At.AddMinutes(1),
                null,
                null,
                ShardedPositionMessageEmitter.Instance)),
            settings,
            new PositionMessageExtractor(16));
    }

    private static ActorSystem CreateActorSystem()
    {
        var port = GetFreeTcpPort();
        return ActorSystem.Create(
            $"bug-016-sharded-handoff-{Guid.NewGuid():N}",
            ConfigurationFactory.ParseString($$"""
                akka.actor.provider = cluster
                akka.remote.dot-netty.tcp.hostname = "127.0.0.1"
                akka.remote.dot-netty.tcp.port = {{port}}
                akka.cluster.roles = ["agents"]
                akka.cluster.sharding.rebalance-interval = 1s
                akka.cluster.sharding.least-shard-allocation-strategy.rebalance-threshold = 1
                akka.cluster.sharding.least-shard-allocation-strategy.max-simultaneous-rebalance = 16
                akka.persistence.journal.plugin = "akka.persistence.journal.inmem"
                akka.persistence.snapshot-store.plugin = "akka.persistence.snapshot-store.inmem"
                akka.actor {
                  serializers {
                    hive-org-message = "{{typeof(OrgMessageJsonSerializer).AssemblyQualifiedName}}"
                    hive-position-protocol = "{{typeof(PositionProtocolJsonSerializer).AssemblyQualifiedName}}"
                  }
                  serialization-bindings {
                    "Hive.Domain.Messaging.OrgMessage, Hive.Domain" = hive-org-message
                    "Hive.Actors.Sharding.PositionEnvelope, Hive.Actors" = hive-position-protocol
                    "Hive.Domain.Positions.PositionCommand, Hive.Domain" = hive-position-protocol
                    "Hive.Domain.Positions.PositionEvent, Hive.Domain" = hive-position-protocol
                    "Hive.Domain.Positions.PositionSnapshot, Hive.Domain" = hive-position-protocol
                  }
                }
                """));
    }

    private static Hive.Domain.Messaging.Directive Directive(
        string messageId,
        string threadId,
        string directiveId,
        string objective) =>
        new(
            MessageId.From(Guid.Parse(messageId)),
            Organization,
            new PositionEndpointRef(Superior),
            new PositionEndpointRef(Source),
            ThreadId.From(Guid.Parse(threadId)),
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: At.AddHours(1),
            DirectiveId.From(Guid.Parse(directiveId)),
            parentDirectiveId: null,
            objective,
            "BUG-016 sharded result handoff integration fixture.");

    private static string ReportOutput() =>
        """
        {
          "schema_version": 1,
          "intent": "Report",
          "report": {
            "kind": "Done",
            "body": "The delivery report is complete."
          }
        }
        """;

    private static string EscalationOutput() =>
        """
        {
          "schema_version": 1,
          "intent": "Escalation",
          "escalation": {
            "issue": "A superior decision is required",
            "context": "The available authority is insufficient.",
            "options_considered": ["Wait", "Escalate now"]
          }
        }
        """;

    private static string ChildDirectiveOutput() =>
        """
        {
          "schema_version": 1,
          "intent": "Directive",
          "directive": {
            "target_position_id": "bug-triage",
            "objective": "Investigate the confirmed regression.",
            "context": "Preserve the original thread and lineage."
          }
        }
        """;

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The cluster did not reach the expected state.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,
            0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class RuntimeConfigurationProvider : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(PositionRuntimeConfigurationLoadResult.Loaded(
                RuntimeConfiguration(entityId)));

        private static PositionRuntimeConfiguration RuntimeConfiguration(
            PositionEntityId entityId) =>
            new(
                new PositionConfigurationStamp(1, "sha256:bug-016-sharding"),
                entityId.Organization,
                entityId.Position,
                new PositionRuntimeDescriptor(
                    UnitId.From("engineering"),
                    entityId.Position == Source ? Superior : null,
                    entityId.Position.Value,
                    "Europe/Lisbon",
                    directSubordinates: entityId.Position == Source ? [Child] : []),
                entityId.Position == Source
                    ? new OccupantRuntimeConfiguration(
                        OccupantType.AiAgent,
                        identityPromptRef: "delivery-lead-v1",
                        aiGateway: new AiPositionRuntimeConfiguration(
                            new AiProviderMetadata("stub", "bug-016"),
                            new AiModelParameters(maxOutputTokens: 256),
                            timeout: TimeSpan.FromSeconds(10),
                            maxIterations: 2),
                        identityPrompt: new IdentityPromptRuntimeConfiguration(
                            "delivery-lead-v1",
                            "prompts/delivery-lead-v1.md",
                            "You lead delivery and return one canonical result."),
                        configuredIdentity: SourceOccupant)
                    : new OccupantRuntimeConfiguration(OccupantType.Human),
                new PositionAuthorityRuntimeConfiguration(canDecide: ["bug.triage"]));
    }

    private sealed class ResponseByMessageInvoker(
        IReadOnlyDictionary<MessageId, string> responses) : IAiAgentGatewayInvoker
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            var output = responses.TryGetValue(invocation.Request.MessageId, out var response)
                ? response
                : throw new InvalidOperationException("No response was configured for the directive.");
            return Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
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

    private sealed class RecordingProjectionPublisher : IPositionProjectionPublisher
    {
        private readonly object _sync = new();
        private readonly List<PositionProjectionEvent> _events = [];

        public void Publish(PositionProjectionEvent @event)
        {
            lock (_sync)
            {
                _events.Add(@event);
            }
        }

        public async Task<CommittedPositionEvent<TEvent>> WaitForAsync<TEvent>(
            PositionEntityId entity,
            Func<TEvent, bool> predicate)
            where TEvent : PositionEvent
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                lock (_sync)
                {
                    var match = _events
                        .OfType<PositionEventCommitted>()
                        .LastOrDefault(committed =>
                            committed.EntityId == entity
                            && committed.Event is TEvent candidate
                            && predicate(candidate));
                    if (match?.Event is TEvent found)
                    {
                        return new CommittedPositionEvent<TEvent>(entity, found);
                    }
                }

                await Task.Delay(25);
            }

            throw new TimeoutException(
                $"Event '{typeof(TEvent).Name}' was not committed for '{entity.Value}'.");
        }
    }

    private sealed record ShardedResultCapture(
        Hive.Domain.Messaging.Directive Source,
        OccupantReplyEmitted Handoff,
        OrgMessage Result,
        MessageReceived Received);
}
