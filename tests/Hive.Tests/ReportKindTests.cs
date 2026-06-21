using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class ReportKindTests
{
    [Fact]
    public void Kinds_have_stable_values()
    {
        Assert.Equal(1, (int)ReportKind.Progress);
        Assert.Equal(2, (int)ReportKind.Done);
        Assert.Equal(
            [ReportKind.Progress, ReportKind.Done],
            Enum.GetValues<ReportKind>());
    }

    [Theory]
    [InlineData(ReportKind.Progress, "progress")]
    [InlineData(ReportKind.Done, "done")]
    public void Wire_values_round_trip_canonically(ReportKind value, string wireValue)
    {
        Assert.Equal(wireValue, ReportKindContract.ToWireValue(value));
        Assert.Equal(value, ReportKindContract.ParseWireValue(wireValue));
        Assert.True(ReportKindContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("progress ")]
    [InlineData("Progress")]
    [InlineData("1")]
    [InlineData("blocked")]
    public void Wire_parsing_rejects_non_canonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => ReportKindContract.ParseWireValue(value));
        Assert.False(ReportKindContract.TryParseWireValue(value, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Fact]
    public void Wire_parsing_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => ReportKindContract.ParseWireValue(null!));
        Assert.False(ReportKindContract.TryParseWireValue(null, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Operations_reject_undefined_in_memory_values(int rawValue)
    {
        var value = (ReportKind)rawValue;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReportKindContract.RequireDefined(value, "kind"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReportKindContract.ToWireValue(value));
    }
}
