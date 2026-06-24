using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>
/// An inbound organizational message was admitted into the position inbox — the fact produced by a
/// successful <see cref="AcceptMessage"/>. It carries the validated envelope so replay can rebuild
/// the inbox and the processed-id set; the duplicate decision that gated acceptance (US-F0-06-T07)
/// already happened before this event was persisted.
/// </summary>
public sealed record MessageReceived : PositionEvent
{
    public MessageReceived(OrgMessage message, DateTimeOffset occurredAt)
        : base(occurredAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        Message = message;
    }

    /// <summary>The organizational message admitted into the inbox.</summary>
    public OrgMessage Message { get; }
}
