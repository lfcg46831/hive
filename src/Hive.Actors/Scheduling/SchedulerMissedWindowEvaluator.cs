using Hive.Domain.Scheduling;
using Hive.Infrastructure.Scheduling;

namespace Hive.Actors.Scheduling;

internal enum SchedulerMissedWindowAction
{
    None,
    Skip,
    CatchUp,
}

internal static class SchedulerMissedWindowEvaluator
{
    public const string MissedWindowSkippedCode = "scheduler-missed-window-skipped";

    public static bool TryResolveCandidate(
        SchedulerScheduleMaterialization materialization,
        DateTimeOffset nowUtc,
        out SchedulerMissedWindowCandidate? candidate,
        out SchedulerDispatchError? error)
    {
        ArgumentNullException.ThrowIfNull(materialization);

        candidate = null;
        if (!SchedulerScheduleWindowCalculator.TryCalculate(
                materialization,
                nowUtc,
                out var dispatchWindow,
                out error))
        {
            return false;
        }

        if (dispatchWindow!.Window.Start < materialization.DeclaredAtUtc)
        {
            error = null;
            return false;
        }

        candidate = new SchedulerMissedWindowCandidate(dispatchWindow);
        return true;
    }

    public static SchedulerMissedWindowDecision Decide(
        SchedulerScheduleMaterialization materialization,
        SchedulerMissedWindowCandidate candidate,
        SchedulerPulseDeliveryState? existingDelivery)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        ArgumentNullException.ThrowIfNull(candidate);

        if (existingDelivery is not null)
        {
            if (existingDelivery.IdempotencyKey != candidate.DispatchWindow.IdempotencyKey)
            {
                throw new ArgumentException(
                    "Existing delivery state must match the missed window candidate key.",
                    nameof(existingDelivery));
            }

            return new SchedulerMissedWindowDecision(
                SchedulerMissedWindowAction.None,
                candidate.DispatchWindow,
                Reason: null);
        }

        if (materialization.Definition.IsCritical
            && materialization.Definition.CatchUp == CatchUpPolicy.CatchUpOnce)
        {
            return new SchedulerMissedWindowDecision(
                SchedulerMissedWindowAction.CatchUp,
                candidate.DispatchWindow,
                Reason: null);
        }

        return new SchedulerMissedWindowDecision(
            SchedulerMissedWindowAction.Skip,
            candidate.DispatchWindow,
            new SchedulerPulseDeliveryReason(
                MissedWindowSkippedCode,
                "Scheduler missed the latest canonical window while down and skipped catch-up."));
    }
}

internal sealed record SchedulerMissedWindowCandidate(
    SchedulerScheduleDispatchWindow DispatchWindow);

internal sealed record SchedulerMissedWindowDecision(
    SchedulerMissedWindowAction Action,
    SchedulerScheduleDispatchWindow DispatchWindow,
    SchedulerPulseDeliveryReason? Reason);
