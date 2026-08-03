using Hive.Infrastructure.Organization.ReadModels;

namespace Hive.Tests;

public sealed class PositionLiveStateReadModelTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Canonical_states_are_declared_in_precedence_order()
    {
        Assert.Equal(
            [
                PositionLiveState.Offline,
                PositionLiveState.Blocked,
                PositionLiveState.WaitingHuman,
                PositionLiveState.Working,
                PositionLiveState.Idle,
            ],
            Enum.GetValues<PositionLiveState>());
    }

    [Fact]
    public void Snapshot_requires_a_non_negative_sequence_and_utc_timestamp()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PositionLiveStateSnapshot(
            "position-a",
            PositionLiveState.Idle,
            sequence: -1,
            At));
        Assert.Throws<ArgumentException>(() => new PositionLiveStateSnapshot(
            "position-a",
            PositionLiveState.Idle,
            sequence: 0,
            At.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentException>(() => new PositionLiveStateSnapshot(
            "position-a",
            PositionLiveState.Idle,
            sequence: 0,
            default));
    }

    [Fact]
    public void Correlated_event_requires_complete_correlation()
    {
        Assert.Throws<ArgumentException>(() => new PositionLiveStateCorrelatedEvent(
            "Escalation",
            Guid.Empty,
            At));
        Assert.Throws<ArgumentException>(() => new PositionLiveStateCorrelatedEvent(
            " ",
            Guid.NewGuid(),
            At));
        Assert.Throws<ArgumentException>(() => new PositionLiveStateCorrelatedEvent(
            "Escalation",
            Guid.NewGuid(),
            At.ToOffset(TimeSpan.FromHours(1))));
    }
}
