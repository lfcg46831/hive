using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Auditing;

namespace Hive.Tests;

public sealed class JourneyAuditPositionProjectionPublisherTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 8, 11, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly PositionEntityId Entity = PositionEntityId.From(Organization, Position);
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001911"));
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001911"));
    private static readonly DirectiveId Directive =
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001911"));

    [Fact]
    public void Publish_records_position_acceptance_and_dispatch_after_journal_commit_without_raw_message_text()
    {
        var audit = new RecordingJourneyAuditLog();
        var inner = new RecordingPositionProjectionPublisher();
        var publisher = new JourneyAuditPositionProjectionPublisher(audit, inner);
        var directive = DirectiveMessage();

        publisher.Publish(new PositionEventCommitted(Entity, new MessageReceived(directive, At)));
        publisher.Publish(new PositionEventCommitted(
            Entity,
            new MessageDispatched(
                Message,
                Thread,
                OccupantId.From("agent-14a"),
                OccupantType.AiAgent,
                At.AddSeconds(1))));

        Assert.Equal(2, inner.Events.Count);
        Assert.Equal(
            [JourneyAuditStage.PositionAccepted, JourneyAuditStage.PositionDispatched],
            audit.Records.Select(record => record.Stage));
        Assert.All(audit.Records, record =>
        {
            Assert.Equal(JourneyAuditOutcome.Accepted, record.Outcome);
            Assert.Equal(Organization, record.OrganizationId);
            Assert.Equal(Position, record.PositionId);
            Assert.Equal(Thread, record.ThreadId);
            Assert.Equal(Directive, record.DirectiveId);
            Assert.Equal(Message, record.MessageId);
            Assert.Equal("Directive", record.MessageType);
            Assert.DoesNotContain("Customer reports checkout failures", string.Join(" ", record.Payload.Values));
        });
    }

    private static Directive DirectiveMessage() =>
        new(
            Message,
            Organization,
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(Position),
            Thread,
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: At.AddHours(2),
            Directive,
            parentDirectiveId: null,
            objective: "Triage checkout regression",
            context: "Customer reports checkout failures.");

    private sealed class RecordingJourneyAuditLog : IJourneyAuditLog
    {
        private readonly List<JourneyAuditRecord> _records = [];

        public IReadOnlyList<JourneyAuditRecord> Records => _records;

        public void Append(JourneyAuditRecord record)
        {
            _records.Add(record);
        }

        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null) =>
            _records
                .Where(record => record.ThreadId == threadId &&
                    (directiveId is null || record.DirectiveId == directiveId))
                .ToArray();
    }

    private sealed class RecordingPositionProjectionPublisher : IPositionProjectionPublisher
    {
        private readonly List<PositionProjectionEvent> _events = [];

        public IReadOnlyList<PositionProjectionEvent> Events => _events;

        public void Publish(PositionProjectionEvent @event)
        {
            _events.Add(@event);
        }
    }
}
