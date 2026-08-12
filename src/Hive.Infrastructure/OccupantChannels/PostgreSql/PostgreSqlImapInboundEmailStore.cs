using Npgsql;
using NpgsqlTypes;
using Hive.Domain.Identity;
using Hive.Domain.OccupantChannels;

namespace Hive.Infrastructure.OccupantChannels.PostgreSql;

internal sealed class PostgreSqlImapInboundEmailStore
    : IImapInboundEmailStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public PostgreSqlImapInboundEmailStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "Connection string cannot be empty or whitespace.",
                nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _ownsDataSource = true;
    }

    internal PostgreSqlImapInboundEmailStore(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<ImapInboundEmailCheckpoint?> ReadCheckpointAsync(
        string sourceId,
        string mailbox,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sourceId, mailbox);
        await using var command = _dataSource.CreateCommand(
            $"""
            SELECT uid_validity, last_uid
            FROM {OccupantChannelTokenSchema.SchemaName}.imap_checkpoints
            WHERE source_id = @source_id AND mailbox = @mailbox;
            """);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("mailbox", mailbox);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ImapInboundEmailCheckpoint(
            sourceId,
            mailbox,
            checked((uint)reader.GetInt64(0)),
            checked((uint)reader.GetInt64(1)));
    }

    public async Task<ImapInboundEmailCommitResult> CommitBatchAsync(
        ImapInboundEmailCheckpoint? expectedCheckpoint,
        ImapInboundEmailBatch batch,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateBatch(expectedCheckpoint, batch, capturedAtUtc);
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext(@lock_key));",
            connection,
            transaction))
        {
            lockCommand.Parameters.AddWithValue(
                "lock_key",
                $"hive.imap:{batch.SourceId}:{batch.Mailbox}");
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var current = await ReadCheckpointAsync(
                connection,
                transaction,
                batch.SourceId,
                batch.Mailbox,
                cancellationToken)
            .ConfigureAwait(false);
        if (current != expectedCheckpoint)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ImapInboundEmailCommitResult.ConcurrentCheckpoint();
        }

        var inserted = 0;
        foreach (var message in batch.Messages)
        {
            await using var insert = new NpgsqlCommand(
                $"""
                INSERT INTO {OccupantChannelTokenSchema.SchemaName}.imap_inbound_emails (
                    source_id,
                    mailbox,
                    uid_validity,
                    uid,
                    raw_message,
                    captured_at,
                    processing_state)
                VALUES (
                    @source_id,
                    @mailbox,
                    @uid_validity,
                    @uid,
                    @raw_message,
                    @captured_at,
                    'pending')
                ON CONFLICT (source_id, mailbox, uid_validity, uid) DO NOTHING;
                """,
                connection,
                transaction);
            AddIdentityParameters(insert, batch.SourceId, batch.Mailbox);
            insert.Parameters.AddWithValue("uid_validity", (long)batch.UidValidity);
            insert.Parameters.AddWithValue("uid", (long)message.Uid);
            insert.Parameters.Add("raw_message", NpgsqlDbType.Bytea).Value = message.RawMessage;
            insert.Parameters.AddWithValue("captured_at", capturedAtUtc);
            inserted += await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var checkpoint = new NpgsqlCommand(
            $"""
            INSERT INTO {OccupantChannelTokenSchema.SchemaName}.imap_checkpoints (
                source_id,
                mailbox,
                uid_validity,
                last_uid,
                updated_at)
            VALUES (
                @source_id,
                @mailbox,
                @uid_validity,
                @last_uid,
                @updated_at)
            ON CONFLICT (source_id, mailbox) DO UPDATE SET
                uid_validity = EXCLUDED.uid_validity,
                last_uid = EXCLUDED.last_uid,
                updated_at = EXCLUDED.updated_at;
            """,
            connection,
            transaction))
        {
            AddIdentityParameters(checkpoint, batch.SourceId, batch.Mailbox);
            checkpoint.Parameters.AddWithValue("uid_validity", (long)batch.UidValidity);
            checkpoint.Parameters.AddWithValue("last_uid", (long)batch.HighestUid);
            checkpoint.Parameters.AddWithValue("updated_at", capturedAtUtc);
            await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var committedCheckpoint = new ImapInboundEmailCheckpoint(
            batch.SourceId,
            batch.Mailbox,
            batch.UidValidity,
            batch.HighestUid);
        return new ImapInboundEmailCommitResult(true, inserted, committedCheckpoint);
    }

    public async Task<IReadOnlyList<ImapInboundEmailEnvelope>> ReadPendingAsync(
        string sourceId,
        string mailbox,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sourceId, mailbox);
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 500.");
        }

        await using var command = _dataSource.CreateCommand(
            $"""
            SELECT uid_validity, uid, raw_message, captured_at
            FROM {OccupantChannelTokenSchema.SchemaName}.imap_inbound_emails
            WHERE source_id = @source_id
              AND mailbox = @mailbox
              AND processing_state = 'pending'
            ORDER BY captured_at, uid_validity, uid
            LIMIT @limit;
            """);
        AddIdentityParameters(command, sourceId, mailbox);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = new List<ImapInboundEmailEnvelope>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ImapInboundEmailEnvelope(
                sourceId,
                mailbox,
                checked((uint)reader.GetInt64(0)),
                checked((uint)reader.GetInt64(1)),
                reader.GetFieldValue<byte[]>(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return result;
    }

    public async Task<bool> CompleteAcceptedAsync(
        InboundOccupantEmailAdmission admission,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ValidateProcessedAt(processedAtUtc);
        if (admission.ContentTrust is not InboundOccupantEmailContentTrust.Untrusted)
        {
            throw new ArgumentOutOfRangeException(
                nameof(admission),
                "Inbound occupant email content must remain untrusted.");
        }

        if (string.IsNullOrWhiteSpace(admission.PlainTextReply))
        {
            throw new ArgumentException(
                "An accepted inbound occupant email requires plain-text reply content.",
                nameof(admission));
        }

        var envelope = admission.Envelope;
        await using var command = _dataSource.CreateCommand(
            $"""
            UPDATE {OccupantChannelTokenSchema.SchemaName}.imap_inbound_emails
            SET processing_state = 'accepted',
                processed_at = @processed_at,
                token_id = @token_id,
                token_issued_at = @token_issued_at,
                token_expires_at = @token_expires_at,
                organization_id = @organization_id,
                position_id = @position_id,
                message_id = @message_id,
                thread_id = @thread_id,
                request_id = @request_id,
                occupant_id = @occupant_id,
                user_id = @user_id,
                binding_id = @binding_id,
                reply_text = @reply_text,
                content_trust = 'untrusted'
            WHERE source_id = @source_id
              AND mailbox = @mailbox
              AND uid_validity = @uid_validity
              AND uid = @uid
              AND processing_state = 'pending';
            """);
        AddEnvelopeIdentityParameters(command, envelope);
        command.Parameters.AddWithValue("processed_at", processedAtUtc);
        command.Parameters.AddWithValue("token_id", admission.Correlation.TokenId);
        command.Parameters.AddWithValue("token_issued_at", admission.Correlation.IssuedAtUtc);
        command.Parameters.AddWithValue("token_expires_at", admission.Correlation.ExpiresAtUtc);
        command.Parameters.AddWithValue(
            "organization_id",
            admission.Correlation.OrganizationId.Value);
        command.Parameters.AddWithValue("position_id", admission.Correlation.PositionId.Value);
        command.Parameters.AddWithValue("message_id", admission.Correlation.MessageId.Value);
        command.Parameters.AddWithValue("thread_id", admission.Correlation.ThreadId.Value);
        command.Parameters.Add("request_id", NpgsqlDbType.Uuid).Value =
            admission.Correlation.RequestId is { } requestId
                ? requestId.Value
                : DBNull.Value;
        command.Parameters.AddWithValue("occupant_id", admission.OccupantId.Value);
        command.Parameters.AddWithValue("user_id", admission.UserId.Value);
        command.Parameters.AddWithValue("binding_id", admission.BindingId.Value);
        command.Parameters.AddWithValue("reply_text", admission.PlainTextReply);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> CompleteRejectedAsync(
        ImapInboundEmailEnvelope envelope,
        InboundOccupantEmailFailureCode failure,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateProcessedAt(processedAtUtc);
        if (!Enum.IsDefined(failure)
            || failure is InboundOccupantEmailFailureCode.IdentityUnavailable
                or InboundOccupantEmailFailureCode.DecisionTokenStoreUnavailable)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failure),
                failure,
                "Only terminal inbound email failures may be persisted as rejected.");
        }

        await using var command = _dataSource.CreateCommand(
            $"""
            UPDATE {OccupantChannelTokenSchema.SchemaName}.imap_inbound_emails
            SET processing_state = 'rejected',
                processed_at = @processed_at,
                failure_code = @failure_code
            WHERE source_id = @source_id
              AND mailbox = @mailbox
              AND uid_validity = @uid_validity
              AND uid = @uid
              AND processing_state = 'pending';
            """);
        AddEnvelopeIdentityParameters(command, envelope);
        command.Parameters.AddWithValue("processed_at", processedAtUtc);
        command.Parameters.AddWithValue("failure_code", failure.ToCode());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyList<InboundOccupantEmailAdmission>> ReadAcceptedAsync(
        string sourceId,
        string mailbox,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sourceId, mailbox);
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 500.");
        }

        await using var command = _dataSource.CreateCommand(
            $"""
            SELECT uid_validity,
                   uid,
                   raw_message,
                   captured_at,
                   token_id,
                   token_issued_at,
                   token_expires_at,
                   organization_id,
                   position_id,
                   message_id,
                   thread_id,
                   request_id,
                   occupant_id,
                   user_id,
                   binding_id,
                   reply_text
            FROM {OccupantChannelTokenSchema.SchemaName}.imap_inbound_emails
            WHERE source_id = @source_id
              AND mailbox = @mailbox
              AND processing_state = 'accepted'
            ORDER BY processed_at, uid_validity, uid
            LIMIT @limit;
            """);
        AddIdentityParameters(command, sourceId, mailbox);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = new List<InboundOccupantEmailAdmission>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var envelope = new ImapInboundEmailEnvelope(
                sourceId,
                mailbox,
                checked((uint)reader.GetInt64(0)),
                checked((uint)reader.GetInt64(1)),
                reader.GetFieldValue<byte[]>(2),
                reader.GetFieldValue<DateTimeOffset>(3));
            var claims = new OccupantChannelCorrelationTokenClaims(
                reader.GetGuid(4),
                OrganizationId.From(reader.GetString(7)),
                PositionId.From(reader.GetString(8)),
                MessageId.From(reader.GetGuid(9)),
                ThreadId.From(reader.GetGuid(10)),
                reader.IsDBNull(11) ? null : MessageId.From(reader.GetGuid(11)),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetFieldValue<DateTimeOffset>(6));
            result.Add(new InboundOccupantEmailAdmission(
                envelope,
                claims,
                OccupantId.From(reader.GetString(12)),
                UserId.From(reader.GetGuid(13)),
                OccupantChannelBindingId.From(reader.GetGuid(14)),
                reader.GetString(15),
                InboundOccupantEmailContentTrust.Untrusted));
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<ImapInboundEmailCheckpoint?> ReadCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sourceId,
        string mailbox,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT uid_validity, last_uid
            FROM {OccupantChannelTokenSchema.SchemaName}.imap_checkpoints
            WHERE source_id = @source_id AND mailbox = @mailbox;
            """,
            connection,
            transaction);
        AddIdentityParameters(command, sourceId, mailbox);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ImapInboundEmailCheckpoint(
            sourceId,
            mailbox,
            checked((uint)reader.GetInt64(0)),
            checked((uint)reader.GetInt64(1)));
    }

    private static void ValidateBatch(
        ImapInboundEmailCheckpoint? expectedCheckpoint,
        ImapInboundEmailBatch batch,
        DateTimeOffset capturedAtUtc)
    {
        ValidateIdentity(batch.SourceId, batch.Mailbox);
        if (expectedCheckpoint is not null
            && (!string.Equals(
                    expectedCheckpoint.SourceId,
                    batch.SourceId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    expectedCheckpoint.Mailbox,
                    batch.Mailbox,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The expected checkpoint belongs to a different IMAP source.",
                nameof(expectedCheckpoint));
        }

        if (batch.UidValidity == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batch), "UIDVALIDITY must be positive.");
        }

        if (capturedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Capture timestamp must use the UTC offset.", nameof(capturedAtUtc));
        }

        var baseline = expectedCheckpoint is not null
            && expectedCheckpoint.UidValidity == batch.UidValidity
                ? expectedCheckpoint.LastUid
                : 0;
        var previous = baseline;
        foreach (var message in batch.Messages)
        {
            if (message.Uid <= previous || message.RawMessage.Length == 0)
            {
                throw new ArgumentException(
                    "Messages must have non-empty payloads and strictly increasing UIDs after the checkpoint.",
                    nameof(batch));
            }

            previous = message.Uid;
        }

        if (batch.HighestUid < previous || batch.HighestUid < baseline)
        {
            throw new ArgumentException(
                "Batch high-water UID cannot precede captured messages or the checkpoint.",
                nameof(batch));
        }
    }

    private static void ValidateIdentity(string sourceId, string mailbox)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new ArgumentException("IMAP source id cannot be empty.", nameof(sourceId));
        }

        if (string.IsNullOrWhiteSpace(mailbox))
        {
            throw new ArgumentException("IMAP mailbox cannot be empty.", nameof(mailbox));
        }
    }

    private static void ValidateProcessedAt(DateTimeOffset processedAtUtc)
    {
        if (processedAtUtc == default || processedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Inbound email processing timestamp must be a non-default UTC value.",
                nameof(processedAtUtc));
        }
    }

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        string sourceId,
        string mailbox)
    {
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("mailbox", mailbox);
    }

    private static void AddEnvelopeIdentityParameters(
        NpgsqlCommand command,
        ImapInboundEmailEnvelope envelope)
    {
        ValidateIdentity(envelope.SourceId, envelope.Mailbox);
        AddIdentityParameters(command, envelope.SourceId, envelope.Mailbox);
        command.Parameters.AddWithValue("uid_validity", (long)envelope.UidValidity);
        command.Parameters.AddWithValue("uid", (long)envelope.Uid);
    }
}
