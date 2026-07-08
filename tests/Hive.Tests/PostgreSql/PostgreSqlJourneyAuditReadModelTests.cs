using System.Text.Json;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Infrastructure.Auditing.PostgreSql;
using Npgsql;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlJourneyAuditReadModelTests(PostgreSqlFixture fixture)
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme-delivery");
    private static readonly OrganizationId OtherOrganization = OrganizationId.From("other-org");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001920"));
    private static readonly DirectiveId Directive =
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001920"));
    private static readonly DirectiveId OtherDirective =
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001921"));
    private static readonly PositionId Position = PositionId.From("bug-triage");
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadModel_reads_persisted_timeline_by_organization_thread_and_directive()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAuditAsync(dataSource);
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
        var auditLog = new PostgreSqlJourneyAuditLog(dataSource);
        auditLog.Append(Record(1, Organization, Thread, Directive, JourneyAuditStage.SubmissionReceived));
        auditLog.Append(Record(2, OtherOrganization, Thread, Directive, JourneyAuditStage.DirectiveCreated));
        auditLog.Append(Record(3, Organization, Thread, OtherDirective, JourneyAuditStage.PositionAccepted));
        auditLog.Append(Record(4, Organization, Thread, null, JourneyAuditStage.GatewayCalled));
        auditLog.Append(Record(
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
            provider: new AiProviderMetadata("stub", "bug-triage"),
            usage: new AiTokenUsage(14, 21, 35, isEstimated: true),
            cost: new AiCostMetadata(0.00042m, "USD", isEstimated: true),
            latency: TimeSpan.FromMilliseconds(251)));

        var recreatedReadModel = new PostgreSqlJourneyAuditReadModel(dataSource);

        var timeline = recreatedReadModel.ReadTimeline(Organization, Thread, Directive);

        Assert.Equal(Organization, timeline.OrganizationId);
        Assert.Equal(Thread, timeline.ThreadId);
        Assert.Equal(Directive, timeline.DirectiveId);
        Assert.Equal(
            [JourneyAuditStage.SubmissionReceived, JourneyAuditStage.AgentDecided],
            timeline.Entries.Select(entry => entry.Stage));
        Assert.All(timeline.Entries, entry => Assert.Equal(Directive, entry.DirectiveId));
        var agentDecision = timeline.Entries.Last();
        Assert.Equal("stub", agentDecision.Provider?.ProviderId);
        Assert.Equal("bug-triage", agentDecision.Provider?.ModelId);
        Assert.Equal(35, agentDecision.Usage?.TotalTokens);
        Assert.Equal(0.00042m, agentDecision.Cost?.Amount);
        Assert.Equal(TimeSpan.FromMilliseconds(251), agentDecision.Latency);
        Assert.Equal(
            "directive.objective,directive.context,gateway.response.text",
            agentDecision.RedactedPayload["redactions"]);
        Assert.DoesNotContain(
            "Customer reports checkout failure",
            JsonSerializer.Serialize(timeline),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadModel_without_directive_returns_all_thread_events_for_organization()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAuditAsync(dataSource);
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
        var auditLog = new PostgreSqlJourneyAuditLog(dataSource);
        auditLog.Append(Record(1, Organization, Thread, Directive, JourneyAuditStage.SubmissionReceived));
        auditLog.Append(Record(2, Organization, Thread, OtherDirective, JourneyAuditStage.PositionAccepted));
        auditLog.Append(Record(3, OtherOrganization, Thread, Directive, JourneyAuditStage.AgentDecided));

        var readModel = new PostgreSqlJourneyAuditReadModel(dataSource);

        var timeline = readModel.ReadTimeline(Organization, Thread);

        Assert.Null(timeline.DirectiveId);
        Assert.Equal(
            [JourneyAuditStage.SubmissionReceived, JourneyAuditStage.PositionAccepted],
            timeline.Entries.Select(entry => entry.Stage));
    }

    [Fact]
    public async Task ReadModel_returns_empty_timeline_when_no_events_exist()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAuditAsync(dataSource);
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
        var readModel = new PostgreSqlJourneyAuditReadModel(dataSource);

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
        AiProviderMetadata? provider = null,
        AiTokenUsage? usage = null,
        AiCostMetadata? cost = null,
        TimeSpan? latency = null) =>
        new(
            Guid.Parse($"88888888-0000-0000-0000-{id:000000000000}"),
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
            usage: usage,
            cost: cost,
            latency: latency,
            payload: payload);

    private static async Task ResetAuditAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("DROP SCHEMA IF EXISTS audit CASCADE;");
        await command.ExecuteNonQueryAsync();
    }
}
