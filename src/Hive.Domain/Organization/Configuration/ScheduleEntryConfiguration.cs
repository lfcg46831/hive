namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// One declared proactive job of an occupant (§4.6/§6.2 <c>occupant.schedule[]</c>): the entry
/// <see cref="Id"/>, the <see cref="Cron"/> expression (interpreted in the position timezone) and
/// the <see cref="Instruction"/> the occupant runs when it fires.
/// </summary>
public sealed record ScheduleEntryConfiguration
{
    /// <summary>Creates a schedule entry <paramref name="id"/> firing on <paramref name="cron"/>.</summary>
    public ScheduleEntryConfiguration(string id, string cron, string instruction)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(cron);
        ArgumentNullException.ThrowIfNull(instruction);

        Id = id;
        Cron = cron;
        Instruction = instruction;
    }

    /// <summary>The identifier of the schedule entry within the occupant.</summary>
    public string Id { get; }

    /// <summary>The cron expression, interpreted in the position timezone.</summary>
    public string Cron { get; }

    /// <summary>The instruction the occupant runs when the schedule fires.</summary>
    public string Instruction { get; }
}
