using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class SystemMessageTests
{
    private static readonly DateTimeOffset SentAt =
        new(2026, 6, 21, 11, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Pulse_preserves_payload_and_derives_system_channel()
    {
        var message = new Pulse(
            MessageId.New(), OrganizationId.From("acme"),
            new SystemEndpointRef(SystemEndpointKind.Scheduler), Position("bug-triage"),
            ThreadId.New(), Priority.Normal, 1, SentAt, null,
            "daily-triage", "{\"queue\":\"customer-reports\"}");

        Assert.Equal("daily-triage", message.ScheduleId);
        Assert.Equal("{\"queue\":\"customer-reports\"}", message.Payload);
        Assert.Equal(MessageChannel.System, message.Channel);
    }

    [Fact]
    public void EventTrigger_preserves_payload_and_derives_system_channel()
    {
        var message = new EventTrigger(
            MessageId.New(), OrganizationId.From("acme"),
            new SystemEndpointRef(SystemEndpointKind.DomainEvents), Position("delivery-lead"),
            ThreadId.New(), Priority.High, 1, SentAt, null,
            "directive-deadline-approaching", "{\"remainingMinutes\":30}");

        Assert.Equal("directive-deadline-approaching", message.EventType);
        Assert.Equal("{\"remainingMinutes\":30}", message.Payload);
        Assert.Equal(MessageChannel.System, message.Channel);
    }

    [Fact]
    public void System_payload_properties_are_get_only()
    {
        AssertGetOnly<Pulse>(
            nameof(Pulse.ScheduleId),
            nameof(Pulse.Payload),
            nameof(Pulse.Channel));
        AssertGetOnly<EventTrigger>(
            nameof(EventTrigger.EventType),
            nameof(EventTrigger.Payload),
            nameof(EventTrigger.Channel));
    }

    private static PositionEndpointRef Position(string value) =>
        new(PositionId.From(value));

    private static void AssertGetOnly<T>(params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = typeof(T).GetProperty(propertyName);

            Assert.NotNull(property);
            Assert.Null(property.SetMethod);
        }
    }
}
