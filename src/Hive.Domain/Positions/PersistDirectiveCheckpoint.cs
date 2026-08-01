using Hive.Domain.Directives;
using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

public enum DirectiveCheckpointPersistenceDecision
{
    Persist = 1,
    AlreadyPersisted = 2,
    Rejected = 3,
}

/// <summary>
/// Requests durable storage of one complete checkpoint revision. The caller supplies the full
/// bounded value so the position can persist the subtask transition atomically.
/// </summary>
public sealed record PersistDirectiveCheckpoint : PositionCommand
{
    public PersistDirectiveCheckpoint(DirectiveCheckpoint checkpoint)
    {
        Checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
    }

    public DirectiveCheckpoint Checkpoint { get; }
}

/// <summary>
/// Acknowledges the durable checkpoint decision to the actor adapter. Persist is reported only
/// from the journal callback, so subsequent effects cannot overtake the accepted event.
/// </summary>
public sealed record DirectiveCheckpointPersistenceResult
{
    public DirectiveCheckpointPersistenceResult(
        DirectiveId directiveId,
        int revision,
        DirectiveCheckpointPersistenceDecision decision)
    {
        DirectiveId = directiveId ?? throw new ArgumentNullException(nameof(directiveId));
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }

        Revision = revision;
        Decision = decision;
    }

    public DirectiveId DirectiveId { get; }

    public int Revision { get; }

    public DirectiveCheckpointPersistenceDecision Decision { get; }
}

/// <summary>
/// One accepted checkpoint revision, including the completed-subtask transitions represented by
/// that revision. Replaying the event replaces only an older revision for the same directive.
/// </summary>
public sealed record DirectiveCheckpointPersisted : PositionEvent
{
    public DirectiveCheckpointPersisted(
        DirectiveCheckpoint checkpoint,
        DateTimeOffset occurredAt)
        : base(occurredAt)
    {
        Checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
    }

    public DirectiveCheckpoint Checkpoint { get; }
}
