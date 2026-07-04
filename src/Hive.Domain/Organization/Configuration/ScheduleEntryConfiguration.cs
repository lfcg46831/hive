namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// One declared proactive job of an occupant (§4.6/§6.2 <c>occupant.schedule[]</c>): the entry
/// <see cref="Id"/>, the <see cref="Cron"/> expression (interpreted in the position timezone) and
/// the <see cref="Instruction"/> the occupant runs when it fires.
/// </summary>
public sealed record ScheduleEntryConfiguration
{
    /// <summary>Creates a schedule entry <paramref name="id"/> firing on <paramref name="cron"/>.</summary>
    public ScheduleEntryConfiguration(
        string id,
        string cron,
        string instruction,
        bool isActive = true,
        string priority = "normal",
        bool isCritical = false,
        string catchUp = "skip")
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(cron);
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(priority);
        ArgumentNullException.ThrowIfNull(catchUp);

        Id = id;
        Cron = cron;
        Instruction = instruction;
        IsActive = isActive;
        Priority = priority;
        IsCritical = isCritical;
        CatchUp = catchUp;
    }

    /// <summary>The identifier of the schedule entry within the occupant.</summary>
    public string Id { get; }

    /// <summary>Whether the scheduler should materialize this declaration as an active trigger.</summary>
    public bool IsActive { get; }

    /// <summary>The cron expression, interpreted in the position timezone.</summary>
    public string Cron { get; }

    /// <summary>The priority wire value carried by the resulting scheduled Pulse.</summary>
    public string Priority { get; }

    /// <summary>Whether the schedule is critical and may use catch-up policy.</summary>
    public bool IsCritical { get; }

    /// <summary>The catch-up policy wire value for missed windows.</summary>
    public string CatchUp { get; }

    /// <summary>The instruction the occupant runs when the schedule fires.</summary>
    public string Instruction { get; }
}
