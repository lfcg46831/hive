using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Sharding;
using Akka.Configuration;
using Hive.Actors.Positions;
using Hive.Actors.Serialization;
using Hive.Actors.Sharding;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Configuration;

namespace Hive.Tests;

/// <summary>
/// Verifies US-F0-06-T11c: after safe passivation, the same sharded position remains addressable
/// through Cluster Sharding and reactivates when a message, schedule pulse, or subscription trigger
/// reaches the shard region.
/// </summary>
[Collection(nameof(AkkaClusterCollection))]
public sealed class PositionShardingReactivationTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 27, 16, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, OrgMessage> ReactivationTriggers()
    {
        var data = new TheoryData<string, OrgMessage>
        {
            { "message", MemoTrigger() },
            { "schedule", ScheduleTrigger() },
            { "subscription", SubscriptionTrigger() },
        };

        return data;
    }

    [Theory]
    [MemberData(nameof(ReactivationTriggers))]
    public async Task Passivated_position_reactivates_when_addressed_through_sharding(
        string triggerName,
        OrgMessage trigger)
    {
        var entity = EntityId("acme", $"bug-triage-{triggerName}");
        var stamp = new PositionConfigurationStamp(1, "sha256:v1");
        var publisher = new CapturingProjectionPublisher();
        var system = CreateClusterSystem($"position-reactivation-{triggerName}", GetFreeTcpPort());

        try
        {
            var cluster = Cluster.Get(system);
            cluster.Join(cluster.SelfAddress);
            await WaitForAsync(
                () => cluster.SelfMember.Status == MemberStatus.Up,
                Timeout());

            var region = await StartPositionRegionAsync(
                system,
                LoadedProvider(entity, stamp),
                publisher);

            region.Tell(PositionEnvelope.For(entity, new RequestPassivation("idle")));

            await publisher.WaitForAsync<PositionEventCommitted>(
                committed => committed.Event is PositionPassivated);
            await WaitForEntityInactiveAsync(region, entity);

            region.Tell(PositionEnvelope.For(entity, new AcceptMessage(trigger)));

            var reactivated = await publisher.WaitForAsync<PositionReactivated>(
                candidate => candidate.EntityId == entity,
                skip: 1);
            var committed = await publisher.WaitForAsync<PositionEventCommitted>(
                candidate => candidate.Event is MessageReceived received
                    && received.Message.Id == trigger.Id);
            var received = Assert.IsType<MessageReceived>(committed.Event);

            Assert.Equal(entity, reactivated.EntityId);
            Assert.Equal(stamp, reactivated.LastConfigurationStamp);
            Assert.Equal(trigger.Id, received.Message.Id);
            Assert.Equal(trigger.Thread, received.Message.Thread);
            Assert.Equal(
                1,
                publisher.Events
                    .OfType<PositionEventCommitted>()
                    .Count(candidate => candidate.Event is PositionConfigurationApplied));
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static async Task<IActorRef> StartPositionRegionAsync(
        ActorSystem system,
        IPositionConfigurationProvider provider,
        IPositionProjectionPublisher publisher)
    {
        var sharding = ClusterSharding.Get(system);
        var settings = ClusterShardingSettings.Create(system)
            .WithRole(NodeRoleNames.Agents)
            .WithRememberEntities(false)
            .WithPassivateIdleAfter(TimeSpan.FromMinutes(5));

        return await sharding
            .StartAsync(
                typeName: PositionEntityId.EntityTypeName,
                entityPropsFactory: entityId => Props.Create(() => new PositionActor(
                    entityId,
                    provider,
                    publisher,
                    () => At)),
                settings: settings,
                messageExtractor: new PositionMessageExtractor(8));
    }

    private static async Task WaitForEntityInactiveAsync(
        ICanTell region,
        PositionEntityId entity)
    {
        await WaitForAsync(
            () =>
            {
                var state = region.Ask<CurrentShardRegionState>(
                    GetShardRegionState.Instance,
                    TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

                return state.Shards.All(shard => !shard.EntityIds.Contains(entity.Value));
            },
            Timeout());
    }

    private static ActorSystem CreateClusterSystem(string namePrefix, int port) =>
        ActorSystem.Create(
            $"{namePrefix}-{Guid.NewGuid():N}",
            ConfigurationFactory.ParseString($$"""
                akka.actor.provider = cluster
                akka.remote.dot-netty.tcp.hostname = "127.0.0.1"
                akka.remote.dot-netty.tcp.port = {{port}}
                akka.cluster.roles = ["{{NodeRoleNames.Agents}}"]
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

    private static IPositionConfigurationProvider LoadedProvider(
        PositionEntityId entity,
        PositionConfigurationStamp stamp) =>
        new StaticConfigurationProvider(
            PositionRuntimeConfigurationLoadResult.Loaded(RuntimeConfiguration(entity, stamp)));

    private static PositionRuntimeConfiguration RuntimeConfiguration(
        PositionEntityId entity,
        PositionConfigurationStamp stamp) =>
        new(
            stamp,
            entity.Organization,
            entity.Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: PositionId.From("cto"),
                name: "Bug triage",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "engineer-v1",
                ai: null,
                workingHours: null,
                subscriptions: Array.Empty<SubscriptionConfiguration>(),
                tools: Array.Empty<ToolConfiguration>()),
            new PositionAuthorityRuntimeConfiguration(
                canDecide: Array.Empty<string>()));

    private static Memo MemoTrigger() =>
        new(
            MessageId("aaaaaaaa-0000-0000-0000-000000000501"),
            OrganizationId.From("acme"),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(PositionId.From("bug-triage-message")),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000501"),
            Priority.Normal,
            schemaVersion: 1,
            sentAt: At.AddMinutes(1),
            deadline: null,
            body: "Customer reported a regression.");

    private static Pulse ScheduleTrigger() =>
        new(
            MessageId("aaaaaaaa-0000-0000-0000-000000000502"),
            OrganizationId.From("acme"),
            new SystemEndpointRef(SystemEndpointKind.Scheduler),
            new PositionEndpointRef(PositionId.From("bug-triage-schedule")),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000502"),
            Priority.Normal,
            schemaVersion: 1,
            sentAt: At.AddMinutes(1),
            deadline: null,
            scheduleId: "daily-pulse",
            payload: "Run scheduled pulse.");

    private static EventTrigger SubscriptionTrigger() =>
        new(
            MessageId("aaaaaaaa-0000-0000-0000-000000000503"),
            OrganizationId.From("acme"),
            new SystemEndpointRef(SystemEndpointKind.DomainEvents),
            new PositionEndpointRef(PositionId.From("bug-triage-subscription")),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000503"),
            Priority.Normal,
            schemaVersion: 1,
            sentAt: At.AddMinutes(1),
            deadline: null,
            eventType: "deadline.near",
            payload: "Directive deadline is approaching.");

    private static PositionEntityId EntityId(string organization, string position) =>
        PositionEntityId.From(OrganizationId.From(organization), PositionId.From(position));

    private static MessageId MessageId(string value) =>
        Hive.Domain.Identity.MessageId.From(new Guid(value));

    private static ThreadId ThreadId(string value) =>
        Hive.Domain.Identity.ThreadId.From(new Guid(value));

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Condition was not met within the allotted time.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
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

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(20);

    private sealed class StaticConfigurationProvider(
        PositionRuntimeConfigurationLoadResult result) : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class CapturingProjectionPublisher : IPositionProjectionPublisher
    {
        private readonly object _gate = new();
        private readonly List<PositionProjectionEvent> _events = new();

        public IReadOnlyList<PositionProjectionEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }
        }

        public void Publish(PositionProjectionEvent @event)
        {
            lock (_gate)
            {
                _events.Add(@event);
            }
        }

        public async Task<T> WaitForAsync<T>(Func<T, bool>? predicate = null, int skip = 0)
            where T : PositionProjectionEvent
        {
            var deadline = DateTimeOffset.UtcNow.Add(Timeout());
            while (DateTimeOffset.UtcNow < deadline)
            {
                var match = Events
                    .OfType<T>()
                    .Where(candidate => predicate?.Invoke(candidate) ?? true)
                    .Skip(skip)
                    .FirstOrDefault();
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException($"Projection event {typeof(T).Name} was not published.");
        }
    }
}
