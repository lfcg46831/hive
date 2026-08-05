using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Auditing.PostgreSql;
using Hive.Infrastructure.Inbox.ReadModels;
using Hive.Infrastructure.Inbox.ReadModels.PostgreSql;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlInboxProjectionFeedTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migration_is_versioned_idempotent_and_owns_the_inbox_schema()
    {
        await fixture.ResetInboxAsync();
        await using var dataSource = fixture.CreateDataSource();
        var migrator = new PostgreSqlInboxProjectionMigrator(dataSource);

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        var tableNames = new List<string>();
        await using (var command = dataSource.CreateCommand(
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'inbox'
            ORDER BY table_name;
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tableNames.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
            [
                "items",
                "projection_checkpoints",
                "projection_facts",
                "projection_progress",
                "projection_watermarks",
                "schema_migrations",
            ],
            tableNames);
        var appliedVersions = new List<int>();
        await using var versions = dataSource.CreateCommand(
            "SELECT version FROM inbox.schema_migrations ORDER BY version;");
        await using (var reader = await versions.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                appliedVersions.Add(reader.GetInt32(0));
            }
        }

        Assert.Equal([1, 2], appliedVersions);
    }

    [Fact]
    public async Task Position_facts_and_checkpoint_commit_atomically_and_resume_after_restart()
    {
        await ResetAndMigrateAsync();
        var facts = PositionFacts(offset: 11);
        await using (var feed = new PostgreSqlInboxProjectionFeed(fixture.ConnectionString))
        {
            Assert.True(await feed.CapturePositionJournalAsync(11, facts));
            Assert.False(await feed.CapturePositionJournalAsync(11, facts));
        }

        await using var restarted = new PostgreSqlInboxProjectionFeed(fixture.ConnectionString);
        var checkpoint = await restarted.ReadCheckpointAsync(
            InboxProjectionSubscription.PositionJournal);
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
            CREATE FUNCTION inbox.reject_projection_fact()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'forced projection failure';
            END;
            $$;

            CREATE TRIGGER reject_projection_fact
            BEFORE INSERT ON inbox.projection_facts
            FOR EACH ROW
            EXECUTE FUNCTION inbox.reject_projection_fact();
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        await using var feed = new PostgreSqlInboxProjectionFeed(fixture.ConnectionString);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await feed.CapturePositionJournalAsync(4, PositionFacts(offset: 4)));

        Assert.Equal(
            0,
            await feed.ReadCheckpointAsync(InboxProjectionSubscription.PositionJournal));
        Assert.Empty(await ReadCapturedFactsAsync());
    }

    [Fact]
    public async Task Audit_capture_advances_its_checkpoint_and_continues_after_restart()
    {
        await ResetAndMigrateAsync();
        await using (var auditLog = new PostgreSqlJourneyAuditLog(fixture.ConnectionString))
        {
            auditLog.Append(AuditRecord(1));
        }

        await using (var feed = new PostgreSqlInboxProjectionFeed(fixture.ConnectionString))
        {
            Assert.Equal(1, await feed.CaptureAuditLogBatchAsync(batchSize: 10));
            Assert.Equal(0, await feed.CaptureAuditLogBatchAsync(batchSize: 10));
        }

        await using (var auditLog = new PostgreSqlJourneyAuditLog(fixture.ConnectionString))
        {
            auditLog.Append(AuditRecord(2));
        }

        await using var restarted = new PostgreSqlInboxProjectionFeed(fixture.ConnectionString);
        Assert.Equal(1, await restarted.CaptureAuditLogBatchAsync(batchSize: 10));
        Assert.Equal(
            2,
            await restarted.ReadCheckpointAsync(InboxProjectionSubscription.AuditLog));

        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            """
            SELECT source, source_offset, fact_type, payload ->> 'stage'
            FROM inbox.projection_facts
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
    public async Task Projection_application_is_idempotent_and_exposes_the_organization_watermark()
    {
        await ResetAndMigrateAsync();
        var organizationId = OrganizationId.From("acme");
        var positionId = PositionId.From("delivery-lead");
        var facts = PositionFacts(offset: 11);
        await using (var feed = new PostgreSqlInboxProjectionFeed(fixture.ConnectionString))
        {
            Assert.True(await feed.CapturePositionJournalAsync(11, facts));
            var captured = await feed.ReadProjectionFactsAsync(0, batchSize: 10);
            var positionEvent = Assert.Single(
                captured,
                item => item.Fact.Source == InboxProjectionSource.PositionEvent);
            var message = Assert.Single(
                captured,
                item => item.Fact.Source == InboxProjectionSource.OrganizationalMessage);
            var change = MaterializedChange(organizationId, positionId, message.Fact);

            Assert.True(await feed.ApplyProjectionFactAsync(positionEvent, []));
            Assert.True(await feed.ApplyProjectionFactAsync(message, [change]));
            Assert.False(await feed.ApplyProjectionFactAsync(message, [change]));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await feed.ApplyProjectionChangesAsync(
                    positionEvent.SequenceId,
                    [change]));

            var progress = await feed.ReadProjectionProgressAsync();
            Assert.Equal(message.SequenceId, progress.LastAppliedSequenceId);
            Assert.Equal(OccurredAt, progress.LastEventAppliedAtUtc);
        }

        await using var snapshotReader = new PostgreSqlInboxProjectionSnapshotReader(
            fixture.ConnectionString);
        var snapshot = await snapshotReader.ReadAsync(organizationId, [positionId]);
        var item = Assert.Single(snapshot.Items);

        Assert.Equal(OccurredAt, snapshot.LastEventAppliedAtUtc);
        Assert.Equal(positionId, item.Key.AssignedPositionId);
        Assert.Equal(InboxProjectionMessageType.Memo, item.Type);
        Assert.Equal(InboxProjectionResponseState.NotApplicable, item.ResponseState);

        var otherPosition = await snapshotReader.ReadAsync(
            organizationId,
            [PositionId.From("engineer")]);
        Assert.Empty(otherPosition.Items);
        Assert.Equal(OccurredAt, otherPosition.LastEventAppliedAtUtc);
    }

    [Fact]
    public async Task Failed_item_materialization_rolls_back_progress_and_watermark()
    {
        await ResetAndMigrateAsync();
        var organizationId = OrganizationId.From("acme");
        var positionId = PositionId.From("delivery-lead");
        await using var feed = new PostgreSqlInboxProjectionFeed(fixture.ConnectionString);
        Assert.True(await feed.CapturePositionJournalAsync(11, PositionFacts(offset: 11)));
        var captured = await feed.ReadProjectionFactsAsync(0, batchSize: 10);
        var positionEvent = Assert.Single(
            captured,
            item => item.Fact.Source == InboxProjectionSource.PositionEvent);
        var message = Assert.Single(
            captured,
            item => item.Fact.Source == InboxProjectionSource.OrganizationalMessage);
        Assert.True(await feed.ApplyProjectionFactAsync(positionEvent, []));

        await using (var dataSource = fixture.CreateDataSource())
        await using (var command = dataSource.CreateCommand(
            """
            CREATE FUNCTION inbox.reject_item_materialization()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'forced item materialization failure';
            END;
            $$;

            CREATE TRIGGER reject_item_materialization
            BEFORE INSERT ON inbox.items
            FOR EACH ROW
            EXECUTE FUNCTION inbox.reject_item_materialization();
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await feed.ApplyProjectionFactAsync(
                message,
                [MaterializedChange(organizationId, positionId, message.Fact)]));

        var progress = await feed.ReadProjectionProgressAsync();
        Assert.Equal(positionEvent.SequenceId, progress.LastAppliedSequenceId);
        await using var verification = fixture.CreateDataSource();
        await using var verify = verification.CreateCommand(
            """
            SELECT
                (SELECT sequence_id
                 FROM inbox.projection_watermarks
                 WHERE organization_id = 'acme'),
                (SELECT count(*) FROM inbox.items);
            """);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(positionEvent.SequenceId, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
    }

    private async Task ResetAndMigrateAsync()
    {
        await fixture.ResetInboxAsync();
        await using var dataSource = fixture.CreateDataSource();
        await using (var command = dataSource.CreateCommand("DROP SCHEMA IF EXISTS audit CASCADE;"))
        {
            await command.ExecuteNonQueryAsync();
        }

        await new PostgreSqlInboxProjectionMigrator(dataSource).MigrateAsync();
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
    }

    private async Task<IReadOnlyList<(string Source, long Offset, string FactType)>>
        ReadCapturedFactsAsync()
    {
        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            """
            SELECT source, source_offset, fact_type
            FROM inbox.projection_facts
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

    private static InboxProjectionFact[] PositionFacts(long offset)
    {
        var organizationId = OrganizationId.From("acme");
        var positionId = PositionId.From("delivery-lead");
        var messageId = MessageId.From(Guid.Parse("bb8fe744-c3f7-44c6-8316-caa54ae7f71f"));
        var threadId = ThreadId.From(Guid.Parse("dce25441-c3eb-4803-a96d-3083434c2b38"));
        return
        [
            new InboxProjectionFact(
                InboxProjectionSource.PositionEvent,
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
            new InboxProjectionFact(
                InboxProjectionSource.OrganizationalMessage,
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

    private static InboxProjectionChange MaterializedChange(
        OrganizationId organizationId,
        PositionId positionId,
        InboxProjectionFact fact) =>
        new(
            new InboxProjectionItem(
                new InboxProjectionItemKey(
                    organizationId,
                    positionId,
                    fact.MessageId!),
                InboxProjectionMessageType.Memo,
                new PositionEndpointRef(PositionId.From("ceo")),
                new PositionEndpointRef(positionId),
                fact.ThreadId!,
                Priority.Normal,
                OccurredAt.AddMinutes(-1),
                DeadlineAtUtc: null,
                IsExpired: false,
                InboxProjectionResponseState.NotApplicable,
                Approval: null),
            fact.FactType,
            fact.OccurredAtUtc);

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
            idempotencyDiscriminator: $"inbox-projection-test-{ordinal}");
}
