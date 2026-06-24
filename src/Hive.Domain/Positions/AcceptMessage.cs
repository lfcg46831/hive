using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>
/// Accept an inbound organizational message into the position inbox. Idempotent acceptance keys on
/// the envelope <see cref="OrgMessage.Id"/>/<see cref="OrgMessage.Thread"/> (US-F0-06-T07); this
/// command only carries the validated envelope and leaves the duplicate/admission decision to the
/// entity.
/// </summary>
public sealed record AcceptMessage : PositionCommand
{
    public AcceptMessage(OrgMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Message = message;
    }

    /// <summary>The inbound organizational message to be admitted into the inbox.</summary>
    public OrgMessage Message { get; }
}
