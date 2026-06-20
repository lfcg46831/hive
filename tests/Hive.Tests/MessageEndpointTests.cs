using System.Reflection;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class MessageEndpointTests
{
    [Fact]
    public void Position_endpoint_preserves_typed_identity_and_value_equality()
    {
        var positionId = PositionId.From("bug-triage");

        var first = new PositionEndpointRef(positionId);
        var second = new PositionEndpointRef(PositionId.From("bug-triage"));

        Assert.Equal(positionId, first.PositionId);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Organization_owner_endpoint_has_value_equality_without_an_extra_identifier()
    {
        Assert.Equal(
            new OrganizationOwnerEndpointRef(),
            new OrganizationOwnerEndpointRef());
    }

    [Theory]
    [InlineData(SystemEndpointKind.Scheduler)]
    [InlineData(SystemEndpointKind.DomainEvents)]
    public void System_endpoint_preserves_defined_kind(SystemEndpointKind kind)
    {
        var endpoint = new SystemEndpointRef(kind);

        Assert.Equal(kind, endpoint.Kind);
        Assert.Equal(endpoint, new SystemEndpointRef(kind));
    }

    [Fact]
    public void Endpoint_union_is_abstract_and_variants_are_sealed()
    {
        Assert.True(typeof(EndpointRef).IsAbstract);
        Assert.True(typeof(PositionEndpointRef).IsSealed);
        Assert.True(typeof(OrganizationOwnerEndpointRef).IsSealed);
        Assert.True(typeof(SystemEndpointRef).IsSealed);
    }

    [Fact]
    public void Endpoint_variants_expose_no_implicit_conversions()
    {
        var endpointTypes = new[]
        {
            typeof(PositionEndpointRef),
            typeof(OrganizationOwnerEndpointRef),
            typeof(SystemEndpointRef),
        };

        foreach (var type in endpointTypes)
        {
            Assert.DoesNotContain(
                type.GetMethods(BindingFlags.Public | BindingFlags.Static),
                method => method.Name == "op_Implicit");
        }
    }

    [Fact]
    public void System_endpoint_kinds_are_limited_to_the_F0_producers()
    {
        Assert.Equal(
            [SystemEndpointKind.Scheduler, SystemEndpointKind.DomainEvents],
            Enum.GetValues<SystemEndpointKind>());
    }

    [Fact]
    public void Position_endpoint_rejects_null_identity()
    {
        Assert.Throws<ArgumentNullException>(() => new PositionEndpointRef(null!));
    }

    [Fact]
    public void System_endpoint_rejects_undefined_kind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SystemEndpointRef((SystemEndpointKind)2));
    }
}
