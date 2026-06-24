using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;

namespace Hive.Domain.Positions;

/// <summary>
/// An accepted message was forwarded to the position's current occupant (US-F0-06-T09) — the fact
/// that the inbox handed work to the <c>AiAgentActor</c>/<c>HumanProxyActor</c> filling the
/// position. It records which <see cref="Message"/> on which <see cref="Thread"/> went to which
/// <see cref="Occupant"/> (and <see cref="OccupantType"/>), so replay can rebuild recent history
/// without re-dispatching. The message stays addressed by id only; its envelope was already persisted
/// by the prior <see cref="MessageReceived"/>.
/// </summary>
public sealed record MessageDispatched : PositionEvent
{
    public MessageDispatched(
        MessageId message,
        ThreadId thread,
        OccupantId occupant,
        OccupantType occupantType,
        DateTimeOffset occurredAt)
        : base(occurredAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(occupant);

        if (!Enum.IsDefined(occupantType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(occupantType),
                occupantType,
                "Occupant type must be AiAgent or Human.");
        }

        Message = message;
        Thread = thread;
        Occupant = occupant;
        OccupantType = occupantType;
    }

    /// <summary>The identity of the message that was dispatched.</summary>
    public MessageId Message { get; }

    /// <summary>The conversation/thread the dispatched message belongs to.</summary>
    public ThreadId Thread { get; }

    /// <summary>The occupant the message was dispatched to.</summary>
    public OccupantId Occupant { get; }

    /// <summary>Whether that occupant is an AI agent or a human.</summary>
    public OccupantType OccupantType { get; }
}
