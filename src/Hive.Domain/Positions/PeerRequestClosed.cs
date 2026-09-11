using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

public sealed record PeerRequestClosed : PositionEvent
{
    public PeerRequestClosed(MessageId requestId, MessageState state, DateTimeOffset occurredAt)
        : base(occurredAt)
    {
        _ = new ClosePeerRequest(requestId, state);
        RequestId = requestId;
        State = state;
    }

    public MessageId RequestId { get; }
    public MessageState State { get; }
}
