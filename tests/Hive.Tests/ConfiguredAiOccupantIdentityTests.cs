using Hive.Domain.Identity;

namespace Hive.Tests;

public sealed class ConfiguredAiOccupantIdentityTests
{
    [Fact]
    public void Creates_the_canonical_configured_AI_occupant_identity()
    {
        var occupant = ConfiguredAiOccupantIdentity.For(
            OrganizationId.From("acme-delivery"),
            PositionId.From("bug-triage"));

        Assert.Equal("configured-ai:acme-delivery/bug-triage", occupant.Value);
        Assert.Equal(occupant.Value, occupant.ToString());
    }

    [Fact]
    public void Is_deterministic_for_the_same_position_entity()
    {
        var entity = PositionEntityId.From(
            OrganizationId.From("acme-delivery"),
            PositionId.From("bug-triage"));

        Assert.Equal(
            ConfiguredAiOccupantIdentity.For(entity),
            ConfiguredAiOccupantIdentity.For(PositionEntityId.Parse(entity.Value)));
    }

    [Fact]
    public void Separates_organizations_and_positions()
    {
        var first = ConfiguredAiOccupantIdentity.For(
            OrganizationId.From("acme-delivery"),
            PositionId.From("bug-triage"));
        var otherOrganization = ConfiguredAiOccupantIdentity.For(
            OrganizationId.From("other-delivery"),
            PositionId.From("bug-triage"));
        var otherPosition = ConfiguredAiOccupantIdentity.For(
            OrganizationId.From("acme-delivery"),
            PositionId.From("incident-triage"));

        Assert.NotEqual(first, otherOrganization);
        Assert.NotEqual(first, otherPosition);
        Assert.NotEqual(otherOrganization, otherPosition);
    }

    [Fact]
    public void Rejects_null_identity_components()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ConfiguredAiOccupantIdentity.For((PositionEntityId)null!));
        Assert.Throws<ArgumentNullException>(() =>
            ConfiguredAiOccupantIdentity.For(null!, PositionId.From("bug-triage")));
        Assert.Throws<ArgumentNullException>(() =>
            ConfiguredAiOccupantIdentity.For(OrganizationId.From("acme-delivery"), null!));
    }

    [Fact]
    public void Rejects_ambiguous_position_segments()
    {
        Assert.Throws<ArgumentException>(() =>
            ConfiguredAiOccupantIdentity.For(
                OrganizationId.From("acme/delivery"),
                PositionId.From("bug-triage")));
        Assert.Throws<ArgumentException>(() =>
            ConfiguredAiOccupantIdentity.For(
                OrganizationId.From("acme-delivery"),
                PositionId.From("bug/triage")));
    }
}
