using Akka.Actor;
using Akka.Persistence;
using Hive.Domain.Identity;
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
    private readonly Func<DateTimeOffset> _clock;

    private PositionState _state = PositionState.Empty;
    private PositionOperationalState _operationalState = PositionOperationalState.Recovering;
    private PositionConfigurationBlockReason? _configurationBlockReason;

    public PositionActor(string entityId)
        : this(
            entityId,
            new UnavailableConfigurationProvider(
                "No position configuration provider was supplied to the PositionActor."),
            () => DateTimeOffset.UtcNow)
    {
    }

    public PositionActor(string entityId, IPositionConfigurationProvider configurationProvider)
        : this(entityId, configurationProvider, () => DateTimeOffset.UtcNow)
    {
    }

    public PositionActor(
        string entityId,
        IPositionConfigurationProvider configurationProvider,
        Func<DateTimeOffset> clock)
    {
        EntityId = PositionEntityId.Parse(entityId);
        _configurationProvider = configurationProvider
            ?? throw new ArgumentNullException(nameof(configurationProvider));
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

                PersistAndApply(new MessageReceived(command.Message, _clock()));
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
                PersistAndApply(new OccupantChanged(command.Occupant, command.Type, _clock()))));
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
        Persist(@event, persisted => _state = _state.Apply(persisted));

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
        Stash.UnstashAll();
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

    private sealed class UnavailableConfigurationProvider(string reason) : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(PositionRuntimeConfigurationLoadResult.TechnicalFailure(
                new InvalidOperationException(reason)));
    }
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
