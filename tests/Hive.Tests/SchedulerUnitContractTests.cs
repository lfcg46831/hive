using Hive.Actors.Scheduling;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Scheduling;
using Hive.Infrastructure.Scheduling;

namespace Hive.Tests;

public sealed class SchedulerUnitContractTests
{
    private static readonly OrganizationId Org = OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("delivery-lead");

    [Fact]
    public void Window_calculator_treats_exact_cron_fire_as_the_canonical_window_start()
    {
        var materialization = Materialization(
            "daily-report",
            "0 0 9 ? * MON-FRI",
            "Europe/Lisbon");
        var firedAtUtc = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);

        var dispatchWindow = SchedulerScheduleWindowCalculator.Calculate(
            materialization,
            firedAtUtc);

        Assert.Equal(firedAtUtc, dispatchWindow.Window.Start);
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 8, 0, 0, TimeSpan.Zero), dispatchWindow.Window.End);
        Assert.Contains(
            "/2026-07-01T08:00:00.0000000Z/2026-07-02T08:00:00.0000000Z",
            dispatchWindow.IdempotencyKey.Value);
    }

    [Fact]
    public void Window_calculator_resolves_seasonal_offsets_from_the_schedule_timezone()
    {
        var materialization = Materialization(
            "morning-report",
            "0 0 9 ? * MON-FRI",
            "Europe/Lisbon");

        var winter = SchedulerScheduleWindowCalculator.Calculate(
            materialization,
            new DateTimeOffset(2026, 1, 5, 9, 1, 0, TimeSpan.Zero));
        var summer = SchedulerScheduleWindowCalculator.Calculate(
            materialization,
            new DateTimeOffset(2026, 7, 3, 8, 1, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero), winter.Window.Start);
        Assert.Equal(new DateTimeOffset(2026, 7, 3, 8, 0, 0, TimeSpan.Zero), summer.Window.Start);
    }

    [Fact]
    public void Dispatch_policy_allows_critical_windows_outside_hours_but_budget_still_blocks()
    {
        var materialization = Materialization(
            "critical-report",
            "0 0 18 ? * MON-FRI",
            "Europe/Lisbon",
            Priority.Critical,
            isCritical: true);
        var dispatch = BuildDispatch(
            materialization,
            new DateTimeOffset(2026, 7, 3, 17, 0, 0, TimeSpan.Zero));

        var withBudget = SchedulerDispatchPolicy.Evaluate(
            materialization,
            dispatch,
            hasAvailableProactiveBudget: true);
        var withoutBudget = SchedulerDispatchPolicy.Evaluate(
            materialization,
            dispatch,
            hasAvailableProactiveBudget: false);

        Assert.True(withBudget.IsAllowed, withBudget.Reason?.Code);
        Assert.False(withoutBudget.IsAllowed);
        Assert.Equal("scheduler-proactive-budget-unavailable", withoutBudget.Reason?.Code);
    }

    [Fact]
    public void Proactive_budget_request_preserves_identity_window_key_and_criticality()
    {
        var materialization = Materialization(
            "critical-report",
            "0 55 17 ? * MON-FRI",
            "Europe/Lisbon",
            Priority.Critical,
            isCritical: true);
        var dispatch = BuildDispatch(
            materialization,
            new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero));

        var request = SchedulerProactiveBudgetRequest.From(materialization, dispatch);

        Assert.Equal(Org, request.OrganizationId);
        Assert.Equal(Position, request.PositionId);
        Assert.Equal(ScheduleId.From("critical-report"), request.ScheduleId);
        Assert.Equal(dispatch.Window, request.Window);
        Assert.Equal(dispatch.IdempotencyKey, request.IdempotencyKey);
        Assert.Equal(dispatch.FiredAtUtc, request.FiredAtUtc);
        Assert.True(request.IsCritical);
    }

    [Fact]
    public void Missed_window_evaluator_uses_latest_window_and_skips_non_catchup_schedules()
    {
        var materialization = Materialization(
            "daily-report",
            "0 55 17 ? * MON-FRI",
            "Europe/Lisbon",
            declaredAtUtc: new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        var nowUtc = new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

        var resolved = SchedulerMissedWindowEvaluator.TryResolveCandidate(
            materialization,
            nowUtc,
            out var candidate,
            out var error);

        Assert.True(resolved, error?.ToString());
        Assert.NotNull(candidate);
        Assert.Equal(new DateTimeOffset(2026, 7, 3, 16, 55, 0, TimeSpan.Zero), candidate.DispatchWindow.Window.Start);
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 16, 55, 0, TimeSpan.Zero), candidate.DispatchWindow.Window.End);

        var decision = SchedulerMissedWindowEvaluator.Decide(
            materialization,
            candidate,
            existingDelivery: null);

        Assert.Equal(SchedulerMissedWindowAction.Skip, decision.Action);
        Assert.Equal("scheduler-missed-window-skipped", decision.Reason?.Code);
    }

    [Fact]
    public void Missed_window_evaluator_rejects_existing_delivery_for_a_different_key()
    {
        var materialization = Materialization(
            "daily-report",
            "0 55 17 ? * MON-FRI",
            "Europe/Lisbon",
            declaredAtUtc: new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        var resolved = SchedulerMissedWindowEvaluator.TryResolveCandidate(
            materialization,
            new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero),
            out var candidate,
            out var error);
        Assert.True(resolved, error?.ToString());
        var other = BuildDispatch(
            Materialization("other-report", "0 55 17 ? * MON-FRI", "Europe/Lisbon"),
            new DateTimeOffset(2026, 7, 3, 17, 10, 0, TimeSpan.Zero));
        var existing = new SchedulerPulseDeliveryState(
            other.IdempotencyKey,
            other.Pulse.Id,
            other.Pulse.Thread,
            SchedulerPulseDeliveryStatus.Delivered,
            attemptCount: 1,
            lastOccurredAtUtc: other.FiredAtUtc,
            reason: null);

        Assert.Throws<ArgumentException>(() =>
            SchedulerMissedWindowEvaluator.Decide(
                materialization,
                candidate!,
                existing));
    }

    private static SchedulerScheduleDispatch BuildDispatch(
        SchedulerScheduleMaterialization materialization,
        DateTimeOffset firedAtUtc)
    {
        var dispatchWindow = SchedulerScheduleWindowCalculator.Calculate(materialization, firedAtUtc);
        var pulse = SchedulerPulseFactory.Build(
            materialization,
            firedAtUtc,
            dispatchWindow.IdempotencyKey);

        return new SchedulerScheduleDispatch(
            materialization.Key,
            firedAtUtc,
            dispatchWindow.Window,
            dispatchWindow.IdempotencyKey,
            pulse);
    }

    private static SchedulerScheduleMaterialization Materialization(
        string scheduleId,
        string cron,
        string timeZone,
        Priority priority = Priority.Normal,
        bool isCritical = false,
        CatchUpPolicy catchUp = CatchUpPolicy.Skip,
        DateTimeOffset? declaredAtUtc = null) =>
        new(
            SchedulerScheduleKey.From(Org, Position, ScheduleId.From(scheduleId)),
            ScheduleDefinition.Create(
                ScheduleId.From(scheduleId),
                CronExpression.From(cron),
                timeZone,
                "Run scheduled work",
                priority,
                isCritical,
                catchUp),
            new LoadedScheduleWorkingHours(new TimeOnly(9, 0), new TimeOnly(18, 0)),
            declaredAtUtc);
}
