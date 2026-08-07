using Hive.Domain.Identity;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Infrastructure.Inbox.ReadModels.PostgreSql;

public sealed class PostgreSqlInboxInteractionStore :
    IInboxInteractionStore,
    IAsyncDisposable
{
    private readonly NpgsqlDataSource? _dataSource;

    public PostgreSqlInboxInteractionStore(IConfiguration configuration)
        : this(ConnectionString(configuration))
    {
    }

    internal PostgreSqlInboxInteractionStore(string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            _dataSource = NpgsqlDataSource.Create(connectionString);
        }
    }

    public bool IsAvailable => _dataSource is not null;

    public async ValueTask<IReadOnlyDictionary<InboxProjectionItemKey, InboxInteractionState>>
        ReadAsync(
            OrganizationId organizationId,
            string personId,
            IReadOnlyCollection<InboxProjectionItemKey> itemKeys,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        personId = InboxInteractionGuards.PersonIdentifier(personId, nameof(personId));
        ArgumentNullException.ThrowIfNull(itemKeys);
        cancellationToken.ThrowIfCancellationRequested();

        var keys = itemKeys.Distinct().ToArray();
        if (keys.Any(key => key.OrganizationId != organizationId))
        {
            throw new ArgumentException(
                "Every inbox item key must belong to the requested organization.",
                nameof(itemKeys));
        }

        if (keys.Length == 0)
        {
            return new Dictionary<InboxProjectionItemKey, InboxInteractionState>();
        }

        var dataSource = _dataSource
            ?? throw new InvalidOperationException("The inbox interaction store is not configured.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT interaction.assigned_position_id,
                   interaction.message_id,
                   interaction.read_state,
                   interaction.reply_state,
                   interaction.draft_text,
                   interaction.updated_at_utc
            FROM inbox.human_interactions AS interaction
            INNER JOIN unnest(
                @assigned_position_ids::text[],
                @message_ids::uuid[])
                AS requested(assigned_position_id, message_id)
                ON requested.assigned_position_id = interaction.assigned_position_id
               AND requested.message_id = interaction.message_id
            WHERE interaction.organization_id = @organization_id
              AND interaction.person_id = @person_id;
            """,
            connection);
        AddText(command, "organization_id", organizationId.Value);
        AddText(command, "person_id", personId);
        command.Parameters.Add(
            "assigned_position_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value = keys
                .Select(static key => key.AssignedPositionId.Value)
                .ToArray();
        command.Parameters.Add(
            "message_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = keys
                .Select(static key => key.MessageId.Value)
                .ToArray();

        var states = new Dictionary<InboxProjectionItemKey, InboxInteractionState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = new InboxProjectionItemKey(
                organizationId,
                PositionId.From(reader.GetString(0)),
                MessageId.From(reader.GetGuid(1)));
            states.Add(
                key,
                new InboxInteractionState(
                    key,
                    personId,
                    ParseEnum<InboxInteractionReadState>(reader.GetString(2)),
                    ParseEnum<InboxInteractionReplyState>(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    ReadUtc(reader, 5)));
        }

        return states;
    }

    public async ValueTask<InboxInteractionState> ApplyAsync(
        InboxInteractionMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = _dataSource
            ?? throw new InvalidOperationException("The inbox interaction store is not configured.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureStateAsync(connection, transaction, mutation, cancellationToken)
            .ConfigureAwait(false);
        var previous = await ReadForUpdateAsync(
                connection,
                transaction,
                mutation.ItemKey,
                mutation.PersonId,
                cancellationToken)
            .ConfigureAwait(false);
        var current = Apply(previous, mutation);
        await UpdateStateAsync(connection, transaction, current, cancellationToken)
            .ConfigureAwait(false);
        await AppendAuditAsync(
                connection,
                transaction,
                previous,
                current,
                mutation,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return current;
    }

    public async ValueTask<IReadOnlyList<InboxInteractionAuditEntry>> ReadAuditAsync(
        InboxProjectionItemKey itemKey,
        string personId,
        CancellationToken cancellationToken = default)
    {
        personId = InboxInteractionGuards.PersonIdentifier(personId, nameof(personId));
        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = _dataSource
            ?? throw new InvalidOperationException("The inbox interaction store is not configured.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT sequence,
                   action,
                   previous_read_state,
                   read_state,
                   previous_reply_state,
                   reply_state,
                   previous_draft_present,
                   draft_present,
                   occurred_at_utc
            FROM inbox.human_interaction_audit
            WHERE organization_id = @organization_id
              AND assigned_position_id = @assigned_position_id
              AND message_id = @message_id
              AND person_id = @person_id
            ORDER BY sequence;
            """,
            connection);
        AddKey(command, itemKey);
        AddText(command, "person_id", personId);

        var entries = new List<InboxInteractionAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new InboxInteractionAuditEntry(
                reader.GetInt64(0),
                itemKey,
                personId,
                ParseEnum<InboxInteractionAction>(reader.GetString(1)),
                ParseEnum<InboxInteractionReadState>(reader.GetString(2)),
                ParseEnum<InboxInteractionReadState>(reader.GetString(3)),
                ParseEnum<InboxInteractionReplyState>(reader.GetString(4)),
                ParseEnum<InboxInteractionReplyState>(reader.GetString(5)),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                ReadUtc(reader, 8)));
        }

        return entries;
    }

    public ValueTask DisposeAsync() =>
        _dataSource is null ? ValueTask.CompletedTask : _dataSource.DisposeAsync();

    private static async Task EnsureStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InboxInteractionMutation mutation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO inbox.human_interactions (
                organization_id,
                assigned_position_id,
                message_id,
                person_id,
                read_state,
                reply_state,
                draft_text,
                updated_at_utc)
            VALUES (
                @organization_id,
                @assigned_position_id,
                @message_id,
                @person_id,
                'Unread',
                'NotStarted',
                NULL,
                @updated_at_utc)
            ON CONFLICT (
                organization_id,
                assigned_position_id,
                message_id,
                person_id)
            DO NOTHING;
            """,
            connection,
            transaction);
        AddKey(command, mutation.ItemKey);
        AddText(command, "person_id", mutation.PersonId);
        command.Parameters.Add("updated_at_utc", NpgsqlDbType.TimestampTz).Value =
            mutation.OccurredAtUtc;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<InboxInteractionState> ReadForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InboxProjectionItemKey itemKey,
        string personId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT read_state,
                   reply_state,
                   draft_text,
                   updated_at_utc
            FROM inbox.human_interactions
            WHERE organization_id = @organization_id
              AND assigned_position_id = @assigned_position_id
              AND message_id = @message_id
              AND person_id = @person_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        AddKey(command, itemKey);
        AddText(command, "person_id", personId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The inbox interaction state could not be created.");
        }

        return new InboxInteractionState(
            itemKey,
            personId,
            ParseEnum<InboxInteractionReadState>(reader.GetString(0)),
            ParseEnum<InboxInteractionReplyState>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            ReadUtc(reader, 3));
    }

    private static InboxInteractionState Apply(
        InboxInteractionState previous,
        InboxInteractionMutation mutation)
    {
        var readState = mutation.Action switch
        {
            InboxInteractionAction.MarkRead => InboxInteractionReadState.Read,
            InboxInteractionAction.MarkUnread => InboxInteractionReadState.Unread,
            _ => previous.ReadState,
        };
        var replyState = mutation.Action switch
        {
            InboxInteractionAction.StartReply or InboxInteractionAction.SaveDraft =>
                InboxInteractionReplyState.InProgress,
            _ => previous.ReplyState,
        };
        var draftText = mutation.Action switch
        {
            InboxInteractionAction.SaveDraft => mutation.DraftText,
            InboxInteractionAction.ClearDraft => null,
            _ => previous.DraftText,
        };
        return new InboxInteractionState(
            previous.ItemKey,
            previous.PersonId,
            readState,
            replyState,
            draftText,
            mutation.OccurredAtUtc);
    }

    private static async Task UpdateStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InboxInteractionState state,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE inbox.human_interactions
            SET read_state = @read_state,
                reply_state = @reply_state,
                draft_text = @draft_text,
                updated_at_utc = @updated_at_utc
            WHERE organization_id = @organization_id
              AND assigned_position_id = @assigned_position_id
              AND message_id = @message_id
              AND person_id = @person_id;
            """,
            connection,
            transaction);
        AddKey(command, state.ItemKey);
        AddText(command, "person_id", state.PersonId);
        AddText(command, "read_state", state.ReadState.ToString());
        AddText(command, "reply_state", state.ReplyState.ToString());
        command.Parameters.Add("draft_text", NpgsqlDbType.Text).Value =
            (object?)state.DraftText ?? DBNull.Value;
        command.Parameters.Add("updated_at_utc", NpgsqlDbType.TimestampTz).Value =
            state.UpdatedAtUtc;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AppendAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InboxInteractionState previous,
        InboxInteractionState current,
        InboxInteractionMutation mutation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO inbox.human_interaction_audit (
                organization_id,
                assigned_position_id,
                message_id,
                person_id,
                action,
                previous_read_state,
                read_state,
                previous_reply_state,
                reply_state,
                previous_draft_present,
                draft_present,
                occurred_at_utc)
            VALUES (
                @organization_id,
                @assigned_position_id,
                @message_id,
                @person_id,
                @action,
                @previous_read_state,
                @read_state,
                @previous_reply_state,
                @reply_state,
                @previous_draft_present,
                @draft_present,
                @occurred_at_utc);
            """,
            connection,
            transaction);
        AddKey(command, current.ItemKey);
        AddText(command, "person_id", current.PersonId);
        AddText(command, "action", mutation.Action.ToString());
        AddText(command, "previous_read_state", previous.ReadState.ToString());
        AddText(command, "read_state", current.ReadState.ToString());
        AddText(command, "previous_reply_state", previous.ReplyState.ToString());
        AddText(command, "reply_state", current.ReplyState.ToString());
        command.Parameters.Add("previous_draft_present", NpgsqlDbType.Boolean).Value =
            previous.DraftText is not null;
        command.Parameters.Add("draft_present", NpgsqlDbType.Boolean).Value =
            current.DraftText is not null;
        command.Parameters.Add("occurred_at_utc", NpgsqlDbType.TimestampTz).Value =
            mutation.OccurredAtUtc;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddKey(NpgsqlCommand command, InboxProjectionItemKey itemKey)
    {
        AddText(command, "organization_id", itemKey.OrganizationId.Value);
        AddText(command, "assigned_position_id", itemKey.AssignedPositionId.Value);
        command.Parameters.Add("message_id", NpgsqlDbType.Uuid).Value = itemKey.MessageId.Value;
    }

    private static void AddText(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value;

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) =>
        reader.GetFieldValue<DateTimeOffset>(ordinal).ToUniversalTime();

    private static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Unknown materialized {typeof(TEnum).Name} value '{value}'.");

    private static string? ConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
    }
}
