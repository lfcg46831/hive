namespace Hive.Domain.Scheduling;

/// <summary>
/// A declared cron expression of a schedule, interpreted in the position timezone
/// (§4.6/§6.2 <c>occupant.schedule[].cron</c>). This value object fixes the domain contract and only
/// enforces structural validity (non-empty, single-line). Semantic validation of the cron grammar and
/// next-fire computation belong to the loader/coordinator (US-F0-09-T02/T05), not to this type.
/// </summary>
public sealed record CronExpression
{
    private CronExpression(string value) => Value = value;

    /// <summary>The raw cron expression exactly as declared.</summary>
    public string Value { get; }

    /// <summary>Creates a cron expression from its declared textual form.</summary>
    public static CronExpression From(string value) =>
        new(SchedulingText.RequireToken(value, nameof(value)));

    public override string ToString() => Value;
}
