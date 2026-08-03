using Hive.Actors.Positions;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.Auditing.PostgreSql;
using Hive.Infrastructure.Organization.ReadModels;
using Hive.Infrastructure.Organization.ReadModels.PostgreSql;
using Hive.Infrastructure.Organization.Registry.PostgreSql;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlPositionLiveStateProjectionFeedTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Position_facts_and_checkpoint_are_committed_atomically_and_resume_after_restart()
    {
        await ResetAndMigrateAsync();
        var facts = PositionFacts(offset: 11);
        await using (var feed = new PostgreSqlPositionLiveStateProjectionFeed(fixture.ConnectionString))
        {
            Assert.True(await feed.CapturePositionJournalAsync(11, facts));
            Assert.False(await feed.CapturePositionJournalAsync(11, facts));
        }

        await using var restarted = new PostgreSqlPositionLiveStateProjectionFeed(
            fixture.ConnectionString);
        var checkpoint = await restarted.ReadCheckpointAsync(
            PositionLiveStateProjectionSubscription.PositionJournal);
        var captured = await ReadCapturedFactsAsync();

        Assert.Equal(11, checkpoint);
        Assert.Equal(
            [
                ("OrganizationalMessage", 11L, "memo"),
                ("PositionEvent", 11L, "message-received"),
            ],
            captured);
    }

    [Fact]
    public async Task Failed_fact_insert_rolls_back_the_position_checkpoint()
    {
        await ResetAndMigrateAsync();
        await using (var dataSource = fixture.CreateDataSource())
        await using (var command = dataSource.CreateCommand(
            """
            CREATE FUNCTION organogram.reject_projection_fact()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'forced projection failure';
            END;
            $$;

            CREATE TRIGGER reject_projection_fact
            BEFORE INSERT ON organogram.position_state_projection_facts
            FOR EACH ROW
            EXECUTE FUNCTION organogram.reject_projection_fact();
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        await using var feed = new PostgreSqlPositionLiveStateProjectionFeed(fixture.ConnectionString);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await feed.CapturePositionJournalAsync(4, PositionFacts(offset: 4)));

        Assert.Equal(
            0,
            await feed.ReadCheckpointAsync(
                PositionLiveStateProjectionSubscription.PositionJournal));
        Assert.Empty(await ReadCapturedFactsAsync());
    }

    [Fact]
    public async Task Audit_capture_advances_its_own_checkpoint_and_continues_after_restart()
    {
        await ResetAndMigrateAsync();
        await using (var auditLog = new PostgreSqlJourneyAuditLog(fixture.ConnectionString))
        {
            auditLog.Append(AuditRecord(1));
        }

        await using (var feed = new PostgreSqlPositionLiveStateProjectionFeed(fixture.ConnectionString))
        {
            Assert.Equal(1, await feed.CaptureAuditLogBatchAsync(batchSize: 10));
            Assert.Equal(0, await feed.CaptureAuditLogBatchAsync(batchSize: 10));
        }

        await using (var auditLog = new PostgreSqlJourneyAuditLog(fixture.ConnectionString))
        {
            auditLog.Append(AuditRecord(2));
        }

        await using var restarted = new PostgreSqlPositionLiveStateProjectionFeed(
            fixture.ConnectionString);
        Assert.Equal(1, await restarted.CaptureAuditLogBatchAsync(batchSize: 10));
        Assert.Equal(
            2,
            await restarted.ReadCheckpointAsync(
                PositionLiveStateProjectionSubscription.AuditLog));

        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            """
            SELECT source, source_offset, fact_type, payload ->> 'stage'
            FROM organogram.position_state_projection_facts
            WHERE source = 'AuditLog'
            ORDER BY source_offset;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string Source, long Offset, string FactType, string PayloadStage)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
        }

        Assert.Equal(
            [
                ("AuditLog", 1L, "PositionAccepted", "PositionAccepted"),
                ("AuditLog", 2L, "PositionAccepted", "PositionAccepted"),
            ],
            rows);
    }

    [Fact]
    public async Task Applied_fact_advances_state_progress_and_watermark_atomically_once()
    {
        await ResetAndMigrateAsync();
        await InsertPositionStateAsync();
        await using var feed = new PostgreSqlPositionLiveStateProjectionFeed(
            fixture.ConnectionString);
        var facts = TaskCreatedFacts(offset: 1);
        Assert.True(await feed.CapturePositionJournalAsync(1, facts));
        var item = Assert.Single(
            await feed.ReadProjectionFactsAsync(afterSequenceId: 0, batchSize: 1));
        var correlated = new PositionLiveStateCorrelatedEvent(
            "TaskCreated",
            Guid.Parse("dce25441-c3eb-4803-a96d-3083434c2b38"),
            OccurredAt);
        var update = new PositionLiveStateProjectionUpdate(
            OrganizationId.From("acme"),
            PositionId.From("delivery-lead"),
            PositionLiveState.Working,
            OccurredAt,
            correlated);

        Assert.True(await feed.ApplyProjectionFactAsync(item, update));
        Assert.False(await feed.ApplyProjectionFactAsync(item, update));

        var progress = await feed.ReadProjectionProgressAsync();
        Assert.Equal(item.SequenceId, progress.LastAppliedSequenceId);
        Assert.Equal(OccurredAt, progress.LastEventAppliedAtUtc);
        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            """
            SELECT state.state,
                   state.sequence,
                   state.updated_at_utc,
                   state.last_event_type,
                   watermark.sequence_id,
                   watermark.last_event_applied_at_utc
            FROM organogram.position_states state
            INNER JOIN organogram.position_state_projection_watermarks watermark
                ON watermark.organization_id = state.organization_id
            WHERE state.organization_id = 'acme'
              AND state.position_id = 'delivery-lead';
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Working", reader.GetString(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(OccurredAt, reader.GetFieldValue<DateTimeOffset>(2));
        Assert.Equal("TaskCreated", reader.GetString(3));
        Assert.Equal(item.SequenceId, reader.GetInt64(4));
        Assert.Equal(OccurredAt, reader.GetFieldValue<DateTimeOffset>(5));
    }

    [Fact]
    public async Task Failed_state_update_does_not_advance_projection_progress_or_watermark()
    {
        await ResetAndMigrateAsync();
        await using var feed = new PostgreSqlPositionLiveStateProjectionFeed(
            fixture.ConnectionString);
        Assert.True(await feed.CapturePositionJournalAsync(1, TaskCreatedFacts(offset: 1)));
        var item = Assert.Single(
            await feed.ReadProjectionFactsAsync(afterSequenceId: 0, batchSize: 1));
        var update = new PositionLiveStateProjectionUpdate(
            OrganizationId.From("acme"),
            PositionId.From("delivery-lead"),
            PositionLiveState.Working,
            OccurredAt);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await feed.ApplyProjectionFactAsync(item, update));

        var progress = await feed.ReadProjectionProgressAsync();
        Assert.Equal(0, progress.LastAppliedSequenceId);
        Assert.Null(progress.LastEventAppliedAtUtc);
        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            "SELECT count(*) FROM organogram.position_state_projection_watermarks;");
        Assert.Equal(0, (long)(await command.ExecuteScalarAsync())!);
    }

    private async Task ResetAndMigrateAsync()
    {
        await fixture.ResetRegistryAsync();
        await using var dataSource = fixture.CreateDataSource();
        await using (var command = dataSource.CreateCommand("DROP SCHEMA IF EXISTS audit CASCADE;"))
        {
            await command.ExecuteNonQueryAsync();
        }

        await new PostgreSqlOrganizationRegistryMigrator(dataSource).MigrateAsync();
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
    }

    private async Task InsertPositionStateAsync()
    {
        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO organogram.position_states (
                organization_id,
                position_id,
                state,
                sequence,
                updated_at_utc)
            VALUES ('acme', 'delivery-lead', 'Idle', 0, @occurred_at_utc);
            """);
        command.Parameters.AddWithValue("occurred_at_utc", OccurredAt.AddMinutes(-1));
        await command.ExecuteNonQueryAsync();
    }

    private async Task<IReadOnlyList<(string Source, long Offset, string FactType)>>
        ReadCapturedFactsAsync()
    {
        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            """
            SELECT source, source_offset, fact_type
            FROM organogram.position_state_projection_facts
            ORDER BY source, source_offset;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string Source, long Offset, string FactType)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        }

        return rows;
    }

    private static PositionLiveStateProjectionFact[] PositionFacts(long offset)
    {
        var organizationId = OrganizationId.From("acme");
        var positionId = PositionId.From("delivery-lead");
        var messageId = MessageId.From(Guid.Parse("bb8fe744-c3f7-44c6-8316-caa54ae7f71f"));
        var threadId = ThreadId.From(Guid.Parse("dce25441-c3eb-4803-a96d-3083434c2b38"));
        return
        [
            new PositionLiveStateProjectionFact(
                PositionLiveStateProjectionSource.PositionEvent,
                offset,
                organizationId,
                "message-received",
                OccurredAt,
                "{\"message\":{}}",
                positionId,
                "position:acme/delivery-lead",
                persistenceSequence: 3,
                messageId,
                threadId),
            new PositionLiveStateProjectionFact(
                PositionLiveStateProjectionSource.OrganizationalMessage,
                offset,
                organizationId,
                "memo",
                OccurredAt,
                "{\"body\":\"Status update\"}",
                positionId,
                "position:acme/delivery-lead",
                persistenceSequence: 3,
                messageId,
                threadId),
        ];
    }

    private static PositionLiveStateProjectionFact[] TaskCreatedFacts(long offset)
    {
        var entityId = PositionEntityId.Parse("acme/delivery-lead");
        var @event = new TaskCreated(
            PositionTaskId.From(Guid.Parse("ed409772-0111-4a87-8fab-345e5a7a66f4")),
            ThreadId.From(Guid.Parse("dce25441-c3eb-4803-a96d-3083434c2b38")),
            "Triage regression",
            Priority.High,
            OccurredAt);
        return PositionLiveStateProjectionWorker.Facts(
                new PositionLiveStateProjectionJournalEvent(
                    offset,
                    "position:acme/delivery-lead",
                    persistenceSequence: 3,
                    entityId,
                    @event))
            .ToArray();
    }

    private static JourneyAuditRecord AuditRecord(int ordinal) =>
        JourneyAuditRecord.Create(
            JourneyAuditStage.PositionAccepted,
            JourneyAuditOutcome.Accepted,
            OrganizationId.From("acme"),
            ThreadId.From(Guid.Parse($"00000000-0000-0000-0000-{ordinal:D12}")),
            MessageId.From(Guid.Parse($"10000000-0000-0000-0000-{ordinal:D12}")),
            positionId: PositionId.From("delivery-lead"),
            messageType: "Memo",
            occurredAtUtc: OccurredAt.AddMinutes(ordinal),
            idempotencyDiscriminator: $"projection-test-{ordinal}");
}
