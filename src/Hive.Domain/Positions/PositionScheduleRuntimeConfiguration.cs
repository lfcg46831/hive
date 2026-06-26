namespace Hive.Domain.Positions;

/// <summary>
/// Runtime schedule entry projected for a position occupant (US-F0-06-T08a).
/// </summary>
public sealed record PositionScheduleRuntimeConfiguration
{
    public PositionScheduleRuntimeConfiguration(string id, string cron, string instruction)
    {
        Id = CommandText.RequireContent(id, nameof(id));
        Cron = CommandText.RequireContent(cron, nameof(cron));
        Instruction = CommandText.RequireContent(instruction, nameof(instruction));
    }

    /// <summary>The identifier of the schedule entry within the position.</summary>
    public string Id { get; }

    /// <summary>The cron expression, interpreted in the position timezone.</summary>
    public string Cron { get; }

    /// <summary>The instruction the occupant runs when the schedule fires.</summary>
    public string Instruction { get; }
}
