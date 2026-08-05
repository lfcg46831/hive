namespace Hive.Infrastructure.Inbox.ReadModels;

public sealed record InboxProjectionProgress
{
    public InboxProjectionProgress(
        long lastAppliedSequenceId,
        DateTimeOffset? lastEventAppliedAtUtc)
    {
        if (lastAppliedSequenceId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lastAppliedSequenceId),
                lastAppliedSequenceId,
                "Projection sequence cannot be negative.");
        }

        if (lastEventAppliedAtUtc is { } timestamp &&
            (timestamp == default || timestamp.Offset != TimeSpan.Zero))
        {
            throw new ArgumentException(
                "Last applied event timestamp must be specified with the UTC offset.",
                nameof(lastEventAppliedAtUtc));
        }

        if ((lastAppliedSequenceId == 0) != (lastEventAppliedAtUtc is null))
        {
            throw new ArgumentException(
                "An empty projection must not have a watermark and an advanced projection must have one.",
                nameof(lastEventAppliedAtUtc));
        }

        LastAppliedSequenceId = lastAppliedSequenceId;
        LastEventAppliedAtUtc = lastEventAppliedAtUtc;
    }

    public long LastAppliedSequenceId { get; }

    public DateTimeOffset? LastEventAppliedAtUtc { get; }
}

public sealed record InboxProjectionFactItem
{
    public InboxProjectionFactItem(long sequenceId, InboxProjectionFact fact)
    {
        if (sequenceId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceId),
                sequenceId,
                "Projection fact sequence must be positive.");
        }

        SequenceId = sequenceId;
        Fact = fact ?? throw new ArgumentNullException(nameof(fact));
    }

    public long SequenceId { get; }

    public InboxProjectionFact Fact { get; }
}
