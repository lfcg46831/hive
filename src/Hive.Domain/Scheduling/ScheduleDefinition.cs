using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Scheduling;

/// <summary>
/// The canonical domain model of a single declared schedule (US-F0-09-T01): the <see cref="Id"/>, the
/// <see cref="Cron"/> expression and the <see cref="TimeZone"/> it is interpreted in, the
/// <see cref="Payload"/> delivered to the position, the <see cref="Priority"/> of the resulting Pulse,
/// whether the schedule is <see cref="IsCritical"/> (may run outside working hours) and its
/// <see cref="CatchUp"/> behaviour for missed windows.
/// </summary>
/// <remarks>
/// This is a pure declaration: it fixes what a schedule <em>is</em>. Loading/validating declarations
/// from the registry is US-F0-09-T02; computing the firing window and the
/// <see cref="PulseIdempotencyKey"/> is US-F0-09-T05; building the canonical Pulse is US-F0-09-T06.
/// A non-critical schedule cannot carry a catch-up replay, so that combination is rejected here
/// (US-F0-09-T10).
/// </remarks>
public sealed record ScheduleDefinition
{
    private ScheduleDefinition(
        ScheduleId id,
        CronExpression cron,
        string timeZone,
        string payload,
        Priority priority,
        bool isCritical,
        CatchUpPolicy catchUp)
    {
        Id = id;
        Cron = cron;
        TimeZone = timeZone;
        Payload = payload;
        Priority = priority;
        IsCritical = isCritical;
        CatchUp = catchUp;
    }

    /// <summary>The identity of the schedule within its position.</summary>
    public ScheduleId Id { get; }

    /// <summary>The cron expression, interpreted in <see cref="TimeZone"/>.</summary>
    public CronExpression Cron { get; }

    /// <summary>The IANA timezone identifier the cron is interpreted in (the position timezone).</summary>
    public string TimeZone { get; }

    /// <summary>The instruction/payload delivered on the resulting Pulse.</summary>
    public string Payload { get; }

    /// <summary>The priority carried by the resulting Pulse.</summary>
    public Priority Priority { get; }

    /// <summary>Whether the schedule is critical (may fire outside working hours and may catch up).</summary>
    public bool IsCritical { get; }

    /// <summary>The behaviour for windows missed while the scheduler was down.</summary>
    public CatchUpPolicy CatchUp { get; }

    /// <summary>
    /// Creates a schedule definition, validating each component. A non-critical schedule may only use
    /// <see cref="CatchUpPolicy.Skip"/>; requesting a catch-up replay on a non-critical schedule is a
    /// contradiction and is rejected.
    /// </summary>
    public static ScheduleDefinition Create(
        ScheduleId id,
        CronExpression cron,
        string timeZone,
        string payload,
        Priority priority,
        bool isCritical,
        CatchUpPolicy catchUp)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(cron);

        var validTimeZone = SchedulingText.RequireToken(timeZone, nameof(timeZone));
        var validPayload = SchedulingText.RequireContent(payload, nameof(payload));
        PriorityContract.RequireDefined(priority, nameof(priority));
        CatchUpPolicyContract.RequireDefined(catchUp, nameof(catchUp));

        if (!isCritical && catchUp == CatchUpPolicy.CatchUpOnce)
        {
            throw new ArgumentException(
                "Only critical schedules may use CatchUpOnce; a non-critical schedule must Skip missed windows.",
                nameof(catchUp));
        }

        return new ScheduleDefinition(id, cron, validTimeZone, validPayload, priority, isCritical, catchUp);
    }
}
