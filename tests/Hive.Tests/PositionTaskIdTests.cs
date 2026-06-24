using Hive.Domain.Identity;

namespace Hive.Tests;

/// <summary>
/// Verifies the position-scoped task identity (US-F0-06-T02): Guid-backed value, non-empty
/// invariant, canonical <c>D</c> formatting and value equality.
/// </summary>
public sealed class PositionTaskIdTests
{
    [Fact]
    public void From_keeps_the_underlying_guid()
    {
        var guid = Guid.NewGuid();

        var taskId = PositionTaskId.From(guid);

        Assert.Equal(guid, taskId.Value);
        Assert.Equal(guid.ToString("D"), taskId.ToString());
    }

    [Fact]
    public void New_produces_distinct_non_empty_ids()
    {
        var first = PositionTaskId.New();
        var second = PositionTaskId.New();

        Assert.NotEqual(Guid.Empty, first.Value);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void From_rejects_the_empty_guid()
    {
        Assert.Throws<ArgumentException>(() => PositionTaskId.From(Guid.Empty));
    }

    [Fact]
    public void Equality_is_by_value()
    {
        var guid = Guid.NewGuid();

        Assert.Equal(PositionTaskId.From(guid), PositionTaskId.From(guid));
        Assert.Equal(PositionTaskId.From(guid).GetHashCode(), PositionTaskId.From(guid).GetHashCode());
    }
}
