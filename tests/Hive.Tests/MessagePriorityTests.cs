using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class MessagePriorityTests
{
    [Fact]
    public void Levels_have_stable_semantic_ranks()
    {
        Assert.Equal(1, (int)Priority.Low);
        Assert.Equal(2, (int)Priority.Normal);
        Assert.Equal(3, (int)Priority.High);
        Assert.Equal(4, (int)Priority.Critical);
        Assert.Equal(
            [Priority.Low, Priority.Normal, Priority.High, Priority.Critical],
            Enum.GetValues<Priority>());
    }

    [Fact]
    public void Compare_uses_semantic_rank()
    {
        Assert.True(PriorityContract.Compare(Priority.Critical, Priority.High) > 0);
        Assert.True(PriorityContract.Compare(Priority.High, Priority.Normal) > 0);
        Assert.True(PriorityContract.Compare(Priority.Normal, Priority.Low) > 0);
        Assert.Equal(0, PriorityContract.Compare(Priority.Normal, Priority.Normal));
    }

    [Theory]
    [InlineData(Priority.Low, "low")]
    [InlineData(Priority.Normal, "normal")]
    [InlineData(Priority.High, "high")]
    [InlineData(Priority.Critical, "critical")]
    public void Wire_values_round_trip_canonically(Priority priority, string wireValue)
    {
        Assert.Equal(wireValue, PriorityContract.ToWireValue(priority));
        Assert.Equal(priority, PriorityContract.ParseWireValue(wireValue));
        Assert.True(PriorityContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(priority, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("normal ")]
    [InlineData(" Normal")]
    [InlineData("Normal")]
    [InlineData("2")]
    [InlineData("urgent")]
    public void Wire_parsing_rejects_non_canonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => PriorityContract.ParseWireValue(value!));
        Assert.False(PriorityContract.TryParseWireValue(value, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Fact]
    public void Wire_parsing_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => PriorityContract.ParseWireValue(null!));
        Assert.False(PriorityContract.TryParseWireValue(null, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Operations_reject_undefined_in_memory_values(int rawValue)
    {
        var priority = (Priority)rawValue;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PriorityContract.RequireDefined(priority, "priority"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PriorityContract.Compare(priority, Priority.Normal));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PriorityContract.Compare(Priority.Normal, priority));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PriorityContract.ToWireValue(priority));
    }
}
