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
internal sealed class PositionActor : ReceivePersistentActor
{
    internal const string PersistenceIdPrefix = "position:";

    private readonly Func<DateTimeOffset> _clock;

    private PositionState _state = PositionState.Empty;

    public PositionActor(string entityId)
        : this(entityId, () => DateTimeOffset.UtcNow)
    {
    }

    public PositionActor(string entityId, Func<DateTimeOffset> clock)
    {
        EntityId = PositionEntityId.Parse(entityId);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        PersistenceId = PersistenceIdFor(EntityId.Value);

        Recover<SnapshotOffer>(RecoverSnapshot);
        Recover<PositionEvent>(ApplyRecovered);

        Command<GetPositionState>(_ => Sender.Tell(_state));
        Command<AcceptMessage>(command =>
            PersistAndApply(new MessageReceived(command.Message, _clock())));
        Command<OpenTask>(command =>
            PersistAndApply(new TaskCreated(
                command.TaskId,
                command.Thread,
                command.Title,
                command.Priority,
                _clock(),
                command.Deadline,
                command.CausedBy)));
        Command<UpdateTask>(command =>
            PersistAndApply(new TaskUpdated(
                command.TaskId,
                command.Note,
                _clock(),
                command.Priority,
                command.Deadline)));
        Command<CompleteTask>(command =>
            PersistAndApply(new TaskCompleted(command.TaskId, _clock(), command.Summary)));
        Command<UpdateShortMemory>(command =>
            PersistAndApply(new ShortMemoryUpdated(command.Key, command.Value, _clock())));
        Command<ChangeOccupant>(command =>
            PersistAndApply(new OccupantChanged(command.Occupant, command.Type, _clock())));
        Command<RequestPassivation>(command =>
            PersistAndApply(new PositionPassivated(_clock(), command.Reason)));
    }

    public override string PersistenceId { get; }

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
}

internal sealed record GetPositionState
{
    public static GetPositionState Instance { get; } = new();
}
