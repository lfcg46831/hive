using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Infrastructure.Auditing.PostgreSql;

public sealed class PostgreSqlDirectiveAuditExportStore :
    IDirectiveAuditExportReader,
    IDirectiveAuditExportResultSink,
    IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public PostgreSqlDirectiveAuditExportStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "PostgreSQL connection string is required for directive audit/export.",
                nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _ownsDataSource = true;
    }

    public PostgreSqlDirectiveAuditExportStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<DirectiveAuditExportPageData> ReadAsync(
        OrganizationId organizationId,
        ThreadId threadId,
        DirectiveId directiveId,
        long afterSequence,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(directiveId);
        if (afterSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }

        if (pageSize is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var events = await ReadEventsAsync(
                connection,
                organizationId,
                threadId,
                directiveId,
                afterSequence,
                pageSize,
                cancellationToken)
            .ConfigureAwait(false);
        var isTerminal = await ReadTerminalAsync(
                connection,
                organizationId,
                threadId,
                directiveId,
                cancellationToken)
            .ConfigureAwait(false);
        var result = isTerminal
            ? await ReadResultAsync(
                    connection,
                    organizationId,
                    threadId,
                    directiveId,
                    cancellationToken)
                .ConfigureAwait(false)
            : null;

        return new DirectiveAuditExportPageData(
            organizationId,
            threadId,
            directiveId,
            afterSequence,
            events,
            isTerminal,
            result);
    }

    public async ValueTask StoreAsync(
        DirectiveAuditExportResultData result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        await using var command = _dataSource.CreateCommand(
            $"""
            INSERT INTO {JourneyAuditSchema.SchemaName}.directive_export_results (
                organization_id,
                thread_id,
                directive_id,
                source_position_id,
                message_type,
                schema_version,
                content)
            VALUES (
                @organization_id,
                @thread_id,
                @directive_id,
                @source_position_id,
                @message_type,
                @schema_version,
                @content)
            ON CONFLICT (organization_id, thread_id, directive_id) DO NOTHING;
            """);
        AddScopeParameters(
            command,
            result.OrganizationId,
            result.ThreadId,
            result.DirectiveId);
        command.Parameters.Add("source_position_id", NpgsqlDbType.Text).Value =
            result.SourcePositionId.Value;
        command.Parameters.Add("message_type", NpgsqlDbType.Text).Value =
            result.MessageType;
        command.Parameters.Add("schema_version", NpgsqlDbType.Integer).Value =
            result.SchemaVersion;
        command.Parameters.Add("content", NpgsqlDbType.Jsonb).Value = result.Content;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() =>
        _ownsDataSource ? _dataSource.DisposeAsync() : ValueTask.CompletedTask;

    private static async Task<IReadOnlyList<DirectiveAuditExportEventData>> ReadEventsAsync(
        NpgsqlConnection connection,
        OrganizationId organizationId,
        ThreadId threadId,
        DirectiveId directiveId,
        long afterSequence,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT
                sequence_id,
                {PostgreSqlJourneyAuditRecordReader.SelectColumns}
            FROM {JourneyAuditSchema.SchemaName}.journey_events
            WHERE organization_id = @organization_id
              AND thread_id = @thread_id
              AND directive_id = @directive_id
              AND sequence_id > @after_sequence
            ORDER BY sequence_id
            LIMIT @page_size;
            """,
            connection);
        AddScopeParameters(command, organizationId, threadId, directiveId);
        command.Parameters.Add("after_sequence", NpgsqlDbType.Bigint).Value = afterSequence;
        command.Parameters.Add("page_size", NpgsqlDbType.Integer).Value = pageSize;

        var events = new List<DirectiveAuditExportEventData>(pageSize);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new DirectiveAuditExportEventData(
                reader.GetInt64(0),
                PostgreSqlJourneyAuditRecordReader.Read(reader, ordinalOffset: 1)));
        }

        return events;
    }

    private static async Task<bool> ReadTerminalAsync(
        NpgsqlConnection connection,
        OrganizationId organizationId,
        ThreadId threadId,
        DirectiveId directiveId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT EXISTS (
                SELECT 1
                FROM {JourneyAuditSchema.SchemaName}.journey_events
                WHERE organization_id = @organization_id
                  AND thread_id = @thread_id
                  AND directive_id = @directive_id
                  AND (
                      stage = 'ResultMessageCreated'
                      OR (stage = 'AgentDecided' AND outcome IN ('Failed', 'Rejected'))
                  ));
            """,
            connection);
        AddScopeParameters(command, organizationId, threadId, directiveId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task<DirectiveAuditExportResultData?> ReadResultAsync(
        NpgsqlConnection connection,
        OrganizationId organizationId,
        ThreadId threadId,
        DirectiveId directiveId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT
                source_position_id,
                message_type,
                schema_version,
                content::text
            FROM {JourneyAuditSchema.SchemaName}.directive_export_results
            WHERE organization_id = @organization_id
              AND thread_id = @thread_id
              AND directive_id = @directive_id;
            """,
            connection);
        AddScopeParameters(command, organizationId, threadId, directiveId);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DirectiveAuditExportResultData(
            organizationId,
            threadId,
            directiveId,
            PositionId.From(reader.GetString(0)),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3));
    }

    private static void AddScopeParameters(
        NpgsqlCommand command,
        OrganizationId organizationId,
        ThreadId threadId,
        DirectiveId directiveId)
    {
        command.Parameters.Add("organization_id", NpgsqlDbType.Text).Value =
            organizationId.Value;
        command.Parameters.Add("thread_id", NpgsqlDbType.Uuid).Value = threadId.Value;
        command.Parameters.Add("directive_id", NpgsqlDbType.Uuid).Value = directiveId.Value;
    }
}
