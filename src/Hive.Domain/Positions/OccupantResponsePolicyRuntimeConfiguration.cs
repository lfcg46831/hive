namespace Hive.Domain.Positions;

/// <summary>Validated runtime projection of a declared human-response policy.</summary>
public sealed record OccupantResponsePolicyRuntimeConfiguration
{
    public OccupantResponsePolicyRuntimeConfiguration(
        int reminderMaxCount,
        TimeSpan reminderInterval,
        TimeSpan timeout,
        string timeZoneId,
        TimeOnly workingHoursStart,
        TimeOnly workingHoursEnd)
    {
        if (reminderMaxCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reminderMaxCount),
                reminderMaxCount,
                "Reminder maximum count cannot be negative.");
        }

        if (reminderInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reminderInterval),
                reminderInterval,
                "Reminder interval must be positive.");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Response timeout must be positive.");
        }

        var reminderHorizon = TimeSpan.FromTicks(
            checked(reminderInterval.Ticks * reminderMaxCount));
        if (timeout <= reminderHorizon)
        {
            throw new ArgumentException(
                "Response timeout must be greater than the last reminder horizon.",
                nameof(timeout));
        }

        if (workingHoursStart >= workingHoursEnd)
        {
            throw new ArgumentException(
                "Working hours must satisfy start < end; end is exclusive.",
                nameof(workingHoursEnd));
        }

        ReminderMaxCount = reminderMaxCount;
        ReminderInterval = reminderInterval;
        Timeout = timeout;
        TimeZoneId = CommandText.RequireContent(timeZoneId, nameof(timeZoneId));
        WorkingHoursStart = workingHoursStart;
        WorkingHoursEnd = workingHoursEnd;
    }

    public int ReminderMaxCount { get; }

    public TimeSpan ReminderInterval { get; }

    public TimeSpan Timeout { get; }

    public string TimeZoneId { get; }

    public TimeOnly WorkingHoursStart { get; }

    public TimeOnly WorkingHoursEnd { get; }
}
