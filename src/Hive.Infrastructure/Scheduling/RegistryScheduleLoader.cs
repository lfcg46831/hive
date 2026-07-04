using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Scheduling;
using Hive.Infrastructure.Organization.Registry;

namespace Hive.Infrastructure.Scheduling;

public static partial class RegistryScheduleLoader
{
    public static RegistryScheduleLoadResult Load(OrganizationRegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var schedules = ImmutableArray.CreateBuilder<LoadedRegistrySchedule>();
        var errors = ImmutableArray.CreateBuilder<RegistryScheduleLoadError>();

        foreach (var (key, entry) in snapshot.Schedules
            .OrderBy(pair => pair.Key.PositionId.Value, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.ScheduleId, StringComparer.Ordinal))
        {
            LoadSchedule(snapshot, key, entry, schedules, errors);
        }

        return new RegistryScheduleLoadResult(schedules.ToImmutable(), errors.ToImmutable());
    }

    private static void LoadSchedule(
        OrganizationRegistrySnapshot snapshot,
        RegistryScheduleKey key,
        RegistryEntry<RegistrySchedule>? entry,
        ImmutableArray<LoadedRegistrySchedule>.Builder schedules,
        ImmutableArray<RegistryScheduleLoadError>.Builder errors)
    {
        var path = $"schedules[{key}]";
        if (entry?.Value is not { } schedule)
        {
            errors.Add(new RegistryScheduleLoadError(
                "schedule-entry-missing",
                path,
                "The registry schedule entry is missing."));
            return;
        }

        var entryErrors = ImmutableArray.CreateBuilder<RegistryScheduleLoadError>();
        if (key.PositionId != schedule.PositionId
            || !string.Equals(key.ScheduleId, schedule.ScheduleId, StringComparison.Ordinal))
        {
            entryErrors.Add(new RegistryScheduleLoadError(
                "schedule-metadata-incoherent",
                path,
                "The registry schedule key does not match the schedule metadata."));
        }

        RegistryPosition? position = null;
        if (!snapshot.Positions.TryGetValue(schedule.PositionId, out var positionEntry)
            || positionEntry.Value is not { } foundPosition)
        {
            entryErrors.Add(new RegistryScheduleLoadError(
                "schedule-position-not-found",
                $"{path}.position_id",
                $"Schedule position '{schedule.PositionId.Value}' does not exist in the registry snapshot."));
        }
        else
        {
            position = foundPosition;
        }

        RegistryOccupant? occupant = null;
        if (!snapshot.Occupants.TryGetValue(schedule.PositionId, out var occupantEntry)
            || occupantEntry.Value is not { } foundOccupant)
        {
            entryErrors.Add(new RegistryScheduleLoadError(
                "schedule-occupant-not-found",
                $"{path}.position_id",
                $"Schedule position '{schedule.PositionId.Value}' has no occupant metadata."));
        }
        else
        {
            occupant = foundOccupant;
        }

        if (!TryValidateCron(schedule.Cron, out var cronReason))
        {
            entryErrors.Add(new RegistryScheduleLoadError(
                "schedule-cron-invalid",
                $"{path}.cron",
                cronReason));
        }

        var timezone = position?.Timezone;
        if (string.IsNullOrWhiteSpace(timezone))
        {
            entryErrors.Add(new RegistryScheduleLoadError(
                "position-timezone-required",
                $"{path}.timezone",
                $"Schedule position '{schedule.PositionId.Value}' must declare a timezone."));
        }
        else if (!TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out _))
        {
            entryErrors.Add(new RegistryScheduleLoadError(
                "position-timezone-invalid",
                $"{path}.timezone",
                $"Timezone '{timezone}' could not be resolved by the .NET runtime."));
        }

