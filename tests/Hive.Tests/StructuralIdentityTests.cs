using System.Reflection;
using Hive.Domain.Identity;

namespace Hive.Tests;

public sealed class StructuralIdentityTests
{
    [Fact]
    public void From_preserves_valid_structural_values()
    {
        Assert.Equal("acme", OrganizationId.From("acme").Value);
        Assert.Equal("engineering", UnitId.From("engineering").Value);
        Assert.Equal("bug-triage", PositionId.From("bug-triage").Value);
        Assert.Equal("agent-primary", OccupantId.From("agent-primary").Value);
    }

    [Fact]
    public void Structural_ids_compare_by_type_and_ordinal_value()
    {
        Assert.Equal(OrganizationId.From("acme"), OrganizationId.From("acme"));
        Assert.NotEqual(OrganizationId.From("acme"), OrganizationId.From("ACME"));
        Assert.NotEqual<object>(OrganizationId.From("acme"), UnitId.From("acme"));
    }

    [Fact]
    public void Structural_ids_reject_null()
    {
        foreach (var factory in StructuralFactories())
        {
            Assert.Throws<ArgumentNullException>(() => factory(null!));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(" acme")]
    [InlineData("acme ")]
    public void Structural_ids_reject_non_canonical_values(string value)
    {
        foreach (var factory in StructuralFactories())
        {
            Assert.Throws<ArgumentException>(() => factory(value));
        }
    }

    [Fact]
    public void Structural_ids_render_the_canonical_value()
    {
        Assert.Equal("acme", OrganizationId.From("acme").ToString());
        Assert.Equal("engineering", UnitId.From("engineering").ToString());
        Assert.Equal("bug-triage", PositionId.From("bug-triage").ToString());
        Assert.Equal("agent-primary", OccupantId.From("agent-primary").ToString());
    }

    [Fact]
    public void Structural_ids_expose_no_implicit_conversions()
    {
        foreach (var type in StructuralTypes())
        {
            Assert.DoesNotContain(
                type.GetMethods(BindingFlags.Public | BindingFlags.Static),
                method => method.Name == "op_Implicit");
        }
    }

    private static IEnumerable<Func<string, object>> StructuralFactories()
    {
        yield return value => OrganizationId.From(value);
        yield return value => UnitId.From(value);
        yield return value => PositionId.From(value);
        yield return value => OccupantId.From(value);
    }

    private static Type[] StructuralTypes() =>
    [
        typeof(OrganizationId),
        typeof(UnitId),
        typeof(PositionId),
        typeof(OccupantId),
    ];
}
