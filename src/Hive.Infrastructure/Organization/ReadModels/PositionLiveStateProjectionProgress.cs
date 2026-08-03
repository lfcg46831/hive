using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.ReadModels;

public sealed record PositionLiveStateProjectionProgress
{
    public PositionLiveStateProjectionProgress(
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

public sealed record PositionLiveStateProjectionItem
{
    public PositionLiveStateProjectionItem(
        long sequenceId,
        PositionLiveStateProjectionFact fact)
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

    public PositionLiveStateProjectionFact Fact { get; }
}

public sealed record PositionLiveStateProjectionUpdate
{
    public PositionLiveStateProjectionUpdate(
        OrganizationId organizationId,
        PositionId positionId,
        PositionLiveState state,
        DateTimeOffset updatedAtUtc,
        PositionLiveStateCorrelatedEvent? correlatedEvent = null)
    {
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        PositionId = positionId ?? throw new ArgumentNullException(nameof(positionId));
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown live state.");
        }

        if (updatedAtUtc == default || updatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Projection update timestamp must be specified with the UTC offset.",
                nameof(updatedAtUtc));
        }

        State = state;
        UpdatedAtUtc = updatedAtUtc;
        CorrelatedEvent = correlatedEvent;
    }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public PositionLiveState State { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public PositionLiveStateCorrelatedEvent? CorrelatedEvent { get; }
}