        LoadedScheduleWorkingHours? workingHours = null;
        if (occupant?.WorkingHours is null)
        {
            entryErrors.Add(new RegistryScheduleLoadError(
                "working-hours-required",
                $"{path}.working_hours",
                $"Schedule position '{schedule.PositionId.Value}' must declare working_hours."));
        }
        else if (!TryCreateWorkingHours(occupant.WorkingHours, out workingHours, out var workingHoursReason))
        {
            entryErrors.Add(new RegistryScheduleLoadError(
                "working-hours-invalid",
                $"{path}.working_hours",
                workingHoursReason));
        }

        if (!PriorityContract.TryParseWireValue(schedule.Priority, out var priority))
        {
            entryErrors.Add(new RegistryScheduleLoadError(
                "schedule-priority-invalid",
                $"{path}.priority",
                $"Schedule priority '{schedule.Priority}' is not a canonical priority value."));
        }

        if (!CatchUpPolicyContract.TryParseWireValue(schedule.CatchUp, out var catchUp))
        {
            entryErrors.Add(new RegistryScheduleLoadError(
                "schedule-catch-up-invalid",
                $"{path}.catch_up",
                $"Schedule catch_up '{schedule.CatchUp}' is not a canonical catch-up policy."));
        }

        if (entryErrors.Count > 0)
        {
            errors.AddRange(entryErrors);
            return;
        }

