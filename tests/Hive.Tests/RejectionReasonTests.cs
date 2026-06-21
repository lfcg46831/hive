using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class RejectionReasonTests
{
    [Fact]
    public void Reasons_have_stable_values()
    {
        Assert.Equal(1, (int)RejectionReason.InvalidContract);
        Assert.Equal(2, (int)RejectionReason.UnsupportedSchemaVersion);
        Assert.Equal(3, (int)RejectionReason.InvalidRoute);
        Assert.Equal(4, (int)RejectionReason.Unauthorized);
        Assert.Equal(5, (int)RejectionReason.Duplicate);
        Assert.Equal(6, (int)RejectionReason.Expired);
        Assert.Equal(
            [
                RejectionReason.InvalidContract,
                RejectionReason.UnsupportedSchemaVersion,
                RejectionReason.InvalidRoute,
                RejectionReason.Unauthorized,
                RejectionReason.Duplicate,
                RejectionReason.Expired,
            ],
            Enum.GetValues<RejectionReason>());
    }

    [Theory]
    [InlineData(RejectionReason.InvalidContract, "invalid-contract")]
    [InlineData(RejectionReason.UnsupportedSchemaVersion, "unsupported-schema-version")]
    [InlineData(RejectionReason.InvalidRoute, "invalid-route")]
    [InlineData(RejectionReason.Unauthorized, "unauthorized")]
    [InlineData(RejectionReason.Duplicate, "duplicate")]
    [InlineData(RejectionReason.Expired, "expired")]
    public void Wire_values_round_trip_canonically(RejectionReason value, string wireValue)
    {
        Assert.Equal(wireValue, RejectionReasonContract.ToWireValue(value));
        Assert.Equal(value, RejectionReasonContract.ParseWireValue(wireValue));
        Assert.True(RejectionReasonContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid-contract ")]
    [InlineData("InvalidContract")]
    [InlineData("invalidContract")]
    [InlineData("invalid_contract")]
    [InlineData("1")]
    [InlineData("unknown")]
    public void Wire_parsing_rejects_non_canonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => RejectionReasonContract.ParseWireValue(value));
        Assert.False(RejectionReasonContract.TryParseWireValue(value, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Fact]
    public void Wire_parsing_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => RejectionReasonContract.ParseWireValue(null!));
        Assert.False(RejectionReasonContract.TryParseWireValue(null, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Operations_reject_undefined_in_memory_values(int rawValue)
    {
        var value = (RejectionReason)rawValue;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => RejectionReasonContract.RequireDefined(value, "reason"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RejectionReasonContract.ToWireValue(value));
    }
}
