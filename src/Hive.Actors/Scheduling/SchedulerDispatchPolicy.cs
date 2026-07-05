using Hive.Domain.Identity;
using Hive.Domain.Scheduling;
using Hive.Infrastructure.Scheduling;

namespace Hive.Actors.Scheduling;

public interface ISchedulerProactiveBudgetPolicy
{
    Task<bool> HasAvailableBudgetAsync(
        SchedulerProactiveBudgetRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record SchedulerProactiveBudgetRequest
{
    public SchedulerProactiveBudgetRequest(
        OrganizationId organizationId,
        PositionId positionId,
        ScheduleId scheduleId,
        TemporalWindow window,
        PulseIdempotencyKey idempotencyKey,
        DateTimeOffset firedAtUtc,
        bool isCritical)
    {
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        PositionId = positionId ?? throw new ArgumentNullException(nameof(positionId));
        ScheduleId = scheduleId ?? throw new ArgumentNullException(nameof(scheduleId));
        Window = window ?? throw new ArgumentNullException(nameof(window));
        IdempotencyKey = idempotencyKey ?? throw new ArgumentNullException(nameof(idempotencyKey));
        if (firedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Scheduler budget policy timestamps must be expressed as UTC offsets.",
                nameof(firedAtUtc));
        }

        FiredAtUtc = firedAtUtc;
        IsCritical = isCritical;
    }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public ScheduleId ScheduleId { get; }

    public TemporalWindow Window { get; }

    public PulseIdempotencyKey IdempotencyKey { get; }

    public DateTimeOffset FiredAtUtc { get; }

    public bool IsCritical { get; }

    internal static SchedulerProactiveBudgetRequest From(
        SchedulerScheduleMaterialization materialization,
        SchedulerScheduleDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        ArgumentNullException.ThrowIfNull(dispatch);

        return new SchedulerProactiveBudgetRequest(
            materialization.Key.Organization,
            materialization.Key.Position,
            materialization.Key.Schedule,
            dispatch.Window,
            dispatch.IdempotencyKey,
            dispatch.FiredAtUtc,
            materialization.Definition.IsCritical);
    }
}

public sealed class AllowingSchedulerProactiveBudgetPolicy : ISchedulerProactiveBudgetPolicy
{
    public static AllowingSchedulerProactiveBudgetPolicy Instance { get; } = new();

    private AllowingSchedulerProactiveBudgetPolicy()
    {
    }

    public Task<bool> HasAvailableBudgetAsync(
        SchedulerProactiveBudgetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }
}

internal static class SchedulerDispatchPolicy
{
    public const string OutsideWorkingHoursCode = "scheduler-outside-working-hours";
    public const string ProactiveBudgetUnavailableCode = "scheduler-proactive-budget-unavailable";

    public static SchedulerDispatchPolicyDecision Evaluate(
        SchedulerScheduleMaterialization materialization,
        SchedulerScheduleDispatch dispatch,
        bool hasAvailableProactiveBudget)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        ArgumentNullException.ThrowIfNull(dispatch);

        if (!materialization.Definition.IsCritical && !IsInsideWorkingHours(materialization, dispatch))
        {
            return SchedulerDispatchPolicyDecision.Skipped(new SchedulerPulseDeliveryReason(
                OutsideWorkingHoursCode,
                "Scheduler dispatch is outside the position working hours."));
        }

        if (!hasAvailableProactiveBudget)
        {
            return SchedulerDispatchPolicyDecision.Skipped(new SchedulerPulseDeliveryReason(
                ProactiveBudgetUnavailableCode,
                "Scheduler dispatch requires proactive budget, but no budget is available."));
        }

        return SchedulerDispatchPolicyDecision.Allowed;
    }

    private static bool IsInsideWorkingHours(
        SchedulerScheduleMaterialization materialization,
        SchedulerScheduleDispatch dispatch)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(materialization.Definition.TimeZone);
        var localWindowStart = TimeZoneInfo.ConvertTime(dispatch.Window.Start, timeZone);
        var localStartTime = TimeOnly.FromDateTime(localWindowStart.DateTime);

        return localStartTime >= materialization.WorkingHours.Start
            && localStartTime < materialization.WorkingHours.End;
    }
}

internal sealed record SchedulerDispatchPolicyDecision
{
    private SchedulerDispatchPolicyDecision(bool isAllowed, SchedulerPulseDeliveryReason? reason)
    {
        IsAllowed = isAllowed;
        Reason = reason;
    }

    public static SchedulerDispatchPolicyDecision Allowed { get; } = new(true, reason: null);

    public bool IsAllowed { get; }

    public SchedulerPulseDeliveryReason? Reason { get; }

    public static SchedulerDispatchPolicyDecision Skipped(SchedulerPulseDeliveryReason reason) =>
        new(false, reason ?? throw new ArgumentNullException(nameof(reason)));
}
