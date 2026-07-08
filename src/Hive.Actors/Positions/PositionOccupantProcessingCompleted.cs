using Hive.Domain.Identity;

namespace Hive.Actors.Positions;

/// <summary>
/// Terminal status of one occupant processing run as seen by the parent
/// <see cref="PositionActor"/>. Deliberately neutral: no AI semantics cross
/// the position/occupant boundary (bible v1.76).
/// </summary>
internal enum PositionOccupantProcessingStatus
{
    Completed = 0,
    Failed = 1,
    Escalated = 2,
}

/// <summary>
/// Neutral completion envelope sent by an occupant child to its parent
/// <see cref="PositionActor"/> when processing of a dispatched message ends.
/// Carries correlation and a terminal status only; interpretation, result
/// messages and effects stay queryable on the occupant (bible v1.76).
/// <see cref="FailureCode"/> is the stable machine-readable terminal code
/// shared with the audit snapshot's TerminalCode, never free audit text.
/// </summary>
internal sealed record PositionOccupantProcessingCompleted
{
    public PositionOccupantProcessingCompleted(
        string correlationId,
        MessageId messageId,
        ThreadId threadId,
        DirectiveId directiveId,
        PositionOccupantProcessingStatus status,
        string? failureCode = null)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException(
                "Occupant processing completion requires a correlation id.",
                nameof(correlationId));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentException(
                $"Unknown occupant processing status '{status}'.",
                nameof(status));
        }

        if (failureCode is not null && string.IsNullOrWhiteSpace(failureCode))
        {
            throw new ArgumentException(
                "Occupant processing failure code must be omitted or non-empty.",
                nameof(failureCode));
        }

        if (status == PositionOccupantProcessingStatus.Completed && failureCode is not null)
        {
            throw new ArgumentException(
                "A completed occupant processing run cannot carry a failure code.",
                nameof(failureCode));
        }

        CorrelationId = correlationId;
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        DirectiveId = directiveId ?? throw new ArgumentNullException(nameof(directiveId));
        Status = status;
        FailureCode = failureCode;
    }

    public string CorrelationId { get; }

    public MessageId MessageId { get; }

    public ThreadId ThreadId { get; }

    public DirectiveId DirectiveId { get; }

    public PositionOccupantProcessingStatus Status { get; }

    public string? FailureCode { get; }
}
