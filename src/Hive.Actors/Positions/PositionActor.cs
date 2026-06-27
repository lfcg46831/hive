using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using Akka.Persistence;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

/// <summary>
/// Persistent entity for one sharded position (US-F0-06-T06b). Recovery restores the latest
/// snapshot and folds subsequent persisted events into <see cref="PositionState"/> before Akka
/// releases commands from the recovery stash.
/// </summary>
internal sealed class PositionActor :
    ReceivePersistentActor,
    IWithUnboundedStash
{
    internal const string PersistenceIdPrefix = "position:";

    private readonly IPositionConfigurationProvider _configurationProvider;
    private readonly IPositionOccupantFactory _occupantFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<PositionOccupantKey, IActorRef> _occupantActors = new();

    private PositionState _state = PositionState.Empty;
    private PositionOperationalState _operationalState = PositionOperationalState.Recovering;
    private PositionConfigurationBlockReason? _configurationBlockReason;

    public PositionActor(string entityId)
        : this(
            entityId,
            new UnavailableConfigurationProvider(
                "No position configuration provider was supplied to the PositionActor."),
            PositionOccupantFactory.Instance,
            () => DateTimeOffset.UtcNow)
    {
    }

    public PositionActor(string entityId, IPositionConfigurationProvider configurationProvider)
        : this(entityId, configurationProvider, PositionOccupantFactory.Instance, () => DateTimeOffset.UtcNow)
    {
    }

    public PositionActor(
        string entityId,
        IPositionConfigurationProvider configurationProvider,
        Func<DateTimeOffset> clock)
        : this(entityId, configurationProvider, PositionOccupantFactory.Instance, clock)
    {
    }

    public PositionActor(
        string entityId,
        IPositionConfigurationProvider configurationProvider,
        IPositionOccupantFactory occupantFactory,
        Func<DateTimeOffset> clock)
    {
        EntityId = PositionEntityId.Parse(entityId);
        _configurationProvider = configurationProvider
            ?? throw new ArgumentNullException(nameof(configurationProvider));
        _occupantFactory = occupantFactory
            ?? throw new ArgumentNullException(nameof(occupantFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        PersistenceId = PersistenceIdFor(EntityId.Value);

        Recover<SnapshotOffer>(RecoverSnapshot);
        Recover<PositionEvent>(ApplyRecovered);
        Recover<RecoveryCompleted>(_ => BeginConfigurationLoad());

        Command<GetPositionState>(_ => Sender.Tell(_state));
        Command<GetPositionRuntimeStatus>(_ => Sender.Tell(RuntimeStatus()));
        Command<PositionConfigurationLoadCompleted>(HandleConfigurationLoadCompleted);
        Command<PositionConfigurationLoadFailed>(failure => throw new PositionConfigurationGateException(
            PersistenceId,
            failure.Cause));
        Command<AcceptMessage>(command =>
        {
            WhenReady(() =>
            {
                if (_state.ProcessedMessages.Contains(command.Message.Id))
                {
                    return;
                }

                PersistAcceptedMessage(command.Message);
            });
        });
        Command<OpenTask>(command =>
            WhenReady(() =>
                PersistAndApply(new TaskCreated(
                    command.TaskId,
                    command.Thread,
                    command.Title,
                    command.Priority,
                    _clock(),
                    command.Deadline,
                    command.CausedBy))));
        Command<UpdateTask>(command =>
            WhenReady(() =>
                PersistAndApply(new TaskUpdated(
                    command.TaskId,
                    command.Note,
                    _clock(),
                    command.Priority,
                    command.Deadline))));
        Command<CompleteTask>(command =>
            WhenReady(() =>
                PersistAndApply(new TaskCompleted(command.TaskId, _clock(), command.Summary))));
        Command<UpdateShortMemory>(command =>
            WhenReady(() =>
                PersistAndApply(new ShortMemoryUpdated(command.Key, command.Value, _clock()))));
        Command<ChangeOccupant>(command =>
            WhenReady(() =>
                PersistOccupantChange(command)));
        Command<RequestPassivation>(command =>
            WhenReady(() =>
                PersistAndApply(new PositionPassivated(_clock(), command.Reason))));
    }

    public override string PersistenceId { get; }

    public new IStash Stash { get; set; } = null!;

    internal PositionEntityId EntityId { get; }

    internal static string PersistenceIdFor(string entityId)
    {
        var parsed = PositionEntityId.Parse(entityId);
        return $"{PersistenceIdPrefix}{parsed.Value}";
    }

    private void RecoverSnapshot(SnapshotOffer offer)
    {
        if (offer.Snapshot is not PositionSnapshot snapshot)
        {
            throw new InvalidOperationException(
                $"PositionActor snapshot for '{PersistenceId}' must be a {nameof(PositionSnapshot)}.");
        }

        _state = PositionState.Restore(snapshot);
    }

    private void ApplyRecovered(PositionEvent @event) => _state = _state.Apply(@event);

    private void PersistAndApply(PositionEvent @event) =>
        Persist(@event, ApplyPersisted);

    private void PersistAcceptedMessage(OrgMessage message)
    {
        var events = new List<PositionEvent>
        {
            new MessageReceived(message, _clock()),
        };

        if (_state.Occupant is { } occupant && _state.OccupantType is { } occupantType)
        {
            events.Add(new MessageDispatched(
                message.Id,
                message.Thread,
                occupant,
                occupantType,
                _clock()));
        }

        PersistEvents(events);
    }

    private void PersistOccupantChange(ChangeOccupant command)
    {
        var events = new List<PositionEvent>
        {
            new OccupantChanged(command.Occupant, command.Type, _clock()),
        };

        foreach (var message in _state.Inbox)
        {
            events.Add(new MessageDispatched(
                message.Id,
                message.Thread,
                command.Occupant,
                command.Type,
                _clock()));
        }

        PersistEvents(events);
    }

    private void PersistPendingDispatches(Action? afterDispatch = null)
    {
        if (_state.Occupant is not { } occupant || _state.OccupantType is not { } occupantType)
        {
            afterDispatch?.Invoke();
            return;
        }

        var events = _state.Inbox
            .Select(message => (PositionEvent)new MessageDispatched(
                message.Id,
                message.Thread,
                occupant,
                occupantType,
                _clock()))
            .ToArray();

        PersistEvents(events, afterDispatch);
    }

    private void PersistEvents(IReadOnlyList<PositionEvent> events, Action? afterLast = null)
    {
        if (events.Count == 0)
        {
            afterLast?.Invoke();
            return;
        }

        var remaining = events.Count;
        PersistAll(events, persisted =>
        {
            ApplyPersisted(persisted);
            remaining--;
            if (remaining == 0)
            {
                afterLast?.Invoke();
            }
        });
    }

    private void ApplyPersisted(PositionEvent persisted)
    {
        OrgMessage? dispatchedMessage = null;
        if (persisted is MessageDispatched dispatched)
        {
            dispatchedMessage = _state.Inbox.FirstOrDefault(message => message.Id == dispatched.Message);
        }

        _state = _state.Apply(persisted);

        if (persisted is MessageDispatched dispatchEvent && dispatchedMessage is not null)
        {
            ResolveOccupant(dispatchEvent.Occupant, dispatchEvent.OccupantType)
                .Tell(dispatchedMessage);
        }
    }

    private void BeginConfigurationLoad()
    {
        _operationalState = PositionOperationalState.LoadingConfiguration;
        _configurationBlockReason = null;

        var self = Self;
        _ = LoadConfigurationAsync(self);
    }

    private async Task LoadConfigurationAsync(IActorRef self)
    {
        try
        {
            var result = await _configurationProvider
                .LoadAsync(EntityId, CancellationToken.None)
                .ConfigureAwait(false);
            self.Tell(new PositionConfigurationLoadCompleted(result));
        }
        catch (Exception exception)
        {
            self.Tell(new PositionConfigurationLoadFailed(exception));
        }
    }

    private void HandleConfigurationLoadCompleted(PositionConfigurationLoadCompleted completed)
    {
        var compatibility = PositionConfigurationCompatibility.Evaluate(
            _state.LastConfigurationStamp,
            completed.Result,
            EntityId);

        switch (compatibility.Decision)
        {
            case PositionConfigurationCompatibilityDecision.ApplyNewConfiguration:
                Persist(
                    new PositionConfigurationApplied(compatibility.Configuration!.Stamp, _clock()),
                    persisted =>
                    {
                        _state = _state.Apply(persisted);
                        MarkReady();
                    });
                break;

            case PositionConfigurationCompatibilityDecision.AlreadyApplied:
                MarkReady();
                break;

            case PositionConfigurationCompatibilityDecision.Blocked:
                _operationalState = PositionOperationalState.ConfigurationBlocked;
                _configurationBlockReason = compatibility.BlockReason;
                break;

            case PositionConfigurationCompatibilityDecision.TechnicalFailure:
                throw new PositionConfigurationGateException(
                    PersistenceId,
                    compatibility.TechnicalException!);

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(completed),
                    compatibility.Decision,
                    "Unknown position configuration compatibility decision.");
        }
    }

    private void MarkReady()
    {
        _operationalState = PositionOperationalState.Ready;
        _configurationBlockReason = null;
        PersistPendingDispatches(() => Stash.UnstashAll());
    }

    private void WhenReady(Action handler)
    {
        if (_operationalState == PositionOperationalState.Ready)
        {
            handler();
            return;
        }

        if (_operationalState == PositionOperationalState.ConfigurationBlocked)
        {
            return;
        }

        Stash.Stash();
    }

    private PositionRuntimeStatus RuntimeStatus() => new(
        _operationalState,
        _configurationBlockReason,
        _state.LastConfigurationStamp);

    private IActorRef ResolveOccupant(OccupantId occupant, OccupantType occupantType)
    {
        var key = new PositionOccupantKey(occupant, occupantType);
        if (_occupantActors.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var actor = Context.ActorOf(_occupantFactory.Create(occupant, occupantType), ChildName(key));
        _occupantActors.Add(key, actor);
        return actor;
    }

    private static string ChildName(PositionOccupantKey key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{key.OccupantType}:{key.Occupant.Value}")))[..16];

        return $"occupant-{key.OccupantType.ToString().ToLowerInvariant()}-{hash.ToLowerInvariant()}";
    }

    private sealed class UnavailableConfigurationProvider(string reason) : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(PositionRuntimeConfigurationLoadResult.TechnicalFailure(
                new InvalidOperationException(reason)));
    }

    private readonly record struct PositionOccupantKey(
        OccupantId Occupant,
        OccupantType OccupantType);
}

internal sealed record GetPositionState
{
    public static GetPositionState Instance { get; } = new();
}

internal sealed record GetPositionRuntimeStatus
{
    public static GetPositionRuntimeStatus Instance { get; } = new();
}

internal sealed record PositionConfigurationLoadCompleted(
    PositionRuntimeConfigurationLoadResult Result);

internal sealed record PositionConfigurationLoadFailed(Exception Cause);

internal sealed record PositionRuntimeStatus(
    PositionOperationalState OperationalState,
    PositionConfigurationBlockReason? BlockReason,
    PositionConfigurationStamp? LastConfigurationStamp);

internal sealed class PositionConfigurationGateException : Exception
{
    public PositionConfigurationGateException(string persistenceId, Exception innerException)
        : base($"PositionActor '{persistenceId}' could not load a compatible runtime configuration.", innerException)
    {
    }
}
