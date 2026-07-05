using Hive.Domain.Scheduling;

namespace Hive.Actors.Scheduling;

internal static class SchedulerScheduleWindowCalculator
{
    public static SchedulerScheduleDispatchWindow Calculate(
        SchedulerScheduleMaterialization materialization,
        DateTimeOffset firedAtUtc)
    {
        if (TryCalculate(materialization, firedAtUtc, out var dispatchWindow, out var error))
        {
            return dispatchWindow!;
        }

        throw new InvalidOperationException(error!.ToString());
    }

    public static bool TryCalculate(
        SchedulerScheduleMaterialization materialization,
        DateTimeOffset firedAtUtc,
        out SchedulerScheduleDispatchWindow? dispatchWindow,
        out SchedulerDispatchError? error)
    {
        ArgumentNullException.ThrowIfNull(materialization);

        dispatchWindow = null;
        error = null;

        if (firedAtUtc.Offset != TimeSpan.Zero)
        {
            error = new SchedulerDispatchError(
                "scheduler-fired-at-not-utc",
                "Scheduler dispatch timestamps must be expressed as UTC offsets.");
            return false;
        }

        var cron = new Quartz.CronExpression(materialization.Definition.Cron.Value)
        {
            TimeZone = TimeZoneInfo.FindSystemTimeZoneById(materialization.Definition.TimeZone),
        };

        var previous = FindPreviousFireTime(cron, firedAtUtc);
        if (previous is null)
        {
            error = new SchedulerDispatchError(
                "schedule-window-unresolved",
                $"Schedule '{materialization.Key.Value}' has no cron occurrence before '{firedAtUtc:O}'.");
            return false;
        }

        var next = cron.GetNextValidTimeAfter(previous.Value);
        if (next is null)
        {
            error = new SchedulerDispatchError(
                "schedule-window-unresolved",
                $"Schedule '{materialization.Key.Value}' has no cron occurrence after '{previous.Value:O}'.");
            return false;
        }

        var window = TemporalWindow.From(
            previous.Value.ToUniversalTime(),
            next.Value.ToUniversalTime());
        var key = PulseIdempotencyKey.From(
            materialization.Key.Organization,
            materialization.Key.Position,
            materialization.Key.Schedule,
            window);

        dispatchWindow = new SchedulerScheduleDispatchWindow(window, key);
        return true;
    }

    private static DateTimeOffset? FindPreviousFireTime(
        Quartz.CronExpression cron,
        DateTimeOffset firedAtUtc)
    {
        var inclusiveBoundary = firedAtUtc.AddTicks(1);
        foreach (var lookback in LookbackWindows)
        {
            var candidate = cron.GetNextValidTimeAfter(inclusiveBoundary - lookback);
            if (candidate is null || candidate.Value > firedAtUtc)
            {
                continue;
            }

            var previous = candidate.Value;
            while (true)
            {
                var next = cron.GetNextValidTimeAfter(previous);
                if (next is null || next.Value > firedAtUtc)
                {
                    return previous;
                }

                previous = next.Value;
            }
        }

        return null;
    }

    // Start close to the observed fire for frequent schedules, then expand to cover sparse cron
    // expressions without scanning a long interval second by second.
    private static readonly TimeSpan[] LookbackWindows =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromHours(1),
        TimeSpan.FromDays(1),
        TimeSpan.FromDays(7),
        TimeSpan.FromDays(32),
        TimeSpan.FromDays(370),
        TimeSpan.FromDays(3700),
    ];
}

internal sealed record SchedulerScheduleDispatchWindow(
    TemporalWindow Window,
    PulseIdempotencyKey IdempotencyKey);