        try
        {
            schedules.Add(new LoadedRegistrySchedule(
                snapshot.OrganizationId,
                schedule.PositionId,
                ScheduleDefinition.Create(
                    ScheduleId.From(schedule.ScheduleId),
                    CronExpression.From(schedule.Cron),
                    timezone!,
                    schedule.Instruction,
                    priority,
                    schedule.IsCritical,
                    catchUp),
                schedule.IsActive,
                workingHours!));
        }
        catch (Exception exception)
            when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            errors.Add(new RegistryScheduleLoadError(
                "schedule-definition-invalid",
                path,
                exception.Message));
        }
    }

    private static bool TryCreateWorkingHours(
        WorkingHoursConfiguration configuration,
        out LoadedScheduleWorkingHours? workingHours,
        out string reason)
    {
        workingHours = null;
        if (!TryParseTime(configuration.Start, out var start))
        {
            reason = $"working_hours.start '{configuration.Start}' must use HH:mm.";
            return false;
        }

        if (!TryParseTime(configuration.End, out var end))
        {
            reason = $"working_hours.end '{configuration.End}' must use HH:mm.";
            return false;
        }

        if (start >= end)
        {
            reason = "working_hours must satisfy start < end; end is exclusive.";
            return false;
        }

        workingHours = new LoadedScheduleWorkingHours(start, end);
        reason = string.Empty;
        return true;
    }

    private static bool TryParseTime(string value, out TimeOnly time) =>
        TimeOnly.TryParseExact(
            value,
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out time);

    private static bool TryValidateCron(string cron, out string reason)
    {
        if (string.IsNullOrWhiteSpace(cron))
        {
            reason = "Cron expression cannot be empty.";
            return false;
        }

        if (!string.Equals(cron, cron.Trim(), StringComparison.Ordinal))
        {
            reason = "Cron expression cannot contain leading or trailing whitespace.";
            return false;
        }

        var fields = Whitespace().Split(cron);
        if (fields.Length is < 6 or > 7)
        {
            reason = "Cron expression must contain 6 or 7 Quartz fields.";
            return false;
        }

        if (!ValidateField(fields[0], 0, 59, null, allowQuestion: false, out reason)
            || !ValidateField(fields[1], 0, 59, null, allowQuestion: false, out reason)
            || !ValidateField(fields[2], 0, 23, null, allowQuestion: false, out reason)
            || !ValidateField(fields[3], 1, 31, null, allowQuestion: true, out reason)
            || !ValidateField(fields[4], 1, 12, MonthNames, allowQuestion: false, out reason)
            || !ValidateField(fields[5], 0, 7, DayNames, allowQuestion: true, out reason)
            || (fields.Length == 7 && !ValidateField(fields[6], 1970, 2099, null, allowQuestion: false, out reason)))
        {
            return false;
        }

        var dayOfMonthUsesPlaceholder = fields[3] == "?";
        var dayOfWeekUsesPlaceholder = fields[5] == "?";
        if (dayOfMonthUsesPlaceholder == dayOfWeekUsesPlaceholder)
        {
            reason = "Cron expression must use '?' in exactly one of day-of-month or day-of-week for Quartz scheduling.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool ValidateField(
        string field,
        int min,
        int max,
        IReadOnlyDictionary<string, int>? names,
        bool allowQuestion,
        out string reason)
    {
        var items = field.Split(',');
        if (items.Any(string.IsNullOrWhiteSpace))
        {
            reason = "Cron fields cannot contain empty list items.";
            return false;
        }

        foreach (var item in items)
        {
            if (!ValidateFieldItem(item, min, max, names, allowQuestion, out reason))
            {
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool ValidateFieldItem(
        string item,
        int min,
        int max,
        IReadOnlyDictionary<string, int>? names,
        bool allowQuestion,
        out string reason)
    {
        var parts = item.Split('/', 2);
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var step) || step <= 0))
        {
            reason = $"Cron step '{item}' must use a positive integer.";
            return false;
        }

        var range = parts[0];
        if (range == "?")
        {
            if (parts.Length == 2)
            {
                reason = "Cron '?' cannot be combined with a step.";
                return false;
            }

            reason = allowQuestion
                ? string.Empty
                : "Cron '?' is only allowed in day-of-month or day-of-week fields.";
            return allowQuestion;
        }

        if (range == "*")
        {
            reason = string.Empty;
            return true;
        }

        var bounds = range.Split('-', 2);
        if (bounds.Length == 2)
        {
            if (!TryParseCronValue(bounds[0], min, max, names, out var start)
                || !TryParseCronValue(bounds[1], min, max, names, out var end)
                || start > end)
            {
                reason = $"Cron range '{range}' is outside the allowed bounds.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        if (!TryParseCronValue(range, min, max, names, out _))
        {
            reason = $"Cron value '{range}' is outside the allowed bounds.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryParseCronValue(
        string value,
        int min,
        int max,
        IReadOnlyDictionary<string, int>? names,
        out int parsed)
    {
        if (names is not null && names.TryGetValue(value.ToUpperInvariant(), out parsed))
        {
            return true;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
            && parsed >= min
            && parsed <= max;
    }

    private static readonly IReadOnlyDictionary<string, int> MonthNames =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["JAN"] = 1,
            ["FEB"] = 2,
            ["MAR"] = 3,
            ["APR"] = 4,
            ["MAY"] = 5,
            ["JUN"] = 6,
            ["JUL"] = 7,
            ["AUG"] = 8,
            ["SEP"] = 9,
            ["OCT"] = 10,
            ["NOV"] = 11,
            ["DEC"] = 12,
        };

    private static readonly IReadOnlyDictionary<string, int> DayNames =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["SUN"] = 0,
            ["MON"] = 1,
            ["TUE"] = 2,
            ["WED"] = 3,
            ["THU"] = 4,
            ["FRI"] = 5,
            ["SAT"] = 6,
        };

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

public sealed record RegistryScheduleLoadResult(
    ImmutableArray<LoadedRegistrySchedule> Schedules,
    ImmutableArray<RegistryScheduleLoadError> Errors)
{
    public bool IsValid => Errors.IsEmpty;
}

public sealed record LoadedRegistrySchedule(
    OrganizationId OrganizationId,
    PositionId PositionId,
    ScheduleDefinition Definition,
    bool IsActive,
    LoadedScheduleWorkingHours WorkingHours);

public sealed record LoadedScheduleWorkingHours(TimeOnly Start, TimeOnly End);

public sealed record RegistryScheduleLoadError(string Code, string Path, string Message)
{
    public override string ToString() => $"{Code}: {Path}: {Message}";
}
