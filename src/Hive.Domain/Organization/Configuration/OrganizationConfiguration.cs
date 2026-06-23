namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// The typed, in-memory representation of an organization YAML document (US-F0-05-T03): the four
/// top-level blocks of §4.8 — the <see cref="Organization"/> header, the <see cref="Prompts"/>
/// catalog, the <see cref="Units"/> tree and the <see cref="Positions"/> — exactly as loaded.
/// </summary>
/// <remarks>
/// <para>
/// This model fixes the <em>shape</em> of a loaded configuration only. It is produced by the parser
/// (US-F0-05-T04) and consumed by the validators that enforce uniqueness (US-F0-05-T05),
/// cross-references (US-F0-05-T06) and structural rules (US-F0-05-T07), and by the import that
/// materializes it into the registry/read model (US-F0-05-T09).
/// </para>
/// <para>
/// It therefore performs no cross-field validation itself: the command relations of §4.1/§4.2 stay
/// implicit in <c>units[].leadership</c>, <c>positions[].unit</c> and <c>positions[].reports_to</c>,
/// and the collections may be empty. Following §9.3, the constructors reject only missing required
/// blocks so the type can never hold a structurally meaningless document, while never applying
/// silent defaults to the data itself.
/// </para>
/// </remarks>
public sealed record OrganizationConfiguration
{
    /// <summary>Creates a loaded configuration from its four top-level blocks.</summary>
    public OrganizationConfiguration(
        OrganizationHeader organization,
        IReadOnlyList<UnitConfiguration> units,
        IReadOnlyList<PositionConfiguration> positions,
        IReadOnlyList<PromptConfiguration>? prompts = null)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(positions);

        Organization = organization;
        Units = units;
        Positions = positions;
        Prompts = prompts ?? Array.Empty<PromptConfiguration>();
    }

    /// <summary>The organization header (id, name, root unit and owner).</summary>
    public OrganizationHeader Organization { get; }

    /// <summary>The prompt catalog; empty when the document declares none.</summary>
    public IReadOnlyList<PromptConfiguration> Prompts { get; }

    /// <summary>The units of the organizational tree in declaration order.</summary>
    public IReadOnlyList<UnitConfiguration> Units { get; }

    /// <summary>The positions of the organization in declaration order.</summary>
    public IReadOnlyList<PositionConfiguration> Positions { get; }
}
