namespace Hive.Domain.Identity;

/// <summary>
/// The sharded identity of a <c>PositionActor</c>: the stable, addressable key that the runtime
/// uses to route messages to a position entity across cluster nodes. The contract is
/// <c>entityId = OrganizationId/PositionId</c>, where '/' is the single reserved separator.
/// </summary>
/// <remarks>
/// This is a pure domain contract: it fixes the identity, its canonical textual form and the
/// stable entity type name, but does not configure Akka.Cluster Sharding. The shard message
/// extractor/resolver and the sharded-message serialization conventions belong to
/// <c>US-F0-06-T04a</c>; serializer wiring belongs to <c>US-F0-06-T05b</c>.
/// </remarks>
public sealed record PositionEntityId
{
    /// <summary>
    /// The stable Cluster Sharding entity type name for positions. Changing it is a disruptive
    /// schema change (it would orphan persisted entities), so it is fixed by this contract.
    /// </summary>
    public const string EntityTypeName = "position";

    /// <summary>The single reserved separator between the organization and position segments.</summary>
    public const char Separator = '/';

    private PositionEntityId(OrganizationId organization, PositionId position)
    {
        Organization = organization;
        Position = position;
    }

    public OrganizationId Organization { get; }

    public PositionId Position { get; }

    /// <summary>The canonical textual form <c>OrganizationId/PositionId</c>.</summary>
    public string Value => $"{Organization.Value}{Separator}{Position.Value}";

    /// <summary>
    /// Builds the entity id from its components. Both segments must already be valid structural
    /// identities and must not contain the reserved separator, otherwise the id would not round-trip.
    /// </summary>
    public static PositionEntityId From(OrganizationId organization, PositionId position)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(position);

        IdentityValue.RequireWithout(organization.Value, Separator, nameof(organization));
        IdentityValue.RequireWithout(position.Value, Separator, nameof(position));

        return new PositionEntityId(organization, position);
    }

    /// <summary>
    /// Parses the canonical textual form back into its components, reusing the structural identity
    /// factories so every domain invariant is enforced. Anything other than exactly one separator
    /// with two non-empty segments is rejected; no silent normalization is performed.
    /// </summary>
    public static PositionEntityId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var separatorIndex = value.IndexOf(Separator);
        if (separatorIndex < 0)
        {
            throw new ArgumentException(
                $"A position entity id must contain the '{Separator}' separator.",
                nameof(value));
        }

        if (value.IndexOf(Separator, separatorIndex + 1) >= 0)
        {
            throw new ArgumentException(
                $"A position entity id must contain exactly one '{Separator}' separator.",
                nameof(value));
        }

        var organization = OrganizationId.From(value[..separatorIndex]);
        var position = PositionId.From(value[(separatorIndex + 1)..]);

        return new PositionEntityId(organization, position);
    }

    /// <summary>
    /// Tries to parse the canonical textual form. Returns <see langword="false"/> for any invalid
    /// input instead of throwing.
    /// </summary>
    public static bool TryParse(string? value, out PositionEntityId? result)
    {
        if (value is not null)
        {
            try
            {
                result = Parse(value);
                return true;
            }
            catch (ArgumentException)
            {
                // Falls through to the failure path below.
            }
        }

        result = null;
        return false;
    }

    public override string ToString() => Value;
}
