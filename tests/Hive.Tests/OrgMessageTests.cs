using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class OrgMessageTests
{
    private static readonly DateTimeOffset SentAt =
        new(2026, 6, 21, 9, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Deadline = SentAt.AddHours(4);

    [Fact]
    public void Derived_message_preserves_every_common_envelope_value()
    {
        var id = MessageId.From(Guid.Parse("61d9c90c-2f73-4b98-9394-8107a326a849"));
        var organizationId = OrganizationId.From("acme");
        var from = new PositionEndpointRef(PositionId.From("delivery-lead"));
        var to = new PositionEndpointRef(PositionId.From("bug-triage"));
        var thread = ThreadId.From(Guid.Parse("e67184b3-8248-4d11-ab1c-00d3495ff51c"));

        var message = new TestOrgMessage(
            id,
            organizationId,
            from,
            to,
            thread,
            Priority.High,
            1,
            SentAt,
            Deadline);

        Assert.Equal(id, message.Id);
        Assert.Equal(organizationId, message.OrganizationId);
        Assert.Equal(from, message.From);
        Assert.Equal(to, message.To);
        Assert.Equal(thread, message.Thread);
        Assert.Equal(Priority.High, message.Priority);
        Assert.Equal(1, message.SchemaVersion);
        Assert.Equal(SentAt, message.SentAt);
        Assert.Equal(Deadline, message.Deadline);
    }

    [Fact]
    public void Deadline_is_optional()
    {
        var message = CreateMessage(deadline: null);

        Assert.Null(message.Deadline);
    }

    [Fact]
    public void Common_properties_are_get_only()
    {
        var propertyNames = new[]
        {
            nameof(OrgMessage.Id),
            nameof(OrgMessage.OrganizationId),
            nameof(OrgMessage.From),
            nameof(OrgMessage.To),
            nameof(OrgMessage.Thread),
            nameof(OrgMessage.Priority),
            nameof(OrgMessage.SchemaVersion),
            nameof(OrgMessage.SentAt),
            nameof(OrgMessage.Deadline),
        };

        foreach (var propertyName in propertyNames)
        {
            var property = typeof(OrgMessage).GetProperty(propertyName);

            Assert.NotNull(property);
            Assert.Null(property.SetMethod);
        }
    }

    [Fact]
    public void Constructor_rejects_null_required_references()
    {
        var id = MessageId.New();
        var organizationId = OrganizationId.From("acme");
        var from = new PositionEndpointRef(PositionId.From("delivery-lead"));
        var to = new PositionEndpointRef(PositionId.From("bug-triage"));
        var thread = ThreadId.New();

        Assert.Throws<ArgumentNullException>(
            () => new TestOrgMessage(
                null!, organizationId, from, to, thread,
                Priority.Normal, 1, SentAt, null));
        Assert.Throws<ArgumentNullException>(
            () => new TestOrgMessage(
                id, null!, from, to, thread,
                Priority.Normal, 1, SentAt, null));
        Assert.Throws<ArgumentNullException>(
            () => new TestOrgMessage(
                id, organizationId, null!, to, thread,
                Priority.Normal, 1, SentAt, null));
        Assert.Throws<ArgumentNullException>(
            () => new TestOrgMessage(
                id, organizationId, from, null!, thread,
                Priority.Normal, 1, SentAt, null));
        Assert.Throws<ArgumentNullException>(
            () => new TestOrgMessage(
                id, organizationId, from, to, null!,
                Priority.Normal, 1, SentAt, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Constructor_rejects_undefined_priority(int rawValue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateMessage(priority: (Priority)rawValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rejects_non_positive_schema_version(int schemaVersion)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateMessage(schemaVersion: schemaVersion));
    }

    private static TestOrgMessage CreateMessage(
        MessageId? id = null,
        OrganizationId? organizationId = null,
        EndpointRef? from = null,
        EndpointRef? to = null,
        ThreadId? thread = null,
        Priority priority = Priority.Normal,
        int schemaVersion = 1,
        DateTimeOffset? deadline = null) =>
        new(
            id ?? MessageId.New(),
            organizationId ?? OrganizationId.From("acme"),
            from ?? new PositionEndpointRef(PositionId.From("delivery-lead")),
            to ?? new PositionEndpointRef(PositionId.From("bug-triage")),
            thread ?? ThreadId.New(),
            priority,
            schemaVersion,
            SentAt,
            deadline);

    private sealed record TestOrgMessage : OrgMessage
    {
        public TestOrgMessage(
            MessageId id,
            OrganizationId organizationId,
            EndpointRef from,
            EndpointRef to,
            ThreadId thread,
            Priority priority,
            int schemaVersion,
            DateTimeOffset sentAt,
            DateTimeOffset? deadline)
            : base(
                id,
                organizationId,
                from,
                to,
                thread,
                priority,
                schemaVersion,
                sentAt,
                deadline)
        {
        }

        public override MessageChannel Channel => MessageChannel.System;
    }
}
