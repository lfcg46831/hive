using System.Collections.Immutable;
using Akka.Actor;
using Akka.Pattern;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

/// <summary>
/// Actor-local, outcome-neutral handoff from an occupant child to its owning position. The position
/// durably accepts the canonical message and its local commands before confirming the handoff.
/// </summary>
internal sealed record PositionOccupantMessageHandoff
{
    public PositionOccupantMessageHandoff(
        MessageId sourceMessageId,
        OccupantReplyAuthor author,
        OrgMessage message,
        IEnumerable<PositionCommand>? positionCommands = null)
    {
        SourceMessageId = sourceMessageId
            ?? throw new ArgumentNullException(nameof(sourceMessageId));
        Author = author ?? throw new ArgumentNullException(nameof(author));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PositionCommands = Snapshot(positionCommands);
    }

    public MessageId SourceMessageId { get; }

    public OccupantReplyAuthor Author { get; }

    public OrgMessage Message { get; }

    public ImmutableArray<PositionCommand> PositionCommands { get; }

    private static ImmutableArray<PositionCommand> Snapshot(
        IEnumerable<PositionCommand>? commands)
    {
        if (commands is null)
        {
            return [];
        }

        var snapshot = commands.ToImmutableArray();
        if (snapshot.Any(command => command is null))
        {
            throw new ArgumentException(
                "Position handoff commands cannot contain null entries.",
                nameof(commands));
        }

        return snapshot;
    }
}

internal sealed record PositionOccupantMessageDeliveryCompleted(
    OccupantReplyEmitted Handoff,
    IActorRef ReplyTo,
    AcceptMessageResult Result);

internal sealed record PositionOccupantMessageDeliveryFailed(
    OccupantReplyEmitted Handoff,
    IActorRef ReplyTo,
    Exception Cause);

internal interface IPositionOccupantMessageHandoffAdapter
{
    ValueTask<OccupantReplyEmissionResult> HandoffAsync(
        IActorRef parent,
        PositionOccupantMessageHandoff handoff);
}

internal sealed class ConfirmedParentMessageHandoffAdapter :
    IPositionOccupantMessageHandoffAdapter
{
    public static ConfirmedParentMessageHandoffAdapter Instance { get; } = new();

    public async ValueTask<OccupantReplyEmissionResult> HandoffAsync(
        IActorRef parent,
        PositionOccupantMessageHandoff handoff)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(handoff);
        return await parent
            .Ask<OccupantReplyEmissionResult>(handoff, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Compatibility adapter for actor-isolated characterization tests. Runtime construction always
/// supplies <see cref="ConfirmedParentMessageHandoffAdapter"/> through
/// <see cref="PositionOccupantFactory"/>.
/// </summary>
internal sealed class ImmediatelyAcceptedMessageHandoffAdapter :
    IPositionOccupantMessageHandoffAdapter
{
    public static ImmediatelyAcceptedMessageHandoffAdapter Instance { get; } = new();

    public ValueTask<OccupantReplyEmissionResult> HandoffAsync(
        IActorRef parent,
        PositionOccupantMessageHandoff handoff)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(handoff);
        return ValueTask.FromResult(OccupantReplyEmissionResult.Accepted(
            handoff.SourceMessageId,
            handoff.Message));
    }
}
