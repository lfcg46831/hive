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
using Hive.Infrastructure.Persistence.PostgreSql;
using Npgsql;

namespace Hive.Tests;

internal sealed class PositionShardingMultiNodeFixture : IAsyncDisposable
{
    private const int NumberOfShards = 8;
    private const string ActivationProbeKey = "t14a-fixture";

    private static readonly DateTimeOffset At = new(2026, 6, 28, 10, 0, 0, TimeSpan.Zero);

    private readonly List<PositionShardingNode> _nodes;
    private readonly string _systemName;
    private readonly string? _persistenceConnectionString;
    private readonly TimeSpan _timeout;

    private PositionShardingMultiNodeFixture(
        string systemName,
        IEnumerable<PositionShardingNode> nodes,
        TimeSpan timeout,
        string? persistenceConnectionString)
    {
        _systemName = systemName;
        _nodes = nodes.ToList();
        _timeout = timeout;
        _persistenceConnectionString = persistenceConnectionString;
    }

    public IReadOnlyList<PositionShardingNode> Nodes => _nodes.ToArray();

    public IReadOnlyList<PositionShardingNode> AgentNodes =>
        _nodes
            .Where(node => node.IsActive && node.HasRole(NodeRoleNames.Agents))
            .ToArray();

    public static async Task<PositionShardingMultiNodeFixture> StartAsync(
        bool startAllAgentRegions = true,
        string? persistenceConnectionString = null)
    {
        var timeout = TimeSpan.FromSeconds(30);
        var systemName = $"hive-t14a-{Guid.NewGuid():N}";
        var ports = Enumerable.Range(0, 3).Select(_ => GetFreeTcpPort()).ToArray();

        if (!string.IsNullOrWhiteSpace(persistenceConnectionString))
        {
            await MigratePersistenceAsync(persistenceConnectionString);
        }

        var nodes = new[]
        {
            CreateNode(systemName, "agents-1", ports[0], [NodeRoleNames.Agents], persistenceConnectionString),
            CreateNode(systemName, "agents-2", ports[1], [NodeRoleNames.Agents], persistenceConnectionString),
            CreateNode(systemName, "api-1", ports[2], [NodeRoleNames.Api], persistenceConnectionString),
        };

        var fixture = new PositionShardingMultiNodeFixture(
            systemName,
            nodes,
            timeout,
            persistenceConnectionString);

        try
        {
            await fixture.JoinClusterAsync();
            await fixture.StartShardingAsync(startAllAgentRegions);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public static IEnumerable<PositionEntityId> GenerateEntitiesCoveringShards(
        OrganizationId organization,
        string positionPrefix,
        int shardCount)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentException.ThrowIfNullOrWhiteSpace(positionPrefix);
        if (shardCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shardCount),
                shardCount,
                "Shard count must be greater than zero.");
        }

        var extractor = new PositionMessageExtractor(shardCount);
        var coveredShards = new HashSet<string>(StringComparer.Ordinal);
        var attempts = 0;
        while (coveredShards.Count < shardCount && attempts < shardCount * 1_000)
        {
            var entity = PositionEntityId.From(
                organization,
                PositionId.From($"{positionPrefix}-{attempts:D4}"));
            var shard = extractor.ShardId(entity.Value, messageHint: null);
            attempts++;

            if (coveredShards.Add(shard))
            {
                yield return entity;
            }
        }

