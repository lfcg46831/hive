using System.Collections.Immutable;
using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Infrastructure.Events;

public sealed class PostgreSqlDirectiveDeadlineSource : IDirectiveDeadlineSource, IAsyncDisposable
{
    private readonly NpgsqlDataSource? _dataSource;

    public PostgreSqlDirectiveDeadlineSource(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
        if (!string.IsNullOrWhiteSpace(connectionString))
            _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async ValueTask<ImmutableArray<OpenDirectiveDeadline>> ReadAsync(OrganizationId organizationId,
        IReadOnlyCollection<PositionId> positionIds, DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionIds);
        foreach (var positionId in positionIds) ArgumentNullException.ThrowIfNull(positionId);
        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = _dataSource
            ?? throw new InvalidOperationException("The directive deadline source is not configured.");

        // A single statement supplies one MVCC snapshot. Never use max(sequence_id) or the inbox
        // business-time watermark as a cursor: allocated ids can commit in a different order.
        await using var command = dataSource.CreateCommand(
            """
            SELECT item.assigned_position_id, item.message_id, item.thread_id,
                   item.deadline_at_utc, fact.directive_id
            FROM inbox.items AS item
            LEFT JOIN LATERAL (
                SELECT payload ->> 'DirectiveId' AS directive_id
                FROM inbox.projection_facts
                WHERE organization_id = item.organization_id
                  AND position_id = item.assigned_position_id
                  AND message_id = item.message_id
                  AND thread_id = item.thread_id
                  AND source = 'OrganizationalMessage'
                  AND fact_type = 'directive'
                ORDER BY sequence_id DESC
                LIMIT 1
            ) AS fact ON TRUE
            WHERE item.organization_id = @organization_id
              AND item.assigned_position_id = ANY(@position_ids)
              AND item.destination_type = 'Position'
              AND item.destination_position_id = item.assigned_position_id
              AND item.message_type = 'Directive'
              AND item.response_state = 'AwaitingResponse'
              AND NOT item.is_expired
              AND item.deadline_at_utc > @evaluated_at_utc
            ORDER BY item.assigned_position_id COLLATE "C", item.message_id;
            """);
        command.Parameters.Add("organization_id", NpgsqlDbType.Text).Value = organizationId.Value;
        command.Parameters.Add("position_ids", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            positionIds.Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray();
        command.Parameters.Add("evaluated_at_utc", NpgsqlDbType.TimestampTz).Value = evaluatedAtUtc.ToUniversalTime();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var candidates = ImmutableArray.CreateBuilder<OpenDirectiveDeadline>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(4) || !Guid.TryParseExact(reader.GetString(4), "D", out var directiveId)
                || directiveId == Guid.Empty)
                throw new InvalidOperationException("Persisted deadline candidate has no valid source directive identity.");
            candidates.Add(new(organizationId, PositionId.From(reader.GetString(0)),
                new EventSourceCorrelation(MessageId.From(reader.GetGuid(1)), ThreadId.From(reader.GetGuid(2)),
                    DirectiveId.From(directiveId)), reader.GetFieldValue<DateTimeOffset>(3)));
        }
        return candidates.ToImmutable();
    }

    public ValueTask DisposeAsync() => _dataSource?.DisposeAsync() ?? ValueTask.CompletedTask;
}
