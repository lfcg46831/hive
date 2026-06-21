using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class MessageChannelTests
{
    [Fact]
    public void Channels_have_stable_values()
    {
        Assert.Equal(1, (int)MessageChannel.Vertical);
        Assert.Equal(2, (int)MessageChannel.Horizontal);
        Assert.Equal(3, (int)MessageChannel.Governance);
        Assert.Equal(4, (int)MessageChannel.System);
        Assert.Equal(
            [
                MessageChannel.Vertical,
                MessageChannel.Horizontal,
                MessageChannel.Governance,
                MessageChannel.System,
            ],
            Enum.GetValues<MessageChannel>());
    }

    [Theory]
    [InlineData(MessageChannel.Vertical, "vertical")]
    [InlineData(MessageChannel.Horizontal, "horizontal")]
    [InlineData(MessageChannel.Governance, "governance")]
    [InlineData(MessageChannel.System, "system")]
    public void Wire_values_round_trip_canonically(MessageChannel value, string wireValue)
    {
        Assert.Equal(wireValue, MessageChannelContract.ToWireValue(value));
        Assert.Equal(value, MessageChannelContract.ParseWireValue(wireValue));
        Assert.True(MessageChannelContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("vertical ")]
    [InlineData("Vertical")]
    [InlineData("1")]
    [InlineData("external")]
    public void Wire_parsing_rejects_non_canonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => MessageChannelContract.ParseWireValue(value));
        Assert.False(MessageChannelContract.TryParseWireValue(value, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Fact]
    public void Wire_parsing_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => MessageChannelContract.ParseWireValue(null!));
        Assert.False(MessageChannelContract.TryParseWireValue(null, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Operations_reject_undefined_in_memory_values(int rawValue)
    {
        var value = (MessageChannel)rawValue;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MessageChannelContract.RequireDefined(value, "channel"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MessageChannelContract.ToWireValue(value));
    }
}
