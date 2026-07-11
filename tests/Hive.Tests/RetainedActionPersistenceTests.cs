using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class RetainedActionPersistenceTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Action_retained_is_idempotent_by_identity_and_correlation()
    {
        var first = SampleAction("correlation-1");
        var sameCorrelation = SampleAction("correlation-1", RetainedActionId.New());

        var state = PositionState.Empty
            .Apply(new ActionRetained(first))
            .Apply(new ActionRetained(first))
            .Apply(new ActionRetained(sameCorrelation));

        Assert.Equal(first, Assert.Single(state.RetainedActions).Value);
        Assert.Equal(RetainedActionState.Retained, first.State);
    }

    [Fact]
    public void Retained_action_round_trips_through_snapshot_with_full_lineage()
    {
        var parent = DirectiveId.New();
        var action = SampleAction("correlation-2", parentDirectiveId: parent);

        var snapshot = PositionState.Empty
            .Apply(new ActionRetained(action))
            .ToSnapshot(At.AddMinutes(1));
        var restored = PositionState.Restore(snapshot);
        var actual = Assert.Single(restored.RetainedActions).Value;

        Assert.Equal(action, actual);
        Assert.Equal(parent, actual.ParentDirectiveId);
        Assert.Equal(new[] { "policy-a", "policy-b" }, actual.ApprovalPolicies.Select(policy => policy.Value));
        Assert.Equal(action.GovernanceMessages, actual.GovernanceMessages);
    }

    [Fact]
    public void Retained_action_blocks_passivation()
    {
        var state = PositionState.Empty.Apply(new ActionRetained(SampleAction("correlation-3")));
        var configuration = new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(1, "sha256:configuration"),
            OrganizationId.From("acme"),
            PositionId.From("delivery-lead"),
            new PositionRuntimeDescriptor(UnitId.From("delivery")),
            new OccupantRuntimeConfiguration(OccupantType.AiAgent),
            new PositionAuthorityRuntimeConfiguration(),
            Array.Empty<PositionScheduleRuntimeConfiguration>());

        var decision = state.EvaluatePassivation(configuration);

        Assert.False(decision.IsAllowed);
        Assert.Contains(PositionPassivationBlockReason.RetainedAction, decision.BlockReasons);
    }

    [Fact]
    public void Policies_are_distinct_and_canonical()
    {
        var action = SampleAction("correlation-4");

        Assert.Equal(new[] { "policy-a", "policy-b" }, action.ApprovalPolicies.Select(policy => policy.Value));
    }

    [Fact]
    public void Snapshot_rejects_duplicate_retained_action_correlation()
    {
        var first = SampleAction("duplicate-correlation");
        var second = SampleAction("duplicate-correlation", RetainedActionId.New());

        Assert.Throws<ArgumentException>(() => new PositionSnapshot(
            At,
            retainedActions: new[] { first, second }));
    }

    private static PersistedRetainedAction SampleAction(
        string correlationId,
        RetainedActionId? id = null,
        DirectiveId? parentDirectiveId = null) =>
        new(
            id ?? RetainedActionId.New(),
            ActionFingerprint.From("sha256:action"),
            RetainedActionKind.Tool,
            "github.create-issue",
            "{\"arguments\":{\"title\":\"Incident\"}}",
            "{\"repository\":\"acme/hive\"}",
            correlationId,
            OrganizationId.From("acme"),
            PositionId.From("delivery-lead"),
            ThreadId.New(),
            MessageId.New(),
            DirectiveId.New(),
            parentDirectiveId,
            "action-gate-human-approval-required",
            At,
            new[]
            {
                ApprovalPolicyRef.From("policy-b"),
                ApprovalPolicyRef.From("policy-a"),
                ApprovalPolicyRef.From("policy-b"),
            },
            new[] { SampleGovernanceMessage() });

    private static Escalation SampleGovernanceMessage() =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(PositionId.From("ceo")),
            ThreadId.New(),
            Priority.High,
            1,
            At,
            null,
            "Action retained",
            "Awaiting authorization",
            []);
}
