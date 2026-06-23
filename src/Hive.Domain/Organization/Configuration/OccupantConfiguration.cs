namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// The interior of a position (§4.8 <c>positions[].occupant</c>, reusing the §6.2 contract): the
/// occupant <see cref="Type"/>, the optional <see cref="IdentityPromptRef"/>, the
/// <see cref="Ai"/> runtime configuration (only meaningful for an <see cref="OccupantType.AiAgent"/>),
/// the optional <see cref="WorkingHours"/> window, the position <see cref="Authority"/>, the declared
/// proactivity (<see cref="Schedule"/>), the event reactions (<see cref="Subscriptions"/>) and the
/// authorized <see cref="Tools"/>. Optional sections are <see langword="null"/> or empty when absent;
/// semantic consistency (for example an AI block only on an AI occupant) is validated later.
/// </summary>
public sealed record OccupantConfiguration
{
    /// <summary>Creates an occupant of <paramref name="type"/> with optional §6.2 sections.</summary>
    public OccupantConfiguration(
        OccupantType type,
        string? identityPromptRef = null,
        AiConfiguration? ai = null,
        WorkingHoursConfiguration? workingHours = null,
        AuthorityConfiguration? authority = null,
        IReadOnlyList<ScheduleEntryConfiguration>? schedule = null,
        IReadOnlyList<SubscriptionConfiguration>? subscriptions = null,
        IReadOnlyList<ToolConfiguration>? tools = null)
    {
        Type = type;
        IdentityPromptRef = identityPromptRef;
        Ai = ai;
        WorkingHours = workingHours;
        Authority = authority;
        Schedule = schedule ?? Array.Empty<ScheduleEntryConfiguration>();
        Subscriptions = subscriptions ?? Array.Empty<SubscriptionConfiguration>();
        Tools = tools ?? Array.Empty<ToolConfiguration>();
    }

    /// <summary>Whether the occupant is an AI agent or a human.</summary>
    public OccupantType Type { get; }

    /// <summary>The identity prompt reference into the <c>prompts</c> catalog, when declared.</summary>
    public string? IdentityPromptRef { get; }

    /// <summary>The AI runtime configuration, present for AI occupants.</summary>
    public AiConfiguration? Ai { get; }

    /// <summary>The optional working-hours window of the occupant.</summary>
    public WorkingHoursConfiguration? WorkingHours { get; }

    /// <summary>The decision authority of the position, when declared.</summary>
    public AuthorityConfiguration? Authority { get; }

    /// <summary>The declared proactive jobs in declaration order; empty when none.</summary>
    public IReadOnlyList<ScheduleEntryConfiguration> Schedule { get; }

    /// <summary>The event subscriptions in declaration order; empty when none.</summary>
    public IReadOnlyList<SubscriptionConfiguration> Subscriptions { get; }

    /// <summary>The authorized tools/connectors in declaration order; empty when none.</summary>
    public IReadOnlyList<ToolConfiguration> Tools { get; }
}
