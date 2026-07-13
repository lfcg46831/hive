namespace Hive.Domain.Identity;

/// <summary>
/// Creates the stable identity of the AI occupant configured for a position.
/// </summary>
/// <remarks>
/// This contract only derives identity. Initial materialization, persistence and dispatch belong
/// to <c>US-F0-13-T07b</c>; human occupants require an authenticated identity and must never use
/// this factory.
/// </remarks>
public static class ConfiguredAiOccupantIdentity
{
    public const string Prefix = "configured-ai:";

    /// <summary>
    /// Returns <c>configured-ai:&lt;OrganizationId&gt;/&lt;PositionId&gt;</c> for the supplied position.
    /// </summary>
    public static OccupantId For(PositionEntityId position)
    {
        ArgumentNullException.ThrowIfNull(position);
        return OccupantId.From($"{Prefix}{position.Value}");
    }

    /// <summary>
    /// Returns the configured AI occupant identity after validating the canonical position key.
    /// </summary>
    public static OccupantId For(
        OrganizationId organization,
        PositionId position) =>
        For(PositionEntityId.From(organization, position));
}
