using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>An admitted outgoing request retained independently of occupant delivery history.</summary>
public sealed record PeerRequestRecorded : PositionEvent
{
    public PeerRequestRecorded(PeerRequest request, DateTimeOffset occurredAt) : base(occurredAt)
    {
        _ = new PeerRequestRecord(request, MessageState.Accepted);
        Request = request;
    }

    public PeerRequest Request { get; }
}
