using System.Text;
using Hive.Domain.Identity;
using Hive.Domain.OccupantChannels;
using Hive.Infrastructure.OccupantChannels;
using Hive.Infrastructure.OccupantChannels.PostgreSql;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlImapInboundEmailStoreTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Batch_checkpoint_and_envelopes_survive_restart_without_duplicates()
    {
        await ResetAndMigrateAsync();
        var capturedAt = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var firstBatch = Batch(7, 12, (11, "first"), (12, "second"));

        await using (var firstStore =
                     new PostgreSqlImapInboundEmailStore(fixture.ConnectionString))
        {
            var committed = await firstStore.CommitBatchAsync(null, firstBatch, capturedAt);
            Assert.True(committed.IsApplied);
            Assert.Equal(2, committed.InsertedCount);
        }

        await using var restarted =
            new PostgreSqlImapInboundEmailStore(fixture.ConnectionString);
        var checkpoint = await restarted.ReadCheckpointAsync("occupant-replies", "INBOX");
        Assert.Equal(new ImapInboundEmailCheckpoint(
            "occupant-replies",
            "INBOX",
            7,
            12), checkpoint);
        var pending = await restarted.ReadPendingAsync("occupant-replies", "INBOX", 10);
        Assert.Equal([11U, 12U], pending.Select(item => item.Uid));
        Assert.Equal(["first", "second"], pending.Select(item => Encoding.ASCII.GetString(item.RawMessage)));

        var staleRedelivery = await restarted.CommitBatchAsync(null, firstBatch, capturedAt.AddMinutes(1));
        Assert.False(staleRedelivery.IsApplied);
        Assert.Equal(2, (await restarted.ReadPendingAsync("occupant-replies", "INBOX", 10)).Count);
    }

    [Fact]
    public async Task Concurrent_failover_batches_use_compare_and_swap_and_insert_once()
    {
        await ResetAndMigrateAsync();
        var capturedAt = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        await using var first = new PostgreSqlImapInboundEmailStore(fixture.ConnectionString);
        await using var second = new PostgreSqlImapInboundEmailStore(fixture.ConnectionString);
        var initial = await first.CommitBatchAsync(null, Batch(7, 10), capturedAt);
        var expected = initial.Checkpoint!;
        var competingBatch = Batch(7, 11, (11, "once"));

        var outcomes = await Task.WhenAll(
            first.CommitBatchAsync(expected, competingBatch, capturedAt.AddSeconds(1)),
            second.CommitBatchAsync(expected, competingBatch, capturedAt.AddSeconds(1)));

        Assert.Single(outcomes.Where(outcome => outcome.IsApplied));
        Assert.Single(outcomes.Where(outcome => !outcome.IsApplied));
        var pending = await first.ReadPendingAsync("occupant-replies", "INBOX", 10);
        Assert.Single(pending);
        Assert.Equal(11U, pending[0].Uid);
        Assert.Equal(11U, (await first.ReadCheckpointAsync("occupant-replies", "INBOX"))!.LastUid);
    }

    [Fact]
    public async Task Uidvalidity_reset_opens_a_new_generation_without_overwriting_history()
    {
        await ResetAndMigrateAsync();
        var capturedAt = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        await using var store = new PostgreSqlImapInboundEmailStore(fixture.ConnectionString);
        var first = await store.CommitBatchAsync(
            null,
            Batch(7, 900, (900, "old-generation")),
            capturedAt);

        var reset = await store.CommitBatchAsync(
            first.Checkpoint,
            Batch(8, 1, (1, "new-generation")),
            capturedAt.AddMinutes(1));

        Assert.True(reset.IsApplied);
        Assert.Equal(8U, reset.Checkpoint!.UidValidity);
        Assert.Equal(1U, reset.Checkpoint.LastUid);
        var pending = await store.ReadPendingAsync("occupant-replies", "INBOX", 10);
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, item => item.UidValidity == 7 && item.Uid == 900);
        Assert.Contains(pending, item => item.UidValidity == 8 && item.Uid == 1);
    }

    [Fact]
    public async Task Admission_transitions_once_and_persists_only_typed_untrusted_acceptance_metadata()
    {
        await ResetAndMigrateAsync();
        var capturedAt = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var processedAt = capturedAt.AddMinutes(1);
        await using var store = new PostgreSqlImapInboundEmailStore(fixture.ConnectionString);
        await store.CommitBatchAsync(
            null,
            Batch(7, 2, (1, "accepted-raw"), (2, "rejected-raw")),
            capturedAt);
        var pending = await store.ReadPendingAsync("occupant-replies", "INBOX", 10);
        var acceptedEnvelope = Assert.Single(pending, item => item.Uid == 1);
        var rejectedEnvelope = Assert.Single(pending, item => item.Uid == 2);
        var claims = new OccupantChannelCorrelationTokenClaims(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            OrganizationId.From("acme"),
            PositionId.From("delivery-lead"),
            MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            ThreadId.From(Guid.Parse("33333333-3333-3333-3333-333333333333")),
            requestId: null,
            capturedAt.AddHours(-1),
            capturedAt.AddDays(1));
        var admission = new InboundOccupantEmailAdmission(
            acceptedEnvelope,
            claims,
            OccupantId.From("human:delivery-lead"),
            UserId.From(Guid.Parse("55555555-5555-5555-5555-555555555555")),
            OccupantChannelBindingId.From(
                Guid.Parse("66666666-6666-6666-6666-666666666666")),
            "approve",
            InboundOccupantEmailContentTrust.Untrusted);

        Assert.True(await store.CompleteAcceptedAsync(admission, processedAt));
        Assert.False(await store.CompleteAcceptedAsync(admission, processedAt.AddSeconds(1)));
        Assert.True(await store.CompleteRejectedAsync(
            rejectedEnvelope,
            InboundOccupantEmailFailureCode.SenderMismatch,
            processedAt));
        Assert.False(await store.CompleteRejectedAsync(
            rejectedEnvelope,
            InboundOccupantEmailFailureCode.TokenExpired,
            processedAt.AddSeconds(1)));

        Assert.Empty(await store.ReadPendingAsync("occupant-replies", "INBOX", 10));
        var persisted = Assert.Single(await store.ReadAcceptedWorkRepliesAsync(
            "occupant-replies",
            "INBOX",
            10));
        Assert.Equal(claims, persisted.Correlation);
        Assert.Equal(admission.OccupantId, persisted.OccupantId);
        Assert.Equal(admission.UserId, persisted.UserId);
        Assert.Equal(admission.BindingId, persisted.BindingId);
        Assert.Equal("approve", persisted.PlainTextReply);
        Assert.Equal(InboundOccupantEmailContentTrust.Untrusted, persisted.ContentTrust);

        var replyMessageId = MessageId.From(
            Guid.Parse("77777777-7777-7777-7777-777777777777"));
        var replyDirectiveId = DirectiveId.From(
            Guid.Parse("88888888-8888-8888-8888-888888888888"));
        var emittedAt = processedAt.AddMinutes(1);
        Assert.True(await store.CompleteWorkReplyEmittedAsync(
            persisted,
            replyMessageId,
            replyDirectiveId,
            emittedAt));
        Assert.False(await store.CompleteWorkReplyEmittedAsync(
            persisted,
            replyMessageId,
            replyDirectiveId,
            emittedAt.AddSeconds(1)));
        Assert.Empty(await store.ReadAcceptedWorkRepliesAsync(
            "occupant-replies",
            "INBOX",
            10));

        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            """
            SELECT processing_state,
                   failure_code,
                   token_id,
                   organization_id,
                   position_id,
                   reply_text,
                   content_trust
            FROM occupant_channel.imap_inbound_emails
            WHERE source_id = 'occupant-replies'
              AND mailbox = 'INBOX'
              AND uid_validity = 7
              AND uid = 2;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("rejected", reader.GetString(0));
        Assert.Equal("sender-mismatch", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(reader.IsDBNull(3));
        Assert.True(reader.IsDBNull(4));
        Assert.True(reader.IsDBNull(5));
        Assert.True(reader.IsDBNull(6));

        await reader.CloseAsync();
        await using var emittedCommand = dataSource.CreateCommand(
            """
            SELECT reply_emission_state,
                   reply_message_id,
                   reply_directive_id,
                   reply_emission_at,
                   reply_emission_failure_codes
            FROM occupant_channel.imap_inbound_emails
            WHERE source_id = 'occupant-replies'
              AND mailbox = 'INBOX'
              AND uid_validity = 7
              AND uid = 1;
            """);
        await using var emittedReader = await emittedCommand.ExecuteReaderAsync();
        Assert.True(await emittedReader.ReadAsync());
        Assert.Equal("emitted", emittedReader.GetString(0));
        Assert.Equal(replyMessageId.Value, emittedReader.GetGuid(1));
        Assert.Equal(replyDirectiveId.Value, emittedReader.GetGuid(2));
        Assert.Equal(emittedAt, emittedReader.GetFieldValue<DateTimeOffset>(3));
        Assert.True(emittedReader.IsDBNull(4));
    }

    [Fact]
    public async Task Work_reply_structural_rejection_is_terminal_and_keeps_only_closed_codes()
    {
        await ResetAndMigrateAsync();
        var capturedAt = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        await using var store = new PostgreSqlImapInboundEmailStore(fixture.ConnectionString);
        await store.CommitBatchAsync(
            null,
            Batch(7, 1, (1, "accepted-raw")),
            capturedAt);
        var envelope = Assert.Single(await store.ReadPendingAsync(
            "occupant-replies",
            "INBOX",
            10));
        var admission = new InboundOccupantEmailAdmission(
            envelope,
            new OccupantChannelCorrelationTokenClaims(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                OrganizationId.From("acme"),
                PositionId.From("delivery-lead"),
                MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                ThreadId.From(Guid.Parse("33333333-3333-3333-3333-333333333333")),
                requestId: null,
                capturedAt.AddHours(-1),
                capturedAt.AddDays(1)),
            OccupantId.From("human:delivery-lead"),
            UserId.From(Guid.Parse("55555555-5555-5555-5555-555555555555")),
            OccupantChannelBindingId.From(
                Guid.Parse("66666666-6666-6666-6666-666666666666")),
            "response",
            InboundOccupantEmailContentTrust.Untrusted);
        Assert.True(await store.CompleteAcceptedAsync(admission, capturedAt.AddMinutes(1)));
        var persisted = Assert.Single(await store.ReadAcceptedWorkRepliesAsync(
            "occupant-replies",
            "INBOX",
            10));
        var replyMessageId = MessageId.From(
            Guid.Parse("77777777-7777-7777-7777-777777777777"));
        var replyDirectiveId = DirectiveId.From(
            Guid.Parse("88888888-8888-8888-8888-888888888888"));

        Assert.True(await store.CompleteWorkReplyRejectedAsync(
            persisted,
            replyMessageId,
            replyDirectiveId,
            ["source-message-not-found", "source-message-not-found"],
            capturedAt.AddMinutes(2)));
        Assert.False(await store.CompleteWorkReplyRejectedAsync(
            persisted,
            replyMessageId,
            replyDirectiveId,
            ["source-message-not-found"],
            capturedAt.AddMinutes(3)));
        Assert.Empty(await store.ReadAcceptedWorkRepliesAsync(
            "occupant-replies",
            "INBOX",
            10));

        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            """
            SELECT reply_emission_state, reply_emission_failure_codes
            FROM occupant_channel.imap_inbound_emails
            WHERE source_id = 'occupant-replies'
              AND mailbox = 'INBOX'
              AND uid_validity = 7
              AND uid = 1;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("rejected", reader.GetString(0));
        Assert.Equal(["source-message-not-found"], reader.GetFieldValue<string[]>(1));
    }

    [Fact]
    public async Task Migration_is_idempotent()
    {
        await ResetAsync();
        await using var dataSource = fixture.CreateDataSource();
        var migrator = new PostgreSqlOccupantChannelTokenMigrator(dataSource);

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        await using var command = dataSource.CreateCommand(
            "SELECT version FROM occupant_channel.schema_migrations ORDER BY version;");
        await using var reader = await command.ExecuteReaderAsync();
        var versions = new List<int>();
        while (await reader.ReadAsync())
        {
            versions.Add(reader.GetInt32(0));
        }

        Assert.Equal([1, 2, 3, 4], versions);
    }

    private async Task ResetAndMigrateAsync()
    {
        await ResetAsync();
        await using var dataSource = fixture.CreateDataSource();
        await new PostgreSqlOccupantChannelTokenMigrator(dataSource).MigrateAsync();
    }

    private async Task ResetAsync()
    {
        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            "DROP SCHEMA IF EXISTS occupant_channel CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static ImapInboundEmailBatch Batch(
        uint uidValidity,
        uint highestUid,
        params (uint Uid, string Body)[] messages) =>
        new(
            "occupant-replies",
            "INBOX",
            uidValidity,
            highestUid,
            messages.Select(message => new FetchedImapMessage(
                message.Uid,
                Encoding.ASCII.GetBytes(message.Body))).ToArray());
}
