namespace Hive.Infrastructure.Organization.ReadModels;

public sealed record PositionLiveStateSnapshot
{
    public PositionLiveStateSnapshot(
        string positionId,
        PositionLiveState state,
        long sequence,
        DateTimeOffset updatedAtUtc,
        PositionLiveStateCorrelatedEvent? lastCorrelatedEvent = null)
    {
        if (string.IsNullOrWhiteSpace(positionId))
        {
            throw new ArgumentException(
                "Position identifier cannot be empty or whitespace.",
                nameof(positionId));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown live state.");
        }

        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                sequence,
                "Position state sequence cannot be negative.");
        }

        PositionId = positionId;
        State = state;
        Sequence = sequence;
        UpdatedAtUtc = RequireUtc(updatedAtUtc, nameof(updatedAtUtc));
        LastCorrelatedEvent = lastCorrelatedEvent;
    }

    public string PositionId { get; }

    public PositionLiveState State { get; }

    public long Sequence { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public PositionLiveStateCorrelatedEvent? LastCorrelatedEvent { get; }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default)
        {
            throw new ArgumentException("Timestamp must be specified.", parameterName);
        }

        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        }

        return value;
    }
}

public sealed record PositionLiveStateCorrelatedEvent
{
    public PositionLiveStateCorrelatedEvent(
        string type,
        Guid threadId,
        DateTimeOffset occurredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException(
                "Correlated event type cannot be empty or whitespace.",
                nameof(type));
        }

        if (threadId == Guid.Empty)
        {
            throw new ArgumentException(
                "Correlated event thread identifier cannot be empty.",
                nameof(threadId));
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Correlated event timestamp must use the UTC offset.",
                nameof(occurredAtUtc));
        }

        if (occurredAtUtc == default)
        {
            throw new ArgumentException(
                "Correlated event timestamp must be specified.",
                nameof(occurredAtUtc));
        }

        Type = type;
        ThreadId = threadId;
        OccurredAtUtc = occurredAtUtc;
    }

    public string Type { get; }

    public Guid ThreadId { get; }

    public DateTimeOffset OccurredAtUtc { get; }
}
