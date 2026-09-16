using System.Collections.Immutable;
using System.Data;
using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Infrastructure.Events;

public sealed class PostgreSqlBudgetThresholdSource : IBudgetThresholdSource, IAsyncDisposable
{
    internal const string DefaultTimeZone = "UTC";
    private const string Currency = "EUR";

    private readonly NpgsqlDataSource? _dataSource;

    public PostgreSqlBudgetThresholdSource(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
        if (!string.IsNullOrWhiteSpace(connectionString))
            _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async ValueTask<ImmutableArray<PositionDailyBudgetUsage>> ReadAsync(OrganizationId organizationId,
        IReadOnlyCollection<PositionId> positionIds, DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionIds);
        foreach (var positionId in positionIds) ArgumentNullException.ThrowIfNull(positionId);
        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = _dataSource
            ?? throw new InvalidOperationException("The budget threshold source is not configured.");
        var evaluatedAt = evaluatedAtUtc.ToUniversalTime();

        // One RepeatableRead transaction gives registry caps/timezones and audit facts the same snapshot.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var snapshot = await PostgreSqlOrganizationRegistryReader.LoadAsync(connection, transaction,
            organizationId, cancellationToken);
        if (snapshot is null) return [];
        if (snapshot.OrganizationId != organizationId)
            throw new InvalidOperationException("The registry returned a snapshot for a different organization.");

        var candidates = new SortedDictionary<string, Candidate>(StringComparer.Ordinal);
        foreach (var positionId in positionIds)
        {
            if (candidates.ContainsKey(positionId.Value)
                || !snapshot.Positions.TryGetValue(positionId, out var position)
                || !snapshot.Occupants.TryGetValue(positionId, out var occupant)
                || occupant.Value.Ai?.Budget is not { } budget
                || !(budget.ReactiveMaxEurPerDay > 0m || budget.ProactiveMaxEurPerDay > 0m || budget.TotalMaxEurPerDay > 0m))
                continue;

            var timeZone = string.IsNullOrWhiteSpace(position.Value.Timezone) ? DefaultTimeZone : position.Value.Timezone;
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone))
                throw new InvalidOperationException(
                    $"Position '{positionId.Value}' declares timezone '{timeZone}', which the runtime cannot resolve.");
            candidates.Add(positionId.Value, new(positionId, timeZone, CivilDay.Containing(evaluatedAt, zone),
                budget.ReactiveMaxEurPerDay, budget.ProactiveMaxEurPerDay, budget.TotalMaxEurPerDay));
        }
        if (candidates.Count == 0) return [];

        // Never use max(sequence_id) as progress: ids can commit out of order. Facts are reread per cycle.
        await using var command = new NpgsqlCommand(
            """
            WITH windows AS (
                SELECT position_id, starts_at_utc
                FROM unnest(@position_ids, @starts_at_utc) AS window_bounds(position_id, starts_at_utc)
            ), attempts AS (
                SELECT DISTINCT ON (cost.position_id, cost.thread_id, cost.message_id,
                                    COALESCE(cost.payload ->> 'operation', ''),
                                    COALESCE(cost.payload ->> 'iteration', ''),
                                    cost.payload ->> 'attemptId')
                       cost.position_id, cost.thread_id, cost.message_id, cost.directive_id,
                       cost.occurred_at_utc, cost.cost_amount, cost.cost_currency,
                       cost.payload ->> 'operation' AS operation,
                       cost.payload ->> 'iteration' AS iteration,
                       cost.payload ->> 'attemptId' AS attempt_id
                FROM audit.journey_events AS cost
                JOIN windows ON windows.position_id = cost.position_id
                WHERE cost.organization_id = @organization_id
                  AND cost.stage = 'GatewayCostRecorded'
                  AND cost.payload ->> 'scope' = 'attempt'
                  AND cost.occurred_at_utc >= windows.starts_at_utc
                  AND cost.occurred_at_utc <= @evaluated_at_utc
                ORDER BY cost.position_id, cost.thread_id, cost.message_id,
                         COALESCE(cost.payload ->> 'operation', ''),
                         COALESCE(cost.payload ->> 'iteration', ''),
                         cost.payload ->> 'attemptId', cost.sequence_id
            )
            SELECT attempts.position_id, attempts.thread_id, attempts.message_id, attempts.directive_id,
                   attempts.occurred_at_utc, attempts.cost_amount, attempts.cost_currency,
                   attempts.operation, attempts.iteration, attempts.attempt_id, accepted.message_type
            FROM attempts
            LEFT JOIN LATERAL (
                SELECT message_type
                FROM audit.journey_events
                WHERE organization_id = @organization_id
                  AND position_id = attempts.position_id
                  AND thread_id = attempts.thread_id
                  AND message_id = attempts.message_id
                  AND stage = 'PositionAccepted'
                  AND message_type IS NOT NULL
                ORDER BY sequence_id
                LIMIT 1
            ) AS accepted ON TRUE
            ORDER BY attempts.position_id COLLATE "C", attempts.occurred_at_utc,
                     attempts.thread_id, attempts.message_id;
            """, connection, transaction);
        command.Parameters.Add("organization_id", NpgsqlDbType.Text).Value = organizationId.Value;
        command.Parameters.Add("position_ids", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            candidates.Keys.ToArray();
        command.Parameters.Add("starts_at_utc", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz).Value =
            candidates.Values.Select(item => item.Day.StartsAtUtc).ToArray();
        command.Parameters.Add("evaluated_at_utc", NpgsqlDbType.TimestampTz).Value = evaluatedAt;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var candidate = candidates[reader.GetString(0)];
                if (reader.IsDBNull(9) || string.IsNullOrWhiteSpace(reader.GetString(9)))
                    throw new InvalidOperationException("Persisted attempt cost fact has no attempt identity.");
                if (reader.IsDBNull(5) || reader.IsDBNull(6) || !string.Equals(reader.GetString(6), Currency, StringComparison.Ordinal))
                    continue;

                var correlation = new EventSourceCorrelation(MessageId.From(reader.GetGuid(2)),
                    ThreadId.From(reader.GetGuid(1)), reader.IsDBNull(3) ? null : DirectiveId.From(reader.GetGuid(3)));
                var identity = string.Join(":", reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    reader.IsDBNull(8) ? string.Empty : reader.GetString(8), reader.GetString(9));
                candidate.Facts.Add(new(correlation, identity, reader.GetFieldValue<DateTimeOffset>(4),
                    reader.GetDecimal(5), Category(reader.IsDBNull(10) ? null : reader.GetString(10))));
            }
        }
        await transaction.CommitAsync(cancellationToken);

        return candidates.Values.Select(item => new PositionDailyBudgetUsage(organizationId, item.PositionId,
            item.TimeZone, item.Day, item.ReactiveLimitEur, item.ProactiveLimitEur, item.TotalLimitEur, item.Facts))
            .ToImmutableArray();
    }

    public ValueTask DisposeAsync() => _dataSource?.DisposeAsync() ?? ValueTask.CompletedTask;

    private static BudgetCostCategory Category(string? messageType) => messageType switch
    {
        null => BudgetCostCategory.Unclassified,
        "Pulse" or "EventTrigger" => BudgetCostCategory.Proactive,
        _ => BudgetCostCategory.Reactive,
    };

    private sealed record Candidate(PositionId PositionId, string TimeZone, CivilDay Day,
        decimal? ReactiveLimitEur, decimal? ProactiveLimitEur, decimal? TotalLimitEur)
    {
        public List<BudgetCostFact> Facts { get; } = [];
    }
}
