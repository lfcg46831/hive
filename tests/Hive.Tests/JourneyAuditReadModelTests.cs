using System.Text.Json;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;

namespace Hive.Tests;

public sealed class JourneyAuditReadModelTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme-delivery");
    private static readonly OrganizationId OtherOrganization = OrganizationId.From("other-org");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001910"));
    private static readonly DirectiveId Directive =
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001910"));
    private static readonly DirectiveId OtherDirective =
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001911"));
    private static readonly PositionId Position = PositionId.From("bug-triage");
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 7, 8, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadTimeline_filters_by_organization_thread_and_directive_into_safe_timeline()
    {
        var auditLog = new RecordingJourneyAuditLog(
            Record(1, Organization, Thread, Directive, JourneyAuditStage.SubmissionReceived),
            Record(2, OtherOrganization, Thread, Directive, JourneyAuditStage.DirectiveCreated),
            Record(3, Organization, Thread, OtherDirective, JourneyAuditStage.PositionAccepted),
            Record(4, Organization, Thread, null, JourneyAuditStage.GatewayCalled),
            Record(
                5,
                Organization,
                Thread,
                Directive,
                JourneyAuditStage.AgentDecided,
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["decisionKind"] = "Report",
                    ["redactions"] = "directive.objective,directive.context,gateway.response.text",
                    ["safeSummary"] = "ids and codes only",
                },
                provider: new AiProviderMetadata("stub", "bug-triage")));
        var readModel = new JourneyAuditReadModel(auditLog);

        var timeline = readModel.ReadTimeline(Organization, Thread, Directive);

        Assert.Equal(Organization, timeline.OrganizationId);
        Assert.Equal(Thread, timeline.ThreadId);
        Assert.Equal(Directive, timeline.DirectiveId);
        Assert.Equal(
            [JourneyAuditStage.SubmissionReceived, JourneyAuditStage.AgentDecided],
            timeline.Entries.Select(entry => entry.Stage));
        Assert.All(timeline.Entries, entry => Assert.Equal(Directive, entry.DirectiveId));
        Assert.All(timeline.Entries, entry =>
            Assert.NotEqual(typeof(JourneyAuditRecord), entry.GetType()));
        Assert.Equal("stub", timeline.Entries.Last().Provider?.ProviderId);
        Assert.Equal(
            "directive.objective,directive.context,gateway.response.text",
            timeline.Entries.Last().RedactedPayload["redactions"]);
        Assert.DoesNotContain(
            "Customer reports checkout failure",
            JsonSerializer.Serialize(timeline),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReadTimeline_without_directive_returns_all_events_for_the_thread_in_the_organization()
    {
        var auditLog = new RecordingJourneyAuditLog(
            Record(1, Organization, Thread, Directive, JourneyAuditStage.SubmissionReceived),
            Record(2, Organization, Thread, OtherDirective, JourneyAuditStage.PositionAccepted),
            Record(3, OtherOrganization, Thread, Directive, JourneyAuditStage.AgentDecided));
        var readModel = new JourneyAuditReadModel(auditLog);

        var timeline = readModel.ReadTimeline(Organization, Thread);

        Assert.Null(timeline.DirectiveId);
        Assert.Equal(
            [JourneyAuditStage.SubmissionReceived, JourneyAuditStage.PositionAccepted],
            timeline.Entries.Select(entry => entry.Stage));
    }

    [Fact]
    public void ReadTimeline_returns_empty_timeline_for_unknown_journey()
    {
        var readModel = new JourneyAuditReadModel(new RecordingJourneyAuditLog());

        var timeline = readModel.ReadTimeline(Organization, Thread, Directive);

        Assert.Equal(Organization, timeline.OrganizationId);
        Assert.Equal(Thread, timeline.ThreadId);
        Assert.Equal(Directive, timeline.DirectiveId);
        Assert.Empty(timeline.Entries);
    }

    private static JourneyAuditRecord Record(
        int id,
        OrganizationId organization,
        ThreadId thread,
        DirectiveId? directive,
        JourneyAuditStage stage,
        IReadOnlyDictionary<string, string>? payload = null,
        AiProviderMetadata? provider = null) =>
        new(
            Guid.Parse($"99999999-0000-0000-0000-{id:000000000000}"),
            OccurredAt.AddSeconds(id),
            stage,
            JourneyAuditOutcome.Succeeded,
            organization,
            thread,
            MessageId.From(Guid.Parse($"aaaaaaaa-0000-0000-0000-{id:000000000000}")),
            directive,
            Position,
            messageType: "Directive",
            provider: provider,
            usage: new AiTokenUsage(10 + id, 20 + id, 30 + id, isEstimated: true),
            cost: new AiCostMetadata(0.001m + id, "USD", isEstimated: true),
            latency: TimeSpan.FromMilliseconds(100 + id),
            payload: payload);

    private sealed class RecordingJourneyAuditLog : IJourneyAuditLog
    {
        private readonly IReadOnlyList<JourneyAuditRecord> _records;

        public RecordingJourneyAuditLog(params JourneyAuditRecord[] records)
        {
            _records = records;
        }

        public void Append(JourneyAuditRecord record)
        {
            throw new NotSupportedException("This fixture is read-only.");
        }

        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null) =>
            _records
                .Where(record => record.ThreadId == threadId &&
                    (directiveId is null || record.DirectiveId == directiveId))
                .ToArray();
    }
}
