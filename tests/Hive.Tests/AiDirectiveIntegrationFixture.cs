using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Ai;
using Microsoft.Extensions.DependencyInjection;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

internal sealed class AiDirectiveIntegrationFixture : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly ActorSystem _system;
    private readonly AiDirectiveIntegrationScenario _scenario;
    private readonly PositionEntityId _entity;
    private readonly IActorRef _position;

    private AiDirectiveIntegrationFixture(
        ServiceProvider services,
        ActorSystem system,
        AiDirectiveIntegrationScenario scenario,
        PositionEntityId entity,
        IActorRef position)
    {
        _services = services;
        _system = system;
        _scenario = scenario;
        _entity = entity;
        _position = position;
    }

    public static async Task<AiDirectiveIntegrationFixture> StartAsync(
        AiDirectiveIntegrationScenario? scenario = null)
    {
        var resolvedScenario = scenario ?? AiDirectiveIntegrationScenario.Create();
        var stubOptions = resolvedScenario.CreateStubOptions();
        var services = BuildGatewayProvider(stubOptions);
        var system = CreateActorSystem("ai-directive-integration");

        try
        {
            await SeedSnapshotAsync(
                system,
                resolvedScenario.Entity,
                resolvedScenario.InitialSnapshot()).ConfigureAwait(false);

            var gateway = services.GetRequiredService<IAiGateway>();
            var runtimeConfiguration = resolvedScenario.RuntimeConfiguration(
                new AiProviderMetadata(stubOptions.ProviderId, stubOptions.ModelId));
            var position = system.ActorOf(
                Props.Create(() => new PositionActor(
                    resolvedScenario.Entity.Value,
                    new StaticConfigurationProvider(
                        PositionRuntimeConfigurationLoadResult.Loaded(runtimeConfiguration)),
                    new PositionOccupantFactory(new AiAgentGatewayInvoker(gateway)),
                    resolvedScenario.Clock)),
                "position");

            var readyFixture = new AiDirectiveIntegrationFixture(
                services,
                system,
                resolvedScenario,
                resolvedScenario.Entity,
                position);
            await readyFixture.WaitForReadyAsync().ConfigureAwait(false);

            return readyFixture;
        }
        catch
        {
            await system.Terminate().ConfigureAwait(false);
            services.Dispose();
            throw;
        }
    }

    public async Task<AiDirectiveIntegrationRun> ProcessDirectiveAsync()
    {
        _position.Tell(new AcceptMessage(_scenario.Directive));

        var agent = await ResolveAgentAsync().ConfigureAwait(false);
        var audit = await WaitForAuditAsync(agent, _scenario.CorrelationId).ConfigureAwait(false);
        var gateway = await agent.Ask<AiDirectiveGatewayInvocationQueryResult>(
            new GetAiDirectiveGatewayInvocation(_scenario.CorrelationId),
            Timeout()).ConfigureAwait(false);
        if (!gateway.Found)
        {
            throw new TimeoutException("AI directive gateway invocation was not recorded.");
        }

        var state = await WaitForPositionStateAsync(audit).ConfigureAwait(false);

        return new AiDirectiveIntegrationRun(
            _scenario.Directive,
            _position,
            agent,
            audit,
            gateway.Result!,
            state);
    }

    public async Task<IActorRef> ResolveAgentAsync()
    {
        var childName = AgentChildName(_scenario.Occupant);
        var selection = _system.ActorSelection($"{_position.Path}/{childName}");
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                return await selection.ResolveOne(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }
            catch (ActorNotFoundException)
            {
                await Task.Delay(25).ConfigureAwait(false);
            }
        }

        throw new TimeoutException("AiAgentActor child was not created by the PositionActor.");
    }

    public async Task<IReadOnlyList<PositionEvent>> ReadPersistedEventsAsync()
    {
        var probe = _system.ActorOf(
            Props.Create(() => new PositionActorPersistenceProbe(
                PositionActor.PersistenceIdFor(_entity.Value))),
            $"read-events-{Guid.NewGuid():N}");
        var events = await probe.Ask<IReadOnlyList<PositionEvent>>(
            ReadEvents.Instance,
            Timeout()).ConfigureAwait(false);
        await probe.GracefulStop(Timeout()).ConfigureAwait(false);

        return events;
    }

    public async ValueTask DisposeAsync()
    {
        await _system.Terminate().ConfigureAwait(false);
        _services.Dispose();
    }

    private async Task WaitForReadyAsync()
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await _position.Ask<PositionRuntimeStatus>(
                GetPositionRuntimeStatus.Instance,
                TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (status.OperationalState == PositionOperationalState.Ready)
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException("PositionActor did not reach Ready.");
    }

    private static async Task SeedSnapshotAsync(
        ActorSystem system,
        PositionEntityId entity,
        PositionSnapshot snapshot)
    {
        var seeder = system.ActorOf(
            Props.Create(() => new PositionActorPersistenceProbe(
                PositionActor.PersistenceIdFor(entity.Value))),
            $"seed-snapshot-{Guid.NewGuid():N}");
        await seeder.Ask<SnapshotSeeded>(new SeedSnapshot(snapshot), Timeout())
            .ConfigureAwait(false);
        await seeder.GracefulStop(Timeout()).ConfigureAwait(false);
    }

    private async Task<AiDirectiveAuditSnapshot> WaitForAuditAsync(
        IActorRef agent,
        string correlationId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await agent.Ask<AiDirectiveAuditSnapshotQueryResult>(
                new GetAiDirectiveAuditSnapshot(correlationId),
                TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (result.Found)
            {
                return result.Snapshot!;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException("AI directive audit snapshot was not recorded.");
    }

    private async Task<PositionState> WaitForPositionStateAsync(
        AiDirectiveAuditSnapshot audit)
    {
        var memoryKey = _scenario.ResultMemoryKey;
        var waitForMemory = audit.PositionEffects?.CommandTypes.Contains(
            nameof(UpdateShortMemory),
            StringComparer.Ordinal) == true;
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());

        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await _position.Ask<PositionState>(
                GetPositionState.Instance,
                TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (state.ProcessedMessages.Contains(_scenario.Directive.Id)
                && (!waitForMemory || state.ShortMemory.ContainsKey(memoryKey)))
            {
                return state;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException("PositionActor state did not reflect the processed AI directive.");
    }

    private static ActorSystem CreateActorSystem(string namePrefix) =>
        ActorSystem.Create(
            $"{namePrefix}-{Guid.NewGuid():N}",
            ConfigurationFactory.ParseString("""
                akka.persistence.journal.plugin = "akka.persistence.journal.inmem"
                akka.persistence.snapshot-store.plugin = "akka.persistence.snapshot-store.inmem"
                akka.actor {
                  serializers {
                    hive-position-protocol = "Hive.Actors.Serialization.PositionProtocolJsonSerializer, Hive.Actors"
                  }
                  serialization-bindings {
                    "Hive.Domain.Positions.PositionEvent, Hive.Domain" = hive-position-protocol
                    "Hive.Domain.Positions.PositionSnapshot, Hive.Domain" = hive-position-protocol
                  }
                }
                """));

    private static ServiceProvider BuildGatewayProvider(
        StubAiGatewayProviderOptions stubOptions)
    {
        var services = new ServiceCollection();
        services.AddHiveAiGatewayStub(options => CopyStubOptions(stubOptions, options));

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static void CopyStubOptions(
        StubAiGatewayProviderOptions source,
        StubAiGatewayProviderOptions target)
    {
        target.ProviderId = source.ProviderId;
        target.ModelId = source.ModelId;
        target.Outcome = source.Outcome;
        target.Text = source.Text;
        target.FinishReason = source.FinishReason;
        var error = source.Error ?? new StubAiGatewayErrorOptions();
        target.Error = new StubAiGatewayErrorOptions
        {
            Code = error.Code,
            Message = error.Message,
            IsRetryable = error.IsRetryable,
        };
        target.Usage = source.Usage is null
            ? null
            : new StubAiGatewayUsageOptions
            {
                InputTokens = source.Usage.InputTokens,
                OutputTokens = source.Usage.OutputTokens,
                TotalTokens = source.Usage.TotalTokens,
                IsEstimated = source.Usage.IsEstimated,
            };
        target.Cost = source.Cost is null
            ? null
            : new StubAiGatewayCostOptions
            {
                Amount = source.Cost.Amount,
                Currency = source.Cost.Currency,
                IsEstimated = source.Cost.IsEstimated,
            };
        target.ToolCall = source.ToolCall is null
            ? null
            : new StubAiGatewayToolCallOptions
            {
                Id = source.ToolCall.Id,
                Name = source.ToolCall.Name,
                Arguments = source.ToolCall.Arguments is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(
                        source.ToolCall.Arguments,
                        StringComparer.Ordinal),
            };
    }

    private static string AgentChildName(OccupantId occupant)
    {
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{OccupantType.AiAgent}:{occupant.Value}")))[..16];

        return $"occupant-aiagent-{hash.ToLowerInvariant()}";
    }

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class StaticConfigurationProvider(
        PositionRuntimeConfigurationLoadResult result) : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class PositionActorPersistenceProbe : ReceivePersistentActor
    {
        private readonly List<PositionEvent> _events = [];
        private IActorRef? _snapshotReplyTo;

        public PositionActorPersistenceProbe(string persistenceId)
        {
            PersistenceId = persistenceId;

            Recover<PositionEvent>(_events.Add);
            RecoverAny(_ =>
            {
            });
            Command<SeedSnapshot>(command =>
            {
                _snapshotReplyTo = Sender;
                SaveSnapshot(command.Snapshot);
            });
            Command<SaveSnapshotSuccess>(_ =>
            {
                _snapshotReplyTo?.Tell(SnapshotSeeded.Instance);
                _snapshotReplyTo = null;
            });
            Command<SaveSnapshotFailure>(failure =>
            {
                _snapshotReplyTo?.Tell(new Status.Failure(failure.Cause));
                _snapshotReplyTo = null;
            });
            Command<ReadEvents>(_ => Sender.Tell(_events.ToArray()));
        }

        public override string PersistenceId { get; }
    }

    private sealed record SeedSnapshot(PositionSnapshot Snapshot);

    private sealed record SnapshotSeeded
    {
        public static SnapshotSeeded Instance { get; } = new();

        private SnapshotSeeded()
        {
        }
    }

    private sealed record ReadEvents
    {
        public static ReadEvents Instance { get; } = new();

        private ReadEvents()
        {
        }
    }
}

internal sealed class AiDirectiveIntegrationScenario
{
    private static readonly DateTimeOffset DefaultNow =
        new(2026, 7, 4, 20, 0, 0, TimeSpan.Zero);

    private AiDirectiveIntegrationScenario(
        Action<StubAiGatewayProviderOptions>? configureStub,
        IEnumerable<ToolConfiguration> tools,
        IEnumerable<PositionId> directSubordinates,
        IEnumerable<PersistedTask> openTasks,
        IReadOnlyDictionary<string, string> shortMemory,
        IEnumerable<MessageId> recentHistory)
    {
        ConfigureStub = configureStub;
        Tools = tools.ToArray();
        DirectSubordinates = directSubordinates.ToArray();
        OpenTasks = openTasks.ToArray();
        ShortMemory = new Dictionary<string, string>(shortMemory, StringComparer.Ordinal);
        RecentHistory = recentHistory.ToArray();
    }

    public PositionEntityId Entity { get; } =
        PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("triage-agent"));

    public OccupantId Occupant { get; } = OccupantId.From("agent-14a");

    public OrgDirective Directive { get; } = new(
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001401")),
        OrganizationId.From("acme"),
        new PositionEndpointRef(PositionId.From("delivery-lead")),
        new PositionEndpointRef(PositionId.From("triage-agent")),
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001401")),
        Priority.High,
        schemaVersion: 1,
        sentAt: DefaultNow,
        deadline: DefaultNow.AddHours(2),
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001401")),
        parentDirectiveId: null,
        objective: "Triage checkout regression",
        context: "Customer reports checkout failures.");

    public Func<DateTimeOffset> Clock { get; } = () => DefaultNow.AddMinutes(1);

    public IReadOnlyList<ToolConfiguration> Tools { get; }

    public IReadOnlyList<PositionId> DirectSubordinates { get; }

    public IReadOnlyList<PersistedTask> OpenTasks { get; }

    public IReadOnlyDictionary<string, string> ShortMemory { get; }

    public IReadOnlyList<MessageId> RecentHistory { get; }

    public string CorrelationId =>
        $"directive:{Directive.DirectiveId.Value:N}:message:{Directive.Id.Value:N}";

    public string ResultMemoryKey =>
        $"directive:{Directive.DirectiveId.Value:N}:result";

    private Action<StubAiGatewayProviderOptions>? ConfigureStub { get; }

    public static AiDirectiveIntegrationScenario Create(
        Action<StubAiGatewayProviderOptions>? configureStub = null,
        IEnumerable<ToolConfiguration>? tools = null,
        IEnumerable<PositionId>? directSubordinates = null,
        IEnumerable<PersistedTask>? openTasks = null,
        IReadOnlyDictionary<string, string>? shortMemory = null,
        IEnumerable<MessageId>? recentHistory = null)
    {
        var directiveMessage = MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001401"));

        return new AiDirectiveIntegrationScenario(
            configureStub,
            tools ?? [new ToolConfiguration("jira", ["issues/read", "issues/comment"])],
            directSubordinates ?? [PositionId.From("engineer")],
            openTasks ?? [
                new PersistedTask(
                    PositionTaskId.From(Guid.Parse("dddddddd-0000-0000-0000-000000001401")),
                    ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001401")),
                    "Triage checkout regression",
                    Priority.High,
                    DefaultNow,
                    causedBy: directiveMessage),
            ],
            shortMemory ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["last-report"] = "Customer reports checkout failures.",
            },
            recentHistory ?? [
                MessageId.From(Guid.Parse("eeeeeeee-0000-0000-0000-000000001401")),
            ]);
    }

    public StubAiGatewayProviderOptions CreateStubOptions()
    {
        var options = new StubAiGatewayProviderOptions
        {
            ProviderId = "stub",
            ModelId = "integration-fixture",
            Outcome = "success",
            FinishReason = "stop",
            Text = """
                {
                  "schema_version": 1,
                  "intent": "Report",
                  "report": {
                    "kind": "Done",
                    "body": "Fixture report complete."
                  }
                }
                """,
        };

        ConfigureStub?.Invoke(options);

        return options;
    }

    public PositionSnapshot InitialSnapshot() =>
        new(
            DefaultNow,
            Occupant,
            OccupantType.AiAgent,
            openTasks: OpenTasks,
            shortMemory: ShortMemory,
            recentHistory: RecentHistory,
            lastConfigurationStamp: Stamp());

    public PositionRuntimeConfiguration RuntimeConfiguration(
        AiProviderMetadata provider) =>
        new(
            Stamp(),
            Entity.Organization,
            Entity.Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: PositionId.From("delivery-lead"),
                name: "Bug triage",
                timezone: "Europe/Lisbon",
                directSubordinates: DirectSubordinates),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "triage-v1",
                tools: Tools,
                aiGateway: new AiPositionRuntimeConfiguration(
                    provider,
                    new AiModelParameters(maxOutputTokens: 256),
                    timeout: TimeSpan.FromSeconds(15),
                    processingMode: AiProcessingMode.Batch,
                    maxIterations: 2),
                identityPrompt: new IdentityPromptRuntimeConfiguration(
                    "triage-v1",
                    "prompts/triage-v1.md",
                    "You are responsible for triaging incoming bugs.")),
            new PositionAuthorityRuntimeConfiguration(
                canDecide: ["bug.triage"]));

    private static PositionConfigurationStamp Stamp() =>
        new(20, "sha256:t14a");
}

internal sealed record AiDirectiveIntegrationRun(
    OrgDirective Directive,
    IActorRef Position,
    IActorRef Agent,
    AiDirectiveAuditSnapshot Audit,
    AiAgentGatewayInvocationResult GatewayInvocation,
    PositionState PositionState);
