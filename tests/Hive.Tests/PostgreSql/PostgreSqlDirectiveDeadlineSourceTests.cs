using System.Text;
using Hive.Actors.Inbox;
using Hive.Actors.Serialization;
using Hive.Application.Events;
using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Events;
using Hive.Infrastructure.Inbox.ReadModels;
using Hive.Infrastructure.Inbox.ReadModels.PostgreSql;
using Microsoft.Extensions.Configuration;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlDirectiveDeadlineSourceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 11, 40, 0, TimeSpan.Zero);
    private static readonly OrganizationId Org = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("engineer");
    private static readonly PositionId Lead = PositionId.From("lead");
    private readonly InboxProjectionFactMapper _mapper = new(new Clock());
    private long _offset;

    [Theory]
    [InlineData(ReportKind.Progress)]
    [InlineData(ReportKind.Done)]
    public async Task Reads_original_correlation_and_reuses_persisted_response_rules(ReportKind kind)
    {
        await InitializeAsync();
        var directive = DirectiveMessage();
        await PersistAsync(directive, Position);
        await using var source = Source();
        var first = Assert.Single((await DetectAsync(source)).Occurrences);
        Assert.Equal(directive.DirectiveId, first.Payload.Correlation!.DirectiveId);
        Assert.NotEqual(directive.Id.Value, first.Payload.Correlation.DirectiveId!.Value);
        Assert.Equal(directive.Thread, first.Payload.Correlation.ThreadId);
        Assert.DoesNotContain("private", first.Payload.ToJson());

        await PersistAsync(ReportMessage(directive, ThreadId.New(), kind), Lead);
        Assert.Single((await DetectAsync(source)).Occurrences);
        await PersistAsync(ReportMessage(directive, directive.Thread, kind), Lead);
        Assert.Empty((await DetectAsync(source)).Occurrences);
        await using var restarted = Source();
        Assert.Empty((await DetectAsync(restarted)).Occurrences);
    }

    [Fact]
    public async Task Filters_scope_type_deadline_expiration_and_destination_and_reloads_late_facts()
    {
        await InitializeAsync();
        var directive = DirectiveMessage();
        await using var source = Source();
        Assert.Empty((await DetectAsync(source)).Occurrences);
        await PersistAsync(directive, Position);
        var first = Assert.Single((await DetectAsync(source)).Occurrences);
        await PersistAsync(DirectiveMessage(deadline: Now, hasDeadline: true), Position);
        await PersistAsync(DirectiveMessage(hasDeadline: false), Position);
        await PersistAsync(DirectiveMessage(organization: OrganizationId.From("other")), Position);
        await PersistAsync(DirectiveMessage(position: Lead), Lead);
        var mismatched = DirectiveMessage(position: Lead);
        await PersistAsync(mismatched, Lead);
        await PersistAsync(new Memo(MessageId.New(), Org, new PositionEndpointRef(Lead),
            new PositionEndpointRef(Position), ThreadId.New(), Priority.Normal, 1, Now, Now.AddMinutes(20), "private"), Position);
        var expired = DirectiveMessage();
        await PersistAsync(expired, Position);
        await using var dataSource = fixture.CreateDataSource();
        // Exercise the reader's defensive destination filter on an inconsistent materialized row.
        await using (var command = dataSource.CreateCommand("UPDATE inbox.items SET assigned_position_id = $1 WHERE message_id = $2"))
        {
            command.Parameters.AddWithValue(Position.Value);
            command.Parameters.AddWithValue(mismatched.Id.Value);
            await command.ExecuteNonQueryAsync();
        }
        await using (var command = dataSource.CreateCommand("UPDATE inbox.items SET is_expired = TRUE WHERE message_id = $1"))
        {
            command.Parameters.AddWithValue(expired.Id.Value);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(first.Key, Assert.Single((await DetectAsync(source)).Occurrences).Key);
        await using var restarted = Source();
        Assert.Equal(first.Key, Assert.Single((await DetectAsync(restarted)).Occurrences).Key);
        Assert.Empty((await DetectAsync(restarted, directive.Deadline)).Occurrences);
    }

    [Fact]
    public async Task Uncommitted_candidate_is_seen_after_commit_without_a_cursor()
    {
        await InitializeAsync();
        var directive = DirectiveMessage();
        await PersistAsync(directive, Position);
        await using var dataSource = fixture.CreateDataSource();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // Allocate and insert a second fact/item before detection, but publish them afterwards.
        var lateId = Guid.NewGuid();
        await using (var command = new Npgsql.NpgsqlCommand(
            """
            WITH inserted_fact AS (INSERT INTO inbox.projection_facts
                (source, source_offset, persistence_id, persistence_sequence, organization_id,
                 position_id, fact_type, message_id, thread_id, occurred_at_utc, payload)
            SELECT source, 100, persistence_id, 100, organization_id, position_id, fact_type,
                   $1, thread_id, occurred_at_utc, jsonb_set(payload, '{Id}', to_jsonb($1::text))
            FROM inbox.projection_facts WHERE message_id = $2
            RETURNING sequence_id)
            INSERT INTO inbox.items
                (organization_id, assigned_position_id, message_id, message_type, origin_type,
                 origin_position_id, destination_type, destination_position_id, thread_id, priority,
                 sent_at_utc, deadline_at_utc, is_expired, response_state, last_fact_type,
                 last_changed_at_utc, message_content)
            SELECT organization_id, assigned_position_id, $1, message_type, origin_type,
                   origin_position_id, destination_type, destination_position_id, thread_id, priority,
                   sent_at_utc, deadline_at_utc, is_expired, response_state, last_fact_type,
                   last_changed_at_utc, message_content
            FROM inbox.items WHERE message_id = $2;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(lateId);
            command.Parameters.AddWithValue(directive.Id.Value);
            await command.ExecuteNonQueryAsync();
        }
        await using var source = Source();
        Assert.Single((await DetectAsync(source)).Occurrences);
        // Another fact with a higher allocated sequence commits before the pending candidate.
        await using (var command = dataSource.CreateCommand(
            """
            INSERT INTO inbox.projection_facts
                (source, source_offset, persistence_id, persistence_sequence, organization_id,
                 position_id, fact_type, message_id, thread_id, occurred_at_utc, payload)
            SELECT source, 101, persistence_id, 101, organization_id, position_id, fact_type,
                   message_id, thread_id, occurred_at_utc, payload
            FROM inbox.projection_facts WHERE message_id = $1;
            """))
        {
            command.Parameters.AddWithValue(directive.Id.Value);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Single((await DetectAsync(source)).Occurrences);
        await transaction.CommitAsync();
        var result = await DetectAsync(source);
        Assert.Equal(2, result.Occurrences.Length);
        Assert.Null(result.Cursor);
        Assert.Contains(result.Occurrences, item => item.Payload.Correlation!.MessageId.Value == lateId);
    }

    [Fact]
    public async Task Missing_source_identity_is_a_failure_and_cancellation_propagates()
    {
        await InitializeAsync();
        await PersistAsync(DirectiveMessage(), Position);
        await using var dataSource = fixture.CreateDataSource();
        await using (var command = dataSource.CreateCommand("UPDATE inbox.projection_facts SET payload = payload - 'DirectiveId'"))
            await command.ExecuteNonQueryAsync();
        await using var source = Source();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DetectAsync(source));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadAsync(Org, [Position], Now, cancellation.Token).AsTask());
    }

    private async Task InitializeAsync()
    {
        await fixture.ResetInboxAsync();
        await using var dataSource = fixture.CreateDataSource();
        await new PostgreSqlInboxProjectionMigrator(dataSource).MigrateAsync();
    }

    private PostgreSqlDirectiveDeadlineSource Source() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:PostgreSql"] = fixture.ConnectionString }).Build());

    private static Task<DomainEventDetectionResult> DetectAsync(IDirectiveDeadlineSource source, DateTimeOffset? at = null) =>
        new DirectiveDeadlineApproachingDetector(source).EvaluateAsync(new(
            new(Org, OrganizationEventType.DirectiveDeadlineApproaching),
            EventSubscriptionsSnapshot.CreateBuilder(Org).AddPosition(Position,
                [new(new DirectiveDeadlineParameters(TimeSpan.FromMinutes(30)))]).Build(), null, at ?? Now)).AsTask();

    private async Task PersistAsync(OrgMessage message, PositionId assignedPosition)
    {
        var fact = new InboxProjectionFact(InboxProjectionSource.OrganizationalMessage, ++_offset,
            message.OrganizationId, OrgMessageManifests.ForType(message.GetType()), Now,
            Encoding.UTF8.GetString(OrgMessageJsonFormat.Serialize(message)), assignedPosition,
            $"position:{message.OrganizationId}/{assignedPosition}", _offset, message.Id, message.Thread);
        await using var feed = new PostgreSqlInboxProjectionFeed(fixture.ConnectionString);
        Assert.True(await feed.CapturePositionJournalAsync(_offset, [fact]));
        var progress = await feed.ReadProjectionProgressAsync();
        var captured = Assert.Single(await feed.ReadProjectionFactsAsync(progress.LastAppliedSequenceId, 10));
        Assert.True(await feed.ApplyProjectionFactAsync(captured, _mapper.Apply(fact)));
    }

    private static Directive DirectiveMessage(OrganizationId? organization = null, PositionId? position = null,
        DateTimeOffset? deadline = null, bool hasDeadline = true) => new(MessageId.New(), organization ?? Org,
        new PositionEndpointRef(Lead), new PositionEndpointRef(position ?? Position), ThreadId.New(), Priority.Normal,
        1, Now.AddHours(-1), hasDeadline ? deadline ?? Now.AddMinutes(20) : null, DirectiveId.New(), null, "private objective", "private context");

    private static Report ReportMessage(Directive directive, ThreadId thread, ReportKind kind) => new(MessageId.New(), Org,
        directive.To, directive.From, thread, Priority.Normal, 1, Now, null, directive.DirectiveId, kind, "private response");

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
