using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Infrastructure.Auditing.PostgreSql;
using Npgsql;
using System.Text.Json;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlDirectiveAuditExportStoreTests(PostgreSqlFixture fixture)
{
    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("bug-triage");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000316"));
    private static readonly DirectiveId Directive =
        DirectiveId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000316"));
    private static readonly DateTimeOffset At =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Store_pages_scoped_audit_events_and_releases_result_only_after_terminal_state()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAuditAsync(dataSource);
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
        var auditLog = new PostgreSqlJourneyAuditLog(dataSource);
        var store = new PostgreSqlDirectiveAuditExportStore(dataSource);
        await store.StoreAsync(new DirectiveAuditExportResultCaptureData(
            new DirectiveAuditExportResultData(
                Organization,
                Thread,
                Directive,
                Position,
                "Report",
                1,
                """{"schema_version":1,"type":"Report"}""")));
        auditLog.Append(Record(
            1,
            JourneyAuditStage.SubmissionReceived,
            JourneyAuditOutcome.Accepted));
        auditLog.Append(Record(
            2,
            JourneyAuditStage.GatewayCostRecorded,
            JourneyAuditOutcome.Succeeded));

        var active = await store.ReadAsync(
            Organization,
            Thread,
            Directive,
            0,
            100);

        Assert.False(active.IsTerminal);
        Assert.Null(active.Result);
        Assert.Equal(2, active.Events.Length);

        auditLog.Append(Record(
            3,
            JourneyAuditStage.ResultMessageCreated,
            JourneyAuditOutcome.Succeeded));

        var firstPage = await store.ReadAsync(
            Organization,
            Thread,
            Directive,
            0,
            2);
        var secondPage = await store.ReadAsync(
            Organization,
            Thread,
            Directive,
            firstPage.NextAfterSequence,
            2);

        Assert.True(firstPage.IsTerminal);
        Assert.Equal(2, firstPage.Events.Length);
        Assert.NotNull(firstPage.Result);
        Assert.True(secondPage.IsTerminal);
        Assert.Single(secondPage.Events);
        Assert.Equal(
            JourneyAuditStage.ResultMessageCreated,
            secondPage.Events[0].Record.Stage);
        using var content = JsonDocument.Parse(firstPage.Result.Content);
        Assert.Equal("Report", content.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, content.RootElement.GetProperty("schema_version").GetInt32());
    }

    [Fact]
    public async Task Store_persists_only_the_observation_from_a_superseded_accepted_result()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAuditAsync(dataSource);
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
        var auditLog = new PostgreSqlJourneyAuditLog(dataSource);
        var store = new PostgreSqlDirectiveAuditExportStore(dataSource);
        const string privateContent = "Private triage assessment.";
        var acceptedReport = JsonSerializer.Serialize(new
        {
            Body = privateContent + "\n" +
                "hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"medium\"],\"missing-information\":[\"environment\"]}}",
        });
        await store.StoreAsync(new DirectiveAuditExportResultCaptureData(
            new DirectiveAuditExportResultData(
                Organization,
                Thread,
                Directive,
                Position,
                "Escalation",
                1,
                """{"Context":"Authoritative fail-safe."}"""),
            new DirectiveAuditExportMessageData("Report", 1, acceptedReport)));
        auditLog.Append(Record(
            1,
            JourneyAuditStage.ResultMessageCreated,
            JourneyAuditOutcome.Succeeded));

        var page = await store.ReadAsync(
            Organization,
            Thread,
            Directive,
            0,
            100);

        Assert.NotNull(page.Result);
        Assert.Equal("Escalation", page.Result.MessageType);
        Assert.DoesNotContain(privateContent, page.Result.Content, StringComparison.Ordinal);
        Assert.NotNull(page.Result.AcceptedObservation);
        Assert.Equal(
            "{\"dimensions\":{\"missing-information\":[\"environment\"],\"severity\":[\"medium\"]}}",
            page.Result.AcceptedObservation.Content);
        Assert.DoesNotContain(
            privateContent,
            page.Result.AcceptedObservation.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "hive-evaluation-v1",
            page.Result.AcceptedObservation.Content,
            StringComparison.Ordinal);
    }

    private static JourneyAuditRecord Record(
        int discriminator,
        JourneyAuditStage stage,
        JourneyAuditOutcome outcome) =>
        new(
            Guid.Parse($"dddddddd-0000-0000-0000-{discriminator:000000000000}"),
            At.AddMilliseconds(discriminator),
            stage,
            outcome,
            Organization,
            Thread,
            MessageId.From(Guid.Parse(
                $"eeeeeeee-0000-0000-0000-{discriminator:000000000000}")),
            Directive,
            Position,
            messageType: stage == JourneyAuditStage.ResultMessageCreated
                ? "Report"
                : null);

    private static async Task ResetAuditAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "DROP SCHEMA IF EXISTS audit CASCADE;");
        await command.ExecuteNonQueryAsync();
    }
}
