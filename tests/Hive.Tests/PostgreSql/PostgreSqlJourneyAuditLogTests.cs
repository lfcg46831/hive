using System.Text.Json;
using Hive.Domain.Auditing;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Auditing.PostgreSql;
using Npgsql;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlJourneyAuditLogTests(PostgreSqlFixture fixture)
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001901"));
    private static readonly DirectiveId Directive =
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001901"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001901"));
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 7, 8, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migration_creates_journey_audit_schema_and_is_idempotent()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAuditAsync(dataSource);
        var migrator = new PostgreSqlJourneyAuditLogMigrator(dataSource);

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        var tableNames = new List<string>();
        await using (var command = dataSource.CreateCommand(
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'audit'
            ORDER BY table_name;
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tableNames.Add(reader.GetString(0));
            }
        }

        var appliedVersions = new List<int>();
        await using (var command = dataSource.CreateCommand(
            "SELECT version FROM audit.schema_migrations ORDER BY version;"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                appliedVersions.Add(reader.GetInt32(0));
            }
        }

        Assert.Equal(["journey_events", "schema_migrations"], tableNames);
        Assert.Equal([1], appliedVersions);
    }

    [Fact]
    public async Task Store_appends_redacted_journey_records_and_reads_by_thread_and_directive()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAuditAsync(dataSource);
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
        var auditLog = new PostgreSqlJourneyAuditLog(dataSource);
        var record = Record(
            JourneyAuditStage.GatewayCostRecorded,
            JourneyAuditOutcome.Succeeded,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["redactions"] = "gateway.request.content,gateway.response.text",
                ["summary"] = "provider metadata only",
            },
            provider: new AiProviderMetadata("stub", "bug-triage"),
            usage: new AiTokenUsage(12, 18, 30, isEstimated: true),
            cost: new AiCostMetadata(0.00042m, "USD", isEstimated: true),
            latency: TimeSpan.FromMilliseconds(312));

        auditLog.Append(record);

        var records = auditLog.ReadByThread(Thread, Directive);
        var reloaded = Assert.Single(records);
        var payloadJson = JsonSerializer.Serialize(reloaded.Payload);

        Assert.Equal(record.AuditEventId, reloaded.AuditEventId);
        Assert.Equal(JourneyAuditStage.GatewayCostRecorded, reloaded.Stage);
        Assert.Equal(JourneyAuditOutcome.Succeeded, reloaded.Outcome);
        Assert.Equal(Organization, reloaded.OrganizationId);
        Assert.Equal(Thread, reloaded.ThreadId);
        Assert.Equal(Directive, reloaded.DirectiveId);
        Assert.Equal(Message, reloaded.MessageId);
        Assert.Equal(Position, reloaded.PositionId);
        Assert.Equal("stub", reloaded.Provider?.ProviderId);
        Assert.Equal("bug-triage", reloaded.Provider?.ModelId);
        Assert.Equal(30, reloaded.Usage?.TotalTokens);
        Assert.Equal(0.00042m, reloaded.Cost?.Amount);
        Assert.Equal(TimeSpan.FromMilliseconds(312), reloaded.Latency);
        Assert.Contains("gateway.request.content", payloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Customer reports checkout failures", payloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Store_deduplicates_duplicate_suppression_observations_by_audit_event_id()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAuditAsync(dataSource);
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
        var auditLog = new PostgreSqlJourneyAuditLog(dataSource);
        var first = DuplicateSuppressionRecord(OccurredAt);
        var repeated = DuplicateSuppressionRecord(OccurredAt.AddSeconds(5));

        auditLog.Append(first);
        auditLog.Append(repeated);

        var reloaded = Assert.Single(auditLog.ReadByThread(Thread, Directive));
        Assert.Equal(first.AuditEventId, reloaded.AuditEventId);
        Assert.Equal(JourneyAuditStage.DuplicateSuppressed, reloaded.Stage);
        Assert.Equal(JourneyAuditOutcome.Rejected, reloaded.Outcome);
        Assert.Equal("terminal-result-already-materialized", reloaded.ReasonCode);
        Assert.Equal("ResultMessageCreated", reloaded.Payload["suppressedStage"]);
        Assert.DoesNotContain(
            "Customer reports checkout failures",
            JsonSerializer.Serialize(reloaded.Payload),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Store_round_trips_and_deduplicates_action_gate_evaluations()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAuditAsync(dataSource);
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
        var auditLog = new PostgreSqlJourneyAuditLog(dataSource);
        var first = ActionGateRecord(OccurredAt);
        var repeated = ActionGateRecord(OccurredAt.AddSeconds(5));

        auditLog.Append(first);
        auditLog.Append(repeated);

        var reloaded = Assert.Single(auditLog.ReadByThread(Thread, Directive));
        Assert.Equal(first.AuditEventId, reloaded.AuditEventId);
        Assert.Equal(JourneyAuditStage.ActionGateEvaluated, reloaded.Stage);
        Assert.Equal(JourneyAuditOutcome.Rejected, reloaded.Outcome);
        Assert.Equal("action-gate-unmatched-action-default", reloaded.ReasonCode);
        Assert.Equal("retained-for-escalation", reloaded.Payload["gateOutcome"]);
        Assert.Equal("escalate", reloaded.Payload["effectiveGate"]);
        Assert.Equal("acting-under-invalid", reloaded.Payload["actingUnderCode"]);
        Assert.DoesNotContain(
            "finance.commitments",
            JsonSerializer.Serialize(reloaded.Payload),
            StringComparison.Ordinal);
    }

    private static JourneyAuditRecord Record(
        JourneyAuditStage stage,
        JourneyAuditOutcome outcome,
        IReadOnlyDictionary<string, string>? payload = null,
        AiProviderMetadata? provider = null,
        AiTokenUsage? usage = null,
        AiCostMetadata? cost = null,
        TimeSpan? latency = null) =>
        new(
            Guid.Parse("99999999-0000-0000-0000-000000001901"),
            OccurredAt,
            stage,
            outcome,
            Organization,
            Thread,
            Message,
            directiveId: Directive,
            positionId: Position,
            provider: provider,
            usage: usage,
            cost: cost,
            latency: latency,
            payload: payload);

    private static JourneyAuditRecord DuplicateSuppressionRecord(DateTimeOffset occurredAt) =>
        JourneyAuditRecord.Create(
            JourneyAuditStage.DuplicateSuppressed,
            JourneyAuditOutcome.Rejected,
            Organization,
            Thread,
            Message,
            Directive,
            Position,
            reasonCode: "terminal-result-already-materialized",
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["suppressedStage"] = "ResultMessageCreated",
                ["suppressedOutcome"] = "Succeeded",
                ["redactions"] = "directive.objective,directive.context,gateway.response.text",
            },
            occurredAtUtc: occurredAt,
            idempotencyDiscriminator: "terminal-result-already-materialized");

    private static JourneyAuditRecord ActionGateRecord(DateTimeOffset occurredAt) =>
        JourneyAuditRecord.Create(
            JourneyAuditStage.ActionGateEvaluated,
            JourneyAuditOutcome.Rejected,
            Organization,
            Thread,
            Message,
            Directive,
            Position,
            reasonCode: "action-gate-unmatched-action-default",
            messageType: "Report",
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["actionKind"] = "organizational-message",
                ["actionSelector"] = "Report",
                ["actionInstanceDigest"] = "sha256:3fd7f45f",
                ["gateOutcome"] = "retained-for-escalation",
                ["effectiveGate"] = "escalate",
                ["actingUnderState"] = "invalid",
                ["actingUnderCode"] = "acting-under-invalid",
                ["redactions"] = "action.message.payload:omitted,acting_under.raw:discarded",
            },
            occurredAtUtc: occurredAt,
            idempotencyDiscriminator:
                "action-gate:v1|organizational-message|Report|sha256:3fd7f45f|retained-for-escalation|action-gate-unmatched-action-default|acting-under-invalid");

    private static async Task ResetAuditAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("DROP SCHEMA IF EXISTS audit CASCADE;");
        await command.ExecuteNonQueryAsync();
    }
}
