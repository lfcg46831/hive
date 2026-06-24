using Hive.Actors.Sharding;
using Hive.Domain.Identity;
using Hive.Domain.Positions;

namespace Hive.Tests;

/// <summary>
/// Verifies the sharded-message envelope contract (US-F0-06-T04a): it pairs a destination
/// <see cref="PositionEntityId"/> with an address-free <see cref="PositionCommand"/> and rejects
/// incomplete addressing.
/// </summary>
public sealed class PositionEnvelopeTests
{
    private static readonly PositionEntityId Position =
        PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("bug-triage"));

    [Fact]
    public void Holds_the_target_position_and_command()
    {
        var command = new RequestPassivation("idle");

        var envelope = new PositionEnvelope(Position, command);

        Assert.Equal(Position, envelope.Position);
        Assert.Same(command, envelope.Command);
    }

    [Fact]
    public void For_factory_builds_an_equivalent_envelope()
    {
        var command = new RequestPassivation();

        var fromCtor = new PositionEnvelope(Position, command);
        var fromFactory = PositionEnvelope.For(Position, command);

        Assert.Equal(fromCtor, fromFactory);
    }

    [Fact]
    public void Rejects_null_position()
    {
        Assert.Throws<ArgumentNullException>(
            () => new PositionEnvelope(null!, new RequestPassivation()));
    }

    [Fact]
    public void Rejects_null_command()
    {
        Assert.Throws<ArgumentNullException>(() => new PositionEnvelope(Position, null!));
    }
}
