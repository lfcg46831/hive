using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

/// <summary>
/// Verifies US-F0-06-T11a: passivation eligibility is a domain decision with guard rails for
/// pending delivery, critical work, and active schedule/subscription triggers.
/// </summary>
public sealed class PositionPassivationDecisionTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 27, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Idle_position_without_pending_delivery_critical_work_or_active_triggers_can_passivate()
    {
        var decision = PositionState.Empty.EvaluatePassivation(RuntimeConfiguration());

        Assert.True(decision.IsAllowed);
        Assert.Empty(decision.BlockReasons);
    }

    [Fact]
    public void Pending_inbox_delivery_blocks_passivation()
    {
        var state = PositionState.Empty.Apply(new MessageReceived(SampleMessage(), At));

        var decision = state.EvaluatePassivation(RuntimeConfiguration());

        Assert.False(decision.IsAllowed);
        Assert.Equal(
            new[] { PositionPassivationBlockReason.PendingDelivery },
            decision.BlockReasons);
    }

    [Fact]
    public void Critical_open_task_blocks_passivation()
    {
        var taskId = PositionTaskId.New();
        var state = PositionState.Empty.Apply(new TaskCreated(
            taskId,
            ThreadId.New(),
            "Escalate blocked production deployment",
            Priority.Critical,
            At));

        var decision = state.EvaluatePassivation(RuntimeConfiguration());

        Assert.False(decision.IsAllowed);
        Assert.Equal(
            new[] { PositionPassivationBlockReason.CriticalTaskOpen },
            decision.BlockReasons);
    }

    [Fact]
    public void Non_critical_open_task_does_not_block_passivation_by_itself()
    {
        var taskId = PositionTaskId.New();
        var state = PositionState.Empty.Apply(new TaskCreated(
            taskId,
            ThreadId.New(),
            "Backlog grooming",
            Priority.High,
            At));

        var decision = state.EvaluatePassivation(RuntimeConfiguration());

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void Active_schedule_or_subscription_blocks_passivation()
    {
        var decision = PositionState.Empty.EvaluatePassivation(RuntimeConfiguration(
            schedules: new[] { new PositionScheduleRuntimeConfiguration("daily-pulse", "0 8 * * *", "Send pulse") },
            subscriptions: new[] { new SubscriptionConfiguration("deadline.near", "PT2H") }));

        Assert.False(decision.IsAllowed);
        Assert.Equal(
            new[]
            {
                PositionPassivationBlockReason.ActiveSchedule,
                PositionPassivationBlockReason.ActiveSubscription,
            },
            decision.BlockReasons);
    }

    [Fact]
    public void Multiple_guard_rails_are_reported_in_deterministic_order()
    {
        var message = SampleMessage();
        var state = PositionState.Empty
            .Apply(new MessageReceived(message, At))
            .Apply(new TaskCreated(
                PositionTaskId.New(),
                message.Thread,
                "Critical customer escalation",
                Priority.Critical,
                At.AddMinutes(1),
                causedBy: message.Id));

        var decision = state.EvaluatePassivation(RuntimeConfiguration(
            schedules: new[] { new PositionScheduleRuntimeConfiguration("daily-pulse", "0 8 * * *", "Send pulse") },
            subscriptions: new[] { new SubscriptionConfiguration("deadline.near", "PT2H") }));

        Assert.False(decision.IsAllowed);
        Assert.Equal(
            new[]
            {
                PositionPassivationBlockReason.PendingDelivery,
                PositionPassivationBlockReason.CriticalTaskOpen,
                PositionPassivationBlockReason.ActiveSchedule,
                PositionPassivationBlockReason.ActiveSubscription,
            },
            decision.BlockReasons);
    }

    private static PositionRuntimeConfiguration RuntimeConfiguration(
        IEnumerable<PositionScheduleRuntimeConfiguration>? schedules = null,
        IEnumerable<SubscriptionConfiguration>? subscriptions = null)
    {
        var entity = PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("bug-triage"));

        return new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(1, "sha256:v1"),
            entity.Organization,
            entity.Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: PositionId.From("cto"),
                name: "Bug triage",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "engineer-v1",
                ai: null,
                workingHours: null,
                subscriptions: subscriptions ?? Array.Empty<SubscriptionConfiguration>(),
                tools: Array.Empty<ToolConfiguration>()),
            new PositionAuthorityRuntimeConfiguration(
                canDecide: Array.Empty<string>(),
                mustEscalate: Array.Empty<string>(),
                requiresHumanApproval: Array.Empty<string>()),
            schedules ?? Array.Empty<PositionScheduleRuntimeConfiguration>());
    }

    private static Memo SampleMessage() => new(
        MessageId.New(),
        OrganizationId.From("acme"),
        new PositionEndpointRef(PositionId.From("delivery-lead")),
        new PositionEndpointRef(PositionId.From("bug-triage")),
        ThreadId.New(),
        Priority.Normal,
        schemaVersion: 1,
        sentAt: At,
        deadline: null,
        body: "Customer reported a regression.");
}
