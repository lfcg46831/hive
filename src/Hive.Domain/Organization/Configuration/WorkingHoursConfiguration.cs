namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// The working-hours window of an occupant (§6.2 <c>occupant.working_hours</c>): the
/// <see cref="Start"/> (inclusive) and <see cref="End"/> (exclusive) local times, interpreted in the
/// position timezone. Values are stored as declared; their format and ordering are validated later.
/// </summary>
public sealed record WorkingHoursConfiguration
{
    /// <summary>Creates a window from <paramref name="start"/> (inclusive) to <paramref name="end"/> (exclusive).</summary>
    public WorkingHoursConfiguration(string start, string end)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);

        Start = start;
        End = end;
    }

    /// <summary>The inclusive local start time of the window.</summary>
    public string Start { get; }

    /// <summary>The exclusive local end time of the window.</summary>
    public string End { get; }
}
