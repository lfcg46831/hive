using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Infrastructure.Auditing.PostgreSql;

public sealed class PostgreSqlJourneyAuditReadModel : IJourneyAuditReadModel, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public PostgreSqlJourneyAuditReadModel(string connectionString)
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

    public PostgreSqlJourneyAuditReadModel(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    public JourneyAuditTimeline ReadTimeline(
        OrganizationId organizationId,
        ThreadId threadId,
        DirectiveId? directiveId = null)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(threadId);

        using var command = _dataSource.CreateCommand(
            $"""
            SELECT
                {PostgreSqlJourneyAuditRecordReader.SelectColumns}
            FROM {JourneyAuditSchema.SchemaName}.journey_events
            WHERE organization_id = @organization_id
              AND thread_id = @thread_id
              AND (@directive_id IS NULL OR directive_id = @directive_id)
            ORDER BY sequence_id;
            """);
        command.Parameters.Add("organization_id", NpgsqlDbType.Text).Value = organizationId.Value;
        command.Parameters.Add("thread_id", NpgsqlDbType.Uuid).Value = threadId.Value;
        command.Parameters.Add("directive_id", NpgsqlDbType.Uuid).Value =
            directiveId?.Value ?? (object)DBNull.Value;

        var entries = new List<JourneyAuditTimelineEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(JourneyAuditTimelineEntry.FromRecord(
                PostgreSqlJourneyAuditRecordReader.Read(reader)));
        }

        return new JourneyAuditTimeline(
            organizationId,
            threadId,
            directiveId,
            entries);
    }
}
