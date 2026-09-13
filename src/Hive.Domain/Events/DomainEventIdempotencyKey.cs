using System.Globalization;
using Hive.Domain.Identity;

namespace Hive.Domain.Events;

/// <summary>
/// Replay-safe identity of an occurrence for a subscriber. Length framing prevents delimiter
/// collisions without restricting structural identities. Trigger ID derivation belongs to T09.
/// </summary>
public sealed record DomainEventIdempotencyKey
{
    private DomainEventIdempotencyKey(string value) => Value = value;

    public string Value { get; }

    public static DomainEventIdempotencyKey From(
        OrganizationId organization,
        PositionId subscriber,
        OrganizationEventPayload payload)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(subscriber);
        ArgumentNullException.ThrowIfNull(payload);
        var components = new[] { organization.Value, subscriber.Value, OrganizationEventTypeContract.ToWireValue(payload.EventType) }
            .Concat(payload.IdentityComponents());
        return new DomainEventIdempotencyKey("hive:domain-events:occurrence:v1:" +
            string.Concat(components.Select(value => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value)));
    }

    public override string ToString() => Value;
}
