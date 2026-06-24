using Hive.Domain.Identity;

namespace Hive.Tests;

/// <summary>
/// Verifies the sharded identity contract of the <c>PositionActor</c> (US-F0-06-T01):
/// <c>entityId = OrganizationId/PositionId</c>, the stable entity type name, the canonical textual
/// form and the reversible (serializable) round-trip that the wire/persisted representation relies on.
/// </summary>
public sealed class PositionEntityIdTests
{
    [Fact]
    public void Entity_type_name_is_the_stable_position_value()
    {
        Assert.Equal("position", PositionEntityId.EntityTypeName);
    }

    [Fact]
    public void From_composes_the_canonical_org_slash_position_value()
    {
        var entityId = PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("bug-triage"));

        Assert.Equal("acme", entityId.Organization.Value);
        Assert.Equal("bug-triage", entityId.Position.Value);
        Assert.Equal("acme/bug-triage", entityId.Value);
        Assert.Equal("acme/bug-triage", entityId.ToString());
    }

    [Fact]
    public void From_rejects_null_components()
    {
        Assert.Throws<ArgumentNullException>(() => PositionEntityId.From(null!, PositionId.From("p")));
        Assert.Throws<ArgumentNullException>(() => PositionEntityId.From(OrganizationId.From("o"), null!));
    }

    [Fact]
    public void From_rejects_components_that_contain_the_separator()
    {
        Assert.Throws<ArgumentException>(
            () => PositionEntityId.From(OrganizationId.From("acme/x"), PositionId.From("p")));
        Assert.Throws<ArgumentException>(
            () => PositionEntityId.From(OrganizationId.From("o"), PositionId.From("a/b")));
    }

    [Fact]
    public void Parse_reconstructs_the_components()
    {
        var entityId = PositionEntityId.Parse("acme/bug-triage");

        Assert.Equal(OrganizationId.From("acme"), entityId.Organization);
        Assert.Equal(PositionId.From("bug-triage"), entityId.Position);
    }

    [Fact]
    public void Parse_round_trips_the_canonical_value()
    {
        var original = PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("eng-lead"));

        var restored = PositionEntityId.Parse(original.Value);

        Assert.Equal(original, restored);
        Assert.Equal(original.Value, restored.Value);
    }

    [Fact]
    public void Parse_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => PositionEntityId.Parse(null!));
    }

    [Theory]
    [InlineData("acme")]
    [InlineData("acme/bug/triage")]
    [InlineData("/bug-triage")]
    [InlineData("acme/")]
    [InlineData("/")]
    [InlineData("acme/ ")]
    [InlineData(" acme/bug-triage")]
    public void Parse_rejects_malformed_values(string value)
    {
        Assert.Throws<ArgumentException>(() => PositionEntityId.Parse(value));
    }

    [Fact]
    public void TryParse_returns_true_for_a_valid_value()
    {
        var parsed = PositionEntityId.TryParse("acme/bug-triage", out var entityId);

        Assert.True(parsed);
        Assert.NotNull(entityId);
        Assert.Equal("acme/bug-triage", entityId!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("acme")]
    [InlineData("acme/bug/triage")]
    public void TryParse_returns_false_for_invalid_values(string? value)
    {
        var parsed = PositionEntityId.TryParse(value, out var entityId);

        Assert.False(parsed);
        Assert.Null(entityId);
    }

    [Fact]
    public void Equality_is_by_components()
    {
        var a = PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("p"));
        var b = PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("p"));
        var different = PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("q"));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, different);
    }

    [Fact]
    public void Equality_is_case_sensitive_and_ordinal()
    {
        var lower = PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("p"));
        var upper = PositionEntityId.From(OrganizationId.From("ACME"), PositionId.From("p"));

        Assert.NotEqual(lower, upper);
    }
}
