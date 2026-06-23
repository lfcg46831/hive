namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// The kind of occupant filling a position (<c>positions[].occupant.type</c>, §4.8/§6.2): an
/// autonomous <see cref="AiAgent"/> or a <see cref="Human"/> represented in the structure.
/// </summary>
public enum OccupantType
{
    /// <summary>An AI agent occupant configured with the §6.2 runtime block.</summary>
    AiAgent,

    /// <summary>A human occupant represented in the organizational structure.</summary>
    Human,
}
