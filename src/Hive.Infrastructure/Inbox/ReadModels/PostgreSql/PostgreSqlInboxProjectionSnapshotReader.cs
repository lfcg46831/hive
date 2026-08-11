using System.Data;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Infrastructure.Inbox.ReadModels.PostgreSql;

public sealed class PostgreSqlInboxProjectionSnapshotReader :
    IInboxProjectionSnapshotReader,
    IAsyncDisposable
{
    private readonly NpgsqlDataSource? _dataSource;

    public PostgreSqlInboxProjectionSnapshotReader(IConfiguration configuration)
        : this(ConnectionString(configuration))
    {
    }

    internal PostgreSqlInboxProjectionSnapshotReader(string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            _dataSource = NpgsqlDataSource.Create(connectionString);
        }
    }

    public bool IsAvailable => _dataSource is not null;

    public async ValueTask<InboxProjectionSnapshot> ReadAsync(
        OrganizationId organizationId,
        IReadOnlyCollection<PositionId> assignedPositionIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(assignedPositionIds);
        if (assignedPositionIds.Any(static positionId => positionId is null))
        {
            throw new ArgumentException(
                "Assigned position identifiers cannot contain null entries.",
                nameof(assignedPositionIds));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = _dataSource
            ?? throw new InvalidOperationException("The inbox projection read model is not configured.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        var watermark = await ReadWatermarkAsync(
            organizationId,
            connection,
            transaction,
            cancellationToken);
        var items = await ReadItemsAsync(
            organizationId,
            assignedPositionIds,
            connection,
            transaction,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new InboxProjectionSnapshot(organizationId, watermark, items);
    }

    public ValueTask DisposeAsync() =>
        _dataSource is null ? ValueTask.CompletedTask : _dataSource.DisposeAsync();

    private static async Task<DateTimeOffset?> ReadWatermarkAsync(
        OrganizationId organizationId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT last_event_applied_at_utc
            FROM inbox.projection_watermarks
            WHERE organization_id = @organization_id;
            """,
            connection,
            transaction);
        AddText(command, "organization_id", organizationId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? reader.GetFieldValue<DateTimeOffset>(0).ToUniversalTime()
            : null;
    }

    private static async Task<IReadOnlyList<InboxProjectionItem>> ReadItemsAsync(
        OrganizationId organizationId,
        IReadOnlyCollection<PositionId> assignedPositionIds,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (assignedPositionIds.Count == 0)
        {
            return [];
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT assigned_position_id,
                   message_id,
                   message_type,
                   origin_type,
                   origin_position_id,
                   destination_type,
                   destination_position_id,
                   thread_id,
                   priority,
                   sent_at_utc,
                   deadline_at_utc,
                   is_expired,
                   response_state,
                   approval_request_id,
                   approval_action,
                   approval_policy_ref,
                   approval_state,
                   approval_decision_message_id,
                   approval_decided_at_utc,
                   is_delegated,
                   last_reminder_at_utc,
                   message_content
            FROM inbox.items
            WHERE organization_id = @organization_id
              AND assigned_position_id = ANY(@assigned_position_ids)
            ORDER BY deadline_at_utc ASC NULLS LAST,
                     CASE priority
                         WHEN 'Critical' THEN 4
                         WHEN 'High' THEN 3
                         WHEN 'Normal' THEN 2
                         WHEN 'Low' THEN 1
                     END DESC,
                     sent_at_utc DESC,
                     message_id;
            """,
            connection,
            transaction);
        AddText(command, "organization_id", organizationId.Value);
        command.Parameters.Add("assigned_position_ids", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            assignedPositionIds
                .Select(positionId => positionId.Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<InboxProjectionItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var messageType = ParseEnum<InboxProjectionMessageType>(reader.GetString(2));
            var approval = reader.IsDBNull(13)
                ? null
                : new InboxProjectionApproval(
                    MessageId.From(reader.GetGuid(13)),
                    reader.GetString(14),
                    ApprovalPolicyRef.From(reader.GetString(15)),
                    ParseEnum<InboxProjectionApprovalState>(reader.GetString(16)),
                    reader.IsDBNull(17) ? null : MessageId.From(reader.GetGuid(17)),
                    reader.IsDBNull(18)
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(18).ToUniversalTime());
            items.Add(new InboxProjectionItem(
                new InboxProjectionItemKey(
                    organizationId,
                    PositionId.From(reader.GetString(0)),
                    MessageId.From(reader.GetGuid(1))),
                messageType,
                Endpoint(reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)),
                Endpoint(reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)),
                ThreadId.From(reader.GetGuid(7)),
                ParseEnum<Priority>(reader.GetString(8)),
                reader.GetFieldValue<DateTimeOffset>(9).ToUniversalTime(),
                reader.IsDBNull(10)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(10).ToUniversalTime(),
                reader.GetBoolean(11),
                ParseEnum<InboxProjectionResponseState>(reader.GetString(12)),
                approval,
                PostgreSqlInboxMessageContentJson.Deserialize(
                    messageType,
                    reader.GetString(21)),
                reader.GetBoolean(19),
                reader.IsDBNull(20)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(20).ToUniversalTime()));
        }

        return items;
    }

    private static EndpointRef Endpoint(string type, string? positionId) =>
        type switch
        {
            "Position" when positionId is not null =>
                new PositionEndpointRef(PositionId.From(positionId)),
            "OrganizationOwner" when positionId is null => new OrganizationOwnerEndpointRef(),
            _ => throw new InvalidOperationException(
                $"Unknown materialized inbox endpoint '{type}'."),
        };

    private static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Unknown materialized {typeof(TEnum).Name} value '{value}'.");

    private static void AddText(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value;

    private static string? ConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
    }
}
