using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

public enum MessageProcessingCompletionStatus
{
    Completed = 0,
    Failed = 1,
    Escalated = 2,
}

/// <summary>
/// A dispatched message reached a terminal occupant-processing state. The event is neutral:
/// it records delivery completion, not AI-specific interpretation details.
/// </summary>
public sealed record MessageProcessingCompleted : PositionEvent
{
    public MessageProcessingCompleted(
        string correlationId,
        MessageId message,
        ThreadId thread,
        MessageProcessingCompletionStatus status,
        DateTimeOffset occurredAt,
        string? failureCode = null)
        : base(occurredAt)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException(
                "Message processing completion requires a correlation id.",
                nameof(correlationId));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown message processing completion status.");
        }

        if (failureCode is not null && string.IsNullOrWhiteSpace(failureCode))
        {
            throw new ArgumentException(
                "Message processing completion failure code must be omitted or non-empty.",
                nameof(failureCode));
        }

        if (status == MessageProcessingCompletionStatus.Completed && failureCode is not null)
        {
            throw new ArgumentException(
                "A completed message processing event cannot carry a failure code.",
                nameof(failureCode));
        }

        CorrelationId = correlationId;
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Thread = thread ?? throw new ArgumentNullException(nameof(thread));
        Status = status;
        FailureCode = failureCode;
    }

    public string CorrelationId { get; }

    public MessageId Message { get; }

    public ThreadId Thread { get; }

    public MessageProcessingCompletionStatus Status { get; }

    public string? FailureCode { get; }
}
