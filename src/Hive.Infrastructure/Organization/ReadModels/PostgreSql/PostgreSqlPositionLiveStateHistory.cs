using System.Collections.Immutable;
using Hive.Domain.Identity;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Infrastructure.Organization.ReadModels.PostgreSql;

public sealed class PostgreSqlPositionLiveStateHistory : IPositionLiveStateHistory, IAsyncDisposable
{
    private readonly NpgsqlDataSource? _dataSource;

    public PostgreSqlPositionLiveStateHistory(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
        if (!string.IsNullOrWhiteSpace(connectionString))
            _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async ValueTask<ImmutableArray<PositionLiveStateProjectionFact>> ReadAsync(
        OrganizationId organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = _dataSource
            ?? throw new InvalidOperationException("The position live-state history is not configured.");
        // One statement gives one MVCC snapshot, including commits below earlier allocated ids.
        // Read the whole organization: a message captured at its recipient can affect its sender.
        await using var command = dataSource.CreateCommand(
            """
            SELECT source, source_offset, persistence_id, persistence_sequence, position_id,
                   fact_type, message_id, thread_id, occurred_at_utc, payload::text
            FROM organogram.position_state_projection_facts
            WHERE organization_id = @organization_id
            ORDER BY sequence_id;
            """);
        command.Parameters.Add("organization_id", NpgsqlDbType.Text).Value = organizationId.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var facts = ImmutableArray.CreateBuilder<PositionLiveStateProjectionFact>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var rawSource = reader.GetString(0);
            if (!Enum.TryParse<PositionLiveStateProjectionSource>(rawSource, out var source)
                || !Enum.IsDefined(source) || source.ToString() != rawSource)
                throw new InvalidOperationException("Unknown persisted operational history source.");
            facts.Add(new(source, reader.GetInt64(1), organizationId, reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(8).ToUniversalTime(), reader.GetString(9),
                reader.IsDBNull(4) ? null : PositionId.From(reader.GetString(4)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(6) ? null : MessageId.From(reader.GetGuid(6)),
                reader.IsDBNull(7) ? null : ThreadId.From(reader.GetGuid(7))));
        }
        return facts.ToImmutable();
    }

    public ValueTask DisposeAsync() => _dataSource?.DisposeAsync() ?? ValueTask.CompletedTask;
}
