using Hive.Domain.Directives;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry.PostgreSql;

namespace Hive.Tests;

public sealed class DirectiveExecutionPolicyTests
{
    [Theory]
    [InlineData(DirectiveExecutionMode.SingleShot, "single-shot")]
    [InlineData(DirectiveExecutionMode.Checkpointable, "checkpointable")]
    public void Mode_wire_contract_is_closed(
        DirectiveExecutionMode mode,
        string wireValue)
    {
        Assert.Equal(wireValue, DirectiveExecutionModeContract.ToWireValue(mode));
        Assert.True(DirectiveExecutionModeContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(mode, parsed);
        Assert.False(DirectiveExecutionModeContract.TryParseWireValue("relaxed", out _));
    }

    [Fact]
    public void Absence_and_explicit_single_shot_remain_single_shot()
    {
        var capability = CheckpointableCapability();

        var absent = DirectiveExecutionPolicyComposer.ComposeV1(
            request: null,
            capability,
            TimeSpan.FromSeconds(90));
        var explicitSingleShot = DirectiveExecutionPolicyComposer.ComposeV1(
            new DirectiveExecutionPolicyRequest(1, DirectiveExecutionMode.SingleShot),
            capability,
            TimeSpan.FromSeconds(90));

        Assert.Equal(DirectiveExecutionMode.SingleShot, absent.Mode);
        Assert.Equal(
            DirectiveExecutionPolicyDecisionCode.DefaultSingleShot,
            absent.DecisionCode);
        Assert.False(absent.AllowsProgressReports);
        Assert.Equal(DirectiveExecutionMode.SingleShot, explicitSingleShot.Mode);
        Assert.Equal(
            DirectiveExecutionPolicyDecisionCode.ExplicitSingleShot,
            explicitSingleShot.DecisionCode);
    }

    [Fact]
    public void Compatible_request_capability_and_temporal_values_enable_checkpointable_mode()
    {
        var effective = DirectiveExecutionPolicyComposer.ComposeV1(
            CheckpointableRequest(),
            CheckpointableCapability(),
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(65));

        Assert.Equal(DirectiveExecutionPolicyContractVersions.V1, effective.ContractVersion);
        Assert.Equal(DirectiveExecutionMode.Checkpointable, effective.Mode);
        Assert.Equal(
            DirectiveExecutionPolicyDecisionCode.Checkpointable,
            effective.DecisionCode);
        Assert.Equal(TimeSpan.FromSeconds(90), effective.TotalExecutionBudget);
        Assert.Equal(TimeSpan.FromSeconds(65), effective.RemainingExecutionTime);
        Assert.Equal(TimeSpan.FromSeconds(15), effective.CheckpointLeadTime);
        Assert.True(effective.AllowsProgressReports);
    }

    [Theory]
    [InlineData(Scenario.UnsupportedRequestVersion, DirectiveExecutionPolicyDecisionCode.RequestVersionUnsupported)]
    [InlineData(Scenario.MissingCapability, DirectiveExecutionPolicyDecisionCode.PositionCapabilityMissing)]
    [InlineData(Scenario.UnsupportedCapabilityVersion, DirectiveExecutionPolicyDecisionCode.PositionVersionUnsupported)]
    [InlineData(Scenario.CapabilityExceeded, DirectiveExecutionPolicyDecisionCode.PositionCapabilityExceeded)]
    [InlineData(Scenario.MissingBudget, DirectiveExecutionPolicyDecisionCode.ExecutionBudgetMissing)]
    [InlineData(Scenario.LeadTimeConsumesBudget, DirectiveExecutionPolicyDecisionCode.TemporalValuesIncoherent)]
    [InlineData(Scenario.RemainingTimeExpandsBudget, DirectiveExecutionPolicyDecisionCode.TemporalValuesIncoherent)]
    public void Relaxation_or_incompatibility_fails_closed(
        Scenario scenario,
        DirectiveExecutionPolicyDecisionCode expectedCode)
    {
        var request = scenario == Scenario.UnsupportedRequestVersion
            ? new DirectiveExecutionPolicyRequest(2, DirectiveExecutionMode.Checkpointable)
            : CheckpointableRequest();
        var capability = scenario switch
        {
            Scenario.MissingCapability => null,
            Scenario.UnsupportedCapabilityVersion =>
                new DirectiveExecutionPolicyCapability(
                    2,
                    DirectiveExecutionMode.Checkpointable,
                    TimeSpan.FromSeconds(15)),
            Scenario.CapabilityExceeded =>
                new DirectiveExecutionPolicyCapability(
                    1,
                    DirectiveExecutionMode.SingleShot),
            Scenario.LeadTimeConsumesBudget =>
                new DirectiveExecutionPolicyCapability(
                    1,
                    DirectiveExecutionMode.Checkpointable,
                    TimeSpan.FromSeconds(90)),
            _ => CheckpointableCapability(),
        };
        var total = scenario == Scenario.MissingBudget
            ? (TimeSpan?)null
            : TimeSpan.FromSeconds(90);
        var remaining = scenario == Scenario.RemainingTimeExpandsBudget
            ? TimeSpan.FromSeconds(91)
            : (TimeSpan?)null;

        var effective = DirectiveExecutionPolicyComposer.ComposeV1(
            request,
            capability,
            total,
            remaining);

        Assert.Equal(DirectiveExecutionMode.SingleShot, effective.Mode);
        Assert.Equal(expectedCode, effective.DecisionCode);
        Assert.False(effective.AllowsProgressReports);
    }

    [Fact]
    public void Contract_temporal_values_must_be_positive_and_coherent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DirectiveExecutionPolicyRequest(0, DirectiveExecutionMode.SingleShot));
        Assert.Throws<ArgumentException>(() =>
            new DirectiveExecutionPolicyCapability(
                1,
                DirectiveExecutionMode.Checkpointable));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DirectiveExecutionPolicyCapability(
                1,
                DirectiveExecutionMode.Checkpointable,
                TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() =>
            new DirectiveExecutionPolicyCapability(
                1,
                DirectiveExecutionMode.SingleShot,
                TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Registry_json_round_trips_position_capability()
    {
        var configuration = new AiConfiguration(
            "stub",
            "model",
            directiveExecutionPolicy: CheckpointableCapability());

        var restored = RegistryJson.Deserialize<AiConfiguration>(
            RegistryJson.Serialize(configuration));

        Assert.Equal(
            DirectiveExecutionMode.Checkpointable,
            restored.DirectiveExecutionPolicy!.MaximumMode);
        Assert.Equal(
            TimeSpan.FromSeconds(15),
            restored.DirectiveExecutionPolicy.CheckpointLeadTime);
    }

    private static DirectiveExecutionPolicyRequest CheckpointableRequest() =>
        new(1, DirectiveExecutionMode.Checkpointable);

    private static DirectiveExecutionPolicyCapability CheckpointableCapability() =>
        new(
            1,
            DirectiveExecutionMode.Checkpointable,
            TimeSpan.FromSeconds(15));

    public enum Scenario
    {
        UnsupportedRequestVersion,
        MissingCapability,
        UnsupportedCapabilityVersion,
        CapabilityExceeded,
        MissingBudget,
        LeadTimeConsumesBudget,
        RemainingTimeExpandsBudget,
    }
}
