using Hive.Domain.Identity;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Infrastructure.Organization.ReadModels.PostgreSql;

public sealed class PostgreSqlPositionLiveStateWriter :
    IPositionLiveStateWriter,
    IAsyncDisposable
{
    private readonly NpgsqlDataSource? _dataSource;

    public PostgreSqlPositionLiveStateWriter(IConfiguration configuration)
        : this(ConnectionString(configuration))
    {
    }

    internal PostgreSqlPositionLiveStateWriter(string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            _dataSource = NpgsqlDataSource.Create(connectionString);
        }
    }

    public async ValueTask<PositionLiveStateSnapshot> AdvanceAsync(
        OrganizationId organizationId,
        PositionId positionId,
        PositionLiveState state,
        DateTimeOffset updatedAtUtc,
        PositionLiveStateCorrelatedEvent? correlatedEvent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown live state.");
        }

        if (updatedAtUtc == default)
        {
            throw new ArgumentException("Timestamp must be specified.", nameof(updatedAtUtc));
        }

        if (updatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", nameof(updatedAtUtc));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_dataSource is null)
        {
            throw new InvalidOperationException("The position live-state read model is not configured.");
        }

        await using var command = _dataSource.CreateCommand(
            """
            UPDATE organogram.position_states
            SET state = @state,
                sequence = sequence + 1,
                updated_at_utc = @updated_at_utc,
                last_event_type = CASE
                    WHEN @has_correlated_event THEN @last_event_type
                    ELSE last_event_type
                END,
                last_event_thread_id = CASE
                    WHEN @has_correlated_event THEN @last_event_thread_id
                    ELSE last_event_thread_id
                END,
                last_event_occurred_at_utc = CASE
                    WHEN @has_correlated_event THEN @last_event_occurred_at_utc
                    ELSE last_event_occurred_at_utc
                END
            WHERE organization_id = @organization_id
              AND position_id = @position_id
            RETURNING state,
                      sequence,
                      updated_at_utc,
                      last_event_type,
                      last_event_thread_id,
                      last_event_occurred_at_utc;
            """);
        AddText(command, "organization_id", organizationId.Value);
        AddText(command, "position_id", positionId.Value);
        AddText(command, "state", state.ToString());
        command.Parameters.Add("updated_at_utc", NpgsqlDbType.TimestampTz).Value = updatedAtUtc;
        command.Parameters.Add("has_correlated_event", NpgsqlDbType.Boolean).Value =
            correlatedEvent is not null;
        AddNullableText(command, "last_event_type", correlatedEvent?.Type);
        command.Parameters.Add("last_event_thread_id", NpgsqlDbType.Uuid).Value =
            correlatedEvent?.ThreadId ?? (object)DBNull.Value;
        command.Parameters.Add("last_event_occurred_at_utc", NpgsqlDbType.TimestampTz).Value =
            correlatedEvent?.OccurredAtUtc ?? (object)DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Position '{positionId.Value}' does not have a live-state row in organization '{organizationId.Value}'.");
        }

        return ReadSnapshot(positionId.Value, reader);
    }

    public ValueTask DisposeAsync() =>
        _dataSource is null ? ValueTask.CompletedTask : _dataSource.DisposeAsync();

    private static PositionLiveStateSnapshot ReadSnapshot(
        string positionId,
        NpgsqlDataReader reader)
    {
        var correlatedEvent = reader.IsDBNull(3)
            ? null
            : new PositionLiveStateCorrelatedEvent(
                reader.GetString(3),
                reader.GetGuid(4),
                reader.GetFieldValue<DateTimeOffset>(5).ToUniversalTime());
        return new PositionLiveStateSnapshot(
            positionId,
            ParseState(reader.GetString(0)),
            reader.GetInt64(1),
            reader.GetFieldValue<DateTimeOffset>(2).ToUniversalTime(),
            correlatedEvent);
    }

    private static PositionLiveState ParseState(string value) =>
        Enum.TryParse<PositionLiveState>(value, ignoreCase: false, out var parsed) &&
        Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Unknown materialized position live state '{value}'.");

    private static void AddText(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value;

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value ?? (object)DBNull.Value;

    private static string? ConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
    }
}