        if (coveredShards.Count < shardCount)
        {
            throw new InvalidOperationException(
                $"Could not generate an entity id for every shard in {attempts} attempts.");
        }
    }

    public async Task ActivateAsync(IReadOnlyCollection<PositionEntityId> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
        {
            return;
        }

        var region = AgentNodes.First().Region
            ?? throw new InvalidOperationException("The agents shard region has not been started.");

        foreach (var entity in entities)
        {
            region.Tell(PositionEnvelope.For(
                entity,
                new UpdateShortMemory(ActivationProbeKey, entity.Value)));
        }

        await WaitForAsync(
            () =>
            {
                var committed = _nodes
                    .SelectMany(node => node.Publisher.Events)
                    .OfType<PositionEventCommitted>()
                    .Where(candidate => candidate.Event is ShortMemoryUpdated updated
                        && updated.Key == ActivationProbeKey)
                    .Select(candidate => candidate.EntityId.Value)
                    .ToHashSet(StringComparer.Ordinal);

                return entities.All(entity => committed.Contains(entity.Value));
            },
            _timeout);
    }

    public async Task SendThroughAllAgentRegionsAsync(PositionEntityId entity, string keyPrefix)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);

        var startedNodes = AgentNodes
            .Where(node => node.Region is not null)
            .ToArray();
        if (startedNodes.Length == 0)
        {
            throw new InvalidOperationException("No agents shard region has been started.");
        }

        foreach (var node in startedNodes)
        {
            node.Region!.Tell(PositionEnvelope.For(
                entity,
                new UpdateShortMemory($"{keyPrefix}-{node.Name}", node.Name)));
        }

        await WaitForCommittedShortMemoryUpdatesAsync(
            [entity],
            key => key.StartsWith(keyPrefix, StringComparison.Ordinal),
            expectedCount: startedNodes.Length);
    }

    public async Task ChangeOccupantAsync(
        PositionEntityId entity,
        OccupantId occupant,
        OccupantType occupantType)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(occupant);

        Send(entity, new ChangeOccupant(occupant, occupantType));
        await WaitForCommittedEventAsync<OccupantChanged>(
            entity,
            changed => changed.Occupant == occupant && changed.Type == occupantType);
    }

    public async Task AcceptMessageAsync(PositionEntityId entity, OrgMessage message)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(message);

        Send(entity, new AcceptMessage(message));
        await WaitForCommittedEventAsync<MessageReceived>(
            entity,
            received => received.Message.Id == message.Id);
    }

    public void SendAcceptMessage(PositionEntityId entity, OrgMessage message)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(message);

        Send(entity, new AcceptMessage(message));
    }

    public async Task PassivateAsync(PositionEntityId entity, string reason)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Send(entity, new RequestPassivation(reason));
        await WaitForCommittedEventAsync<PositionPassivated>(
            entity,
            passivated => passivated.Reason == reason);
    }

    public async Task WaitForEntityInactiveAsync(PositionEntityId entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        await WaitForAsync(
            () =>
            {
                foreach (var node in ActiveAgentNodesWithRegions())
                {
                    var state = node.Region!.Ask<CurrentShardRegionState>(
                        GetShardRegionState.Instance,
                        TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                    if (state.Shards.Any(shard => shard.EntityIds.Contains(entity.Value)))
                    {
                        return false;
                    }
                }

                return true;
            },
            _timeout);
    }

    public async Task RestartAgentNodeAsync(string nodeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);

        var node = AgentNodes.Single(candidate => candidate.Name == nodeName);
        await StopNodeAsync(node);

        var replacement = CreateNode(
            _systemName,
            node.Name,
            GetFreeTcpPort(),
            node.Roles.ToArray(),
            _persistenceConnectionString);
        _nodes.Add(replacement);

        var seed = ActiveClusterAddresses().First();
        Cluster.Get(replacement.System).JoinSeedNodes([seed]);
        await WaitForNodeUpAsync(replacement);
        await StartShardingAsync(replacement);
    }

    public async Task WaitForDuplicateRejectedAsync(PositionEntityId entity, MessageId message)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(message);

        await WaitForProjectionEventAsync<PositionMessageDuplicateRejected>(
            entity,
            rejected => rejected.Message == message);
    }

    public IReadOnlyList<CommittedPositionEvent<TEvent>> CommittedEvents<TEvent>(PositionEntityId entity)
        where TEvent : PositionEvent
    {
        ArgumentNullException.ThrowIfNull(entity);

        return ProjectionEvents<PositionEventCommitted>(entity)
            .Where(committed => committed.Event is TEvent)
            .Select(committed => new CommittedPositionEvent<TEvent>(
                committed.EntityId,
                (TEvent)committed.Event))
            .ToArray();
    }

    public IReadOnlyList<TEvent> ProjectionEvents<TEvent>(PositionEntityId entity)
        where TEvent : PositionProjectionEvent
    {
        ArgumentNullException.ThrowIfNull(entity);

        return _nodes
            .SelectMany(node => node.Publisher.Events)
            .OfType<TEvent>()
            .Where(candidate => candidate.EntityId == entity)
            .ToArray();
    }

    public async Task StartRemainingAgentShardRegionsAsync()
    {
        foreach (var node in AgentNodes.Where(node => node.Region is null))
        {
            await StartShardingAsync(node);
        }
    }

    public async Task<IReadOnlyList<PositionEntityLocation>> WaitForRebalancedLocationsAsync(
        IReadOnlyCollection<PositionEntityId> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
        {
            return await GetEntityLocationsAsync();
        }

        var deadline = DateTimeOffset.UtcNow.Add(_timeout);
        var attempt = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var keyPrefix = $"t14b-rebalance-{attempt:D2}";
            foreach (var entity in entities)
            {
                AgentNodes[0].Region!.Tell(PositionEnvelope.For(
                    entity,
                    new UpdateShortMemory(keyPrefix, entity.Value)));
            }

            var locations = await GetEntityLocationsAsync();
            var committedEntities = CommittedShortMemoryEntities(
                key => key.StartsWith("t14b-rebalance-", StringComparison.Ordinal));
            if (entities.All(entity => committedEntities.Contains(entity.Value))
                && HasEntityOnSecondAgent(locations)
                && EntitiesAreActiveExactlyOnce(entities, locations))
            {
                return locations;
            }

            attempt++;
            await Task.Delay(250);
        }

        throw new TimeoutException("Position entities did not rebalance without duplicate active entities.");
    }

    public async Task<IReadOnlyList<PositionEntityLocation>> WaitForAllEntitiesLocatedAsync(
        IReadOnlyCollection<PositionEntityId> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
        {
            return await GetEntityLocationsAsync();
        }

        var deadline = DateTimeOffset.UtcNow.Add(_timeout);
        IReadOnlyList<PositionEntityLocation> locations = Array.Empty<PositionEntityLocation>();
        while (DateTimeOffset.UtcNow < deadline)
        {
            // Re-send the activation probe so entities dropped by a rebalance
            // handoff (remember-entities is disabled) are recreated on their
            // current shard owner before the location snapshot is taken.
            var region = AgentNodes.First().Region
                ?? throw new InvalidOperationException("The agents shard region has not been started.");
            foreach (var entity in entities)
            {
                region.Tell(PositionEnvelope.For(
                    entity,
                    new UpdateShortMemory(ActivationProbeKey, entity.Value)));
            }

            locations = await GetEntityLocationsAsync();
            var located = locations
                .SelectMany(location => location.EntityIds)
                .ToHashSet(StringComparer.Ordinal);
            if (entities.All(entity => located.Contains(entity.Value)))
            {
                return locations;
            }

            await Task.Delay(250);
        }

        return locations;
    }

    public async Task<IReadOnlyList<PositionEntityLocation>> GetEntityLocationsAsync()
    {
        var locations = new List<PositionEntityLocation>();
        foreach (var node in ActiveAgentNodesWithRegions())
        {
            var state = await node.Region!.Ask<CurrentShardRegionState>(
                GetShardRegionState.Instance,
                _timeout);
            var entityIds = state.Shards
                .SelectMany(shard => shard.EntityIds)
                .OrderBy(entityId => entityId, StringComparer.Ordinal)
                .ToArray();

            locations.Add(new PositionEntityLocation(node.Name, entityIds));
        }

        return locations;
    }

    private async Task JoinClusterAsync()
    {
        var seed = Address.Parse(
            $"akka.tcp://{_nodes[0].System.Name}@127.0.0.1:{_nodes[0].Port}");
        var seeds = new[] { seed };

        foreach (var node in _nodes)
        {
            Cluster.Get(node.System).JoinSeedNodes(seeds);
        }

        await WaitForAsync(
            () => _nodes.All(node =>
            {
                var cluster = Cluster.Get(node.System);
                return cluster.SelfMember.Status == MemberStatus.Up
                    && cluster.State.Members.Count(member => member.Status == MemberStatus.Up) == _nodes.Count;
            }),
            _timeout);
    }

    private async Task StartShardingAsync(bool startAllAgentRegions)
    {
        var nodes = startAllAgentRegions
            ? AgentNodes
            : AgentNodes.Take(1).ToArray();

        foreach (var node in nodes)
        {
            await StartShardingAsync(node);
        }
    }

    private static async Task StartShardingAsync(PositionShardingNode node)
    {
        if (node.Region is not null)
        {
            return;
        }

        var sharding = ClusterSharding.Get(node.System);
        var settings = ClusterShardingSettings.Create(node.System)
            .WithRole(NodeRoleNames.Agents)
            .WithRememberEntities(false)
            .WithPassivateIdleAfter(TimeSpan.FromMinutes(10));

        node.Region = await sharding
            .StartAsync(
                typeName: PositionEntityId.EntityTypeName,
                entityPropsFactory: entityId => Props.Create(() => new PositionActor(
                    entityId,
                    node.ConfigurationProvider,
                    node.Publisher,
                    () => At)),
                settings: settings,
                messageExtractor: new PositionMessageExtractor(NumberOfShards))
            .ConfigureAwait(false);
    }

    private static PositionShardingNode CreateNode(
        string systemName,
        string name,
        int port,
        string[] roles,
        string? persistenceConnectionString)
    {
        var publisher = new CapturingProjectionPublisher();
        var provider = new DynamicConfigurationProvider();
        var system = ActorSystem.Create(
            systemName,
            ConfigurationFactory.ParseString($$"""
                akka.actor.provider = cluster
                akka.remote.dot-netty.tcp.hostname = "127.0.0.1"
                akka.remote.dot-netty.tcp.port = {{port}}
                akka.cluster.roles = [{{RolesHocon(roles)}}]
                akka.cluster.sharding.rebalance-interval = 1s
                akka.cluster.sharding.least-shard-allocation-strategy.rebalance-threshold = 1
                akka.cluster.sharding.least-shard-allocation-strategy.max-simultaneous-rebalance = {{NumberOfShards}}
                {{PersistenceHocon(persistenceConnectionString)}}
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

        return new PositionShardingNode(name, roles, port, system, provider, publisher);
    }

    private static async Task MigratePersistenceAsync(string persistenceConnectionString)
    {
        await using var dataSource = NpgsqlDataSource.Create(persistenceConnectionString);
        await new PostgreSqlPositionPersistenceMigrator(dataSource).MigrateAsync();
    }

    private void Send(PositionEntityId entity, PositionCommand command)
    {
        var region = ActiveAgentNodesWithRegions().FirstOrDefault()?.Region
            ?? throw new InvalidOperationException("No agents shard region has been started.");

        region.Tell(PositionEnvelope.For(entity, command));
    }

    private async Task WaitForCommittedEventAsync<TEvent>(
        PositionEntityId entity,
        Func<TEvent, bool> predicate)
        where TEvent : PositionEvent
    {
        await WaitForAsync(
            () => ProjectionEvents<PositionEventCommitted>(entity)
                .Any(committed => committed.Event is TEvent @event && predicate(@event)),
            _timeout);
    }

    private async Task WaitForProjectionEventAsync<TEvent>(
        PositionEntityId entity,
        Func<TEvent, bool> predicate)
        where TEvent : PositionProjectionEvent
    {
        await WaitForAsync(
            () => ProjectionEvents<TEvent>(entity).Any(predicate),
            _timeout);
    }

    private async Task StopNodeAsync(PositionShardingNode node)
    {
        var cluster = Cluster.Get(node.System);
        var address = cluster.SelfAddress;
        cluster.Leave(address);

        await WaitForAsync(
            () => _nodes
                .Where(candidate => candidate.IsActive && candidate != node)
                .All(candidate => Cluster.Get(candidate.System)
                    .State
                    .Members
                    .All(member => member.Address != address)),
            _timeout);

        await node.System.Terminate().WaitAsync(_timeout);
        node.Region = null;
        node.IsActive = false;
    }

    private async Task WaitForNodeUpAsync(PositionShardingNode node)
    {
        await WaitForAsync(
            () =>
            {
                var address = Cluster.Get(node.System).SelfAddress;
                return Cluster.Get(node.System).SelfMember.Status == MemberStatus.Up
                    && _nodes
                        .Where(candidate => candidate.IsActive)
                        .All(candidate => Cluster.Get(candidate.System)
                            .State
                            .Members
                            .Any(member => member.Address == address
                                && member.Status == MemberStatus.Up));
            },
            _timeout);
    }

    private IReadOnlyList<Address> ActiveClusterAddresses() =>
        _nodes
            .Where(node => node.IsActive)
            .Select(node => Cluster.Get(node.System).SelfAddress)
            .ToArray();

    private IReadOnlyList<PositionShardingNode> ActiveAgentNodesWithRegions() =>
        AgentNodes
            .Where(node => node.Region is not null)
            .ToArray();

    private static string RolesHocon(IEnumerable<string> roles) =>
        string.Join(", ", roles.Select(role => $"\"{role}\""));

    private static string PersistenceHocon(string? persistenceConnectionString)
    {
        if (string.IsNullOrWhiteSpace(persistenceConnectionString))
        {
            return """
                akka.persistence.journal.plugin = "akka.persistence.journal.inmem"
                akka.persistence.snapshot-store.plugin = "akka.persistence.snapshot-store.inmem"
                """;
        }

        var escapedConnectionString = EscapeHoconString(persistenceConnectionString);
        return $$"""
            akka.persistence.journal.plugin = "akka.persistence.journal.sql"
            akka.persistence.snapshot-store.plugin = "akka.persistence.snapshot-store.sql"
            akka.persistence.journal.sql {
              class = "Akka.Persistence.Sql.Journal.SqlWriteJournal, Akka.Persistence.Sql"
              connection-string = "{{escapedConnectionString}}"
              provider-name = "PostgreSQL"
              table-mapping = default
              auto-initialize = true
              warn-on-auto-init-fail = false
              default.schema-name = "{{PositionPersistenceSchema.SchemaName}}"
            }
            akka.persistence.snapshot-store.sql {
              class = "Akka.Persistence.Sql.Snapshot.SqlSnapshotStore, Akka.Persistence.Sql"
              connection-string = "{{escapedConnectionString}}"
              provider-name = "PostgreSQL"
              table-mapping = default
              auto-initialize = true
              warn-on-auto-init-fail = false
              default.schema-name = "{{PositionPersistenceSchema.SchemaName}}"
            }
            """;
    }

    private static string EscapeHoconString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

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

    private async Task WaitForCommittedShortMemoryUpdatesAsync(
        IReadOnlyCollection<PositionEntityId> entities,
        Func<string, bool> keyPredicate,
        int expectedCount)
    {
        var entityValues = entities
            .Select(entity => entity.Value)
            .ToHashSet(StringComparer.Ordinal);

        await WaitForAsync(
            () =>
            {
                var committed = _nodes
                    .SelectMany(node => node.Publisher.Events)
                    .OfType<PositionEventCommitted>()
                    .Where(candidate => entityValues.Contains(candidate.EntityId.Value)
                        && candidate.Event is ShortMemoryUpdated updated
                        && keyPredicate(updated.Key))
                    .Count();

                return committed >= expectedCount;
            },
            _timeout);
    }

    private HashSet<string> CommittedShortMemoryEntities(Func<string, bool> keyPredicate) =>
        _nodes
            .SelectMany(node => node.Publisher.Events)
            .OfType<PositionEventCommitted>()
            .Where(candidate => candidate.Event is ShortMemoryUpdated updated && keyPredicate(updated.Key))
            .Select(candidate => candidate.EntityId.Value)
            .ToHashSet(StringComparer.Ordinal);

    private bool HasEntityOnSecondAgent(IReadOnlyCollection<PositionEntityLocation> locations) =>
        locations.Any(location => location.NodeName == AgentNodes[1].Name && location.EntityIds.Count > 0);

    private static bool EntitiesAreActiveExactlyOnce(
        IReadOnlyCollection<PositionEntityId> entities,
        IReadOnlyCollection<PositionEntityLocation> locations)
    {
        foreach (var entity in entities)
        {
            var owners = locations.Count(location =>
                location.EntityIds.Contains(entity.Value, StringComparer.Ordinal));
            if (owners != 1)
            {
                return false;
            }
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_nodes.Select(node => node.System.Terminate()));
    }

    private sealed class DynamicConfigurationProvider : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(PositionRuntimeConfigurationLoadResult.Loaded(RuntimeConfiguration(entityId)));

        private static PositionRuntimeConfiguration RuntimeConfiguration(PositionEntityId entityId) =>
            new(
                new PositionConfigurationStamp(1, "sha256:t14a-fixture"),
                entityId.Organization,
                entityId.Position,
                new PositionRuntimeDescriptor(
                    UnitId.From("engineering"),
                    reportsTo: PositionId.From("cto"),
                    name: "Fixture position",
                    timezone: "Europe/Lisbon"),
                new OccupantRuntimeConfiguration(
                    OccupantType.AiAgent,
                    identityPromptRef: "engineer-v1",
                    ai: null,
                    workingHours: null,
                    subscriptions: Array.Empty<SubscriptionConfiguration>(),
                    tools: Array.Empty<ToolConfiguration>()),
                new PositionAuthorityRuntimeConfiguration(
                    canDecide: Array.Empty<string>(),
                    mustEscalate: Array.Empty<string>(),
                    requiresHumanApproval: Array.Empty<string>()));
    }

    internal sealed class CapturingProjectionPublisher : IPositionProjectionPublisher
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
    }
}

internal sealed record CommittedPositionEvent<TEvent>(
    PositionEntityId EntityId,
    TEvent Event)
    where TEvent : PositionEvent;

internal sealed class PositionShardingNode
{
    public PositionShardingNode(
        string name,
        IReadOnlyCollection<string> roles,
        int port,
        ActorSystem system,
        IPositionConfigurationProvider configurationProvider,
        PositionShardingMultiNodeFixture.CapturingProjectionPublisher publisher)
    {
        Name = name;
        Roles = roles.ToArray();
        Port = port;
        System = system;
        ConfigurationProvider = configurationProvider;
        Publisher = publisher;
    }

    public string Name { get; }

    public IReadOnlyCollection<string> Roles { get; }

    public int Port { get; }

    public ActorSystem System { get; }

    public IPositionConfigurationProvider ConfigurationProvider { get; }

    public PositionShardingMultiNodeFixture.CapturingProjectionPublisher Publisher { get; }

    public IActorRef? Region { get; internal set; }

    public bool IsActive { get; internal set; } = true;

    public bool HasRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
}

internal sealed record PositionEntityLocation(
    string NodeName,
    IReadOnlyCollection<string> EntityIds);
