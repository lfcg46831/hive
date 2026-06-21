using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class MessageStateTests
{
    [Fact]
    public void States_have_stable_values()
    {
        Assert.Equal(1, (int)MessageState.Received);
        Assert.Equal(2, (int)MessageState.Accepted);
        Assert.Equal(3, (int)MessageState.Processing);
        Assert.Equal(4, (int)MessageState.Completed);
        Assert.Equal(5, (int)MessageState.Rejected);
        Assert.Equal(6, (int)MessageState.Failed);
        Assert.Equal(
            [
                MessageState.Received,
                MessageState.Accepted,
                MessageState.Processing,
                MessageState.Completed,
                MessageState.Rejected,
                MessageState.Failed,
            ],
            Enum.GetValues<MessageState>());
    }

    [Theory]
    [InlineData(MessageState.Received, "received")]
    [InlineData(MessageState.Accepted, "accepted")]
    [InlineData(MessageState.Processing, "processing")]
    [InlineData(MessageState.Completed, "completed")]
    [InlineData(MessageState.Rejected, "rejected")]
    [InlineData(MessageState.Failed, "failed")]
    public void Wire_values_round_trip_canonically(MessageState value, string wireValue)
    {
        Assert.Equal(wireValue, MessageStateContract.ToWireValue(value));
        Assert.Equal(value, MessageStateContract.ParseWireValue(wireValue));
        Assert.True(MessageStateContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("received ")]
    [InlineData("Received")]
    [InlineData("1")]
    [InlineData("pending")]
    public void Wire_parsing_rejects_non_canonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => MessageStateContract.ParseWireValue(value));
        Assert.False(MessageStateContract.TryParseWireValue(value, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Fact]
    public void Wire_parsing_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => MessageStateContract.ParseWireValue(null!));
        Assert.False(MessageStateContract.TryParseWireValue(null, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Fact]
    public void Transition_matrix_contains_only_canonical_transitions()
    {
        var allowed = new HashSet<(MessageState From, MessageState To)>
        {
            (MessageState.Received, MessageState.Rejected),
            (MessageState.Received, MessageState.Accepted),
            (MessageState.Accepted, MessageState.Processing),
            (MessageState.Processing, MessageState.Completed),
            (MessageState.Processing, MessageState.Failed),
        };

        foreach (var from in Enum.GetValues<MessageState>())
        {
            foreach (var to in Enum.GetValues<MessageState>())
            {
                Assert.Equal(
                    allowed.Contains((from, to)),
                    MessageStateContract.CanTransition(from, to));
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Operations_reject_undefined_in_memory_values(int rawValue)
    {
        var value = (MessageState)rawValue;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MessageStateContract.RequireDefined(value, "state"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MessageStateContract.ToWireValue(value));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MessageStateContract.CanTransition(value, MessageState.Accepted));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MessageStateContract.CanTransition(MessageState.Received, value));
    }
}
