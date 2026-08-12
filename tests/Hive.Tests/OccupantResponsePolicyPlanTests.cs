using Hive.Actors.Positions;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class OccupantResponsePolicyPlanTests
{
    private static readonly PositionEntityId EntityId = PositionEntityId.From(
        OrganizationId.From("acme"),
        PositionId.From("delivery-lead"));

    [Fact]
    public void Non_critical_policy_counts_only_time_inside_the_daily_working_window()
    {
        var dispatchedAt = new DateTimeOffset(2026, 8, 12, 16, 0, 0, TimeSpan.Zero);
        var policy = Policy();

        var plan = OccupantResponsePolicyPlan.Create(
            EntityId,
            Directive(Priority.High, dispatchedAt),
            dispatchedAt,
            policy);

        var reminder = Assert.Single(plan.Reminders);
        Assert.Equal(new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero), reminder.ScheduledForUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 13, 11, 0, 0, TimeSpan.Zero), plan.TimeoutAtUtc);
    }

    [Fact]
    public void Critical_policy_uses_elapsed_time_even_outside_working_hours()
    {
        var dispatchedAt = new DateTimeOffset(2026, 8, 12, 19, 0, 0, TimeSpan.Zero);

        var plan = OccupantResponsePolicyPlan.Create(
            EntityId,
            Directive(Priority.Critical, dispatchedAt),
            dispatchedAt,
            Policy());

        Assert.Equal(dispatchedAt.AddHours(2), Assert.Single(plan.Reminders).ScheduledForUtc);
        Assert.Equal(dispatchedAt.AddHours(4), plan.TimeoutAtUtc);
    }

    [Fact]
    public void Recomputing_the_same_plan_preserves_reminder_and_timeout_message_ids()
    {
        var dispatchedAt = new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.Zero);
        var message = Directive(Priority.Normal, dispatchedAt);

        var first = OccupantResponsePolicyPlan.Create(EntityId, message, dispatchedAt, Policy());
        var second = OccupantResponsePolicyPlan.Create(EntityId, message, dispatchedAt, Policy());

        Assert.Equal(first.TimeoutAtUtc, second.TimeoutAtUtc);
        Assert.Equal(first.Reminders, second.Reminders);
        Assert.Equal(
            OccupantResponsePolicyPlan.TimeoutEscalationId(EntityId, message.Id, first.TimeoutAtUtc),
            OccupantResponsePolicyPlan.TimeoutEscalationId(EntityId, message.Id, second.TimeoutAtUtc));
    }

    [Fact]
    public void Only_message_kinds_with_a_closed_human_response_open_a_policy()
    {
        var at = new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.Zero);

        Assert.True(OccupantResponsePolicyPlan.IsEligible(Directive(Priority.Normal, at)));
        Assert.False(OccupantResponsePolicyPlan.IsEligible(new Memo(
            MessageId.New(),
            EntityId.Organization,
            new PositionEndpointRef(PositionId.From("peer")),
            new PositionEndpointRef(EntityId.Position),
            ThreadId.New(),
            Priority.Normal,
            1,
            at,
            null,
            "Informational only")));
    }

    private static OccupantResponsePolicyRuntimeConfiguration Policy() => new(
        reminderMaxCount: 1,
        reminderInterval: TimeSpan.FromHours(2),
        timeout: TimeSpan.FromHours(4),
        timeZoneId: "Europe/Lisbon",
        workingHoursStart: new TimeOnly(9, 0),
        workingHoursEnd: new TimeOnly(18, 0));

    private static Directive Directive(Priority priority, DateTimeOffset sentAt) => new(
        MessageId.From(new Guid("b1000000-0000-0000-0000-000000000001")),
        EntityId.Organization,
        new OrganizationOwnerEndpointRef(),
        new PositionEndpointRef(EntityId.Position),
        ThreadId.From(new Guid("b1000000-0000-0000-0000-000000000002")),
        priority,
        1,
        sentAt,
        null,
        DirectiveId.From(new Guid("b1000000-0000-0000-0000-000000000003")),
        null,
        "Handle the request",
        "Context");
}
