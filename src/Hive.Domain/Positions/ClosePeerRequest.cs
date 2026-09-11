using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>Close a retained request without recording a response.</summary>
public sealed record ClosePeerRequest : PositionCommand
{
    public ClosePeerRequest(MessageId requestId, MessageState state)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        if (state is not (MessageState.Rejected or MessageState.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }
        RequestId = requestId;
        State = state;
    }

    public MessageId RequestId { get; }
    public MessageState State { get; }
}
