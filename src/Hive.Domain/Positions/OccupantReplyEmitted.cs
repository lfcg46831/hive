using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>
/// Durable authorship and emission fact for one organizational message created from occupant input.
/// Message content remains in the canonical envelope and is redacted from the derived audit log.
/// </summary>
public sealed record OccupantReplyEmitted : PositionEvent
{
    public OccupantReplyEmitted(
        MessageId sourceMessageId,
        OccupantReplyAuthor author,
        OrgMessage message,
        DateTimeOffset occurredAt)
        : base(occurredAt)
    {
        SourceMessageId = sourceMessageId
            ?? throw new ArgumentNullException(nameof(sourceMessageId));
        Author = author ?? throw new ArgumentNullException(nameof(author));
        Message = message ?? throw new ArgumentNullException(nameof(message));

        if (message.Id == SourceMessageId)
        {
            throw new ArgumentException(
                "An occupant reply must use a message identifier different from its source message.",
                nameof(message));
        }

        if (message is not (Report or PeerResponse or Directive or ApprovalDecision))
        {
            throw new ArgumentException(
                "An occupant reply must be a report, peer response, directive, or approval decision.",
                nameof(message));
        }
    }

    public MessageId SourceMessageId { get; }

    public OccupantReplyAuthor Author { get; }

    public OrgMessage Message { get; }
}
