using Hive.Actors.Events;
using Hive.Application.Events;
using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Organization.ReadModels;
using Hive.Infrastructure.Organization.ReadModels.PostgreSql;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlPositionBlockedSourceTests(PostgreSqlFixture fixture)
{
    private long _offset;

    [Fact]
    public async Task Persisted_period_survives_replay_partial_resolution_and_restart_without_backfill()
    {
        await InitializeAsync();
        var first = BlockedFacts.Escalation(0);
        var second = BlockedFacts.Escalation(1);
        await PersistAsync(first);
        await PersistAsync(second);
        await using var history = History();
        var detected = Assert.Single((await DetectAsync(history)).Occurrences);
        Assert.Equal(first.Id, detected.Payload.Correlation!.MessageId);
        Assert.DoesNotContain("private", detected.Payload.ToJson());
        Assert.Empty((await DetectAsync(history, BlockedFacts.At.AddMinutes(30).AddTicks(-1))).Occurrences);
        Assert.Single((await DetectAsync(history, BlockedFacts.At.AddMinutes(30))).Occurrences);
        await PersistAsync(BlockedFacts.Resolution(first, 2));
        var partial = Assert.Single((await DetectAsync(history)).Occurrences);
        Assert.Equal(detected.Key, partial.Key);
        Assert.Equal(second.Id, partial.Payload.Correlation!.MessageId);
        await using var restarted = History();
        Assert.Equal(partial.Key, Assert.Single((await DetectAsync(restarted)).Occurrences).Key);
        await PersistAsync(BlockedFacts.Resolution(second, 3));
        Assert.Empty((await DetectAsync(restarted)).Occurrences);
        var next = BlockedFacts.Escalation(4);
        await PersistAsync(next);
        var newPeriod = Assert.Single((await DetectAsync(restarted)).Occurrences);
        Assert.NotEqual(partial.Key, newPeriod.Key);
        Assert.Equal(next.SentAt, Assert.IsType<PositionBlockedProlongedPayload>(newPeriod.Payload).BlockedSinceUtc);
    }

    [Fact]
    public async Task Reads_all_organization_facts_but_only_returns_subscribing_emitters()
    {
        await InitializeAsync();
        var own = BlockedFacts.Escalation(0);
        await PersistAsync(BlockedFacts.Escalation(0, OrganizationId.From("other")));
        await PersistAsync(BlockedFacts.Escalation(0, position: PositionId.From("other-position")));
        await PersistAsync(own);
        await using var history = History();
        var facts = await history.ReadAsync(BlockedFacts.Org);
        Assert.Equal(2, facts.Length);
        Assert.All(facts, fact => Assert.Equal(BlockedFacts.Lead, fact.PositionId));
        Assert.Equal(own.Id, Assert.Single((await DetectAsync(history)).Occurrences).Payload.Correlation!.MessageId);
        Assert.Empty(await new PersistedPositionBlockedSource(history).ReadAsync(BlockedFacts.Org, [BlockedFacts.Lead]));
        Assert.Empty(await history.ReadAsync(OrganizationId.From("missing")));
    }

    [Fact]
    public async Task Late_committed_lower_sequence_is_revisited_after_higher_sequence_was_seen()
    {
        await InitializeAsync();
        var late = BlockedFacts.Escalation(0);
        var higher = BlockedFacts.Escalation(1, position: PositionId.From("other-position"));
        await using var dataSource = fixture.CreateDataSource();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertAsync(BlockedFacts.Fact(late, 100), connection, transaction);
        // This commits a higher sequence while the lower sequence remains invisible.
        await PersistAsync(higher);
        await using var history = History();
        Assert.Single(await history.ReadAsync(BlockedFacts.Org));
        Assert.Empty((await DetectAsync(history)).Occurrences);
        await transaction.CommitAsync();
        var result = await DetectAsync(history);
        Assert.Null(result.Cursor);
        Assert.Equal(late.Id, Assert.Single(result.Occurrences).Payload.Correlation!.MessageId);
        Assert.Equal(2, (await history.ReadAsync(BlockedFacts.Org)).Length);
    }

    [Fact]
    public async Task Invalid_source_payload_technical_failure_and_cancellation_propagate()
    {
        await InitializeAsync();
        await PersistAsync(BlockedFacts.Escalation(0));
        await using var history = History();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => history.ReadAsync(BlockedFacts.Org, canceled.Token).AsTask());
        await using var dataSource = fixture.CreateDataSource();
        await using (var command = dataSource.CreateCommand("UPDATE organogram.position_state_projection_facts SET payload = payload - 'Id'"))
            await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => DetectAsync(history));
        await using (var command = dataSource.CreateCommand("DROP TABLE organogram.position_state_projection_facts"))
            await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<PostgresException>(() => history.ReadAsync(BlockedFacts.Org).AsTask());
    }

    private async Task InitializeAsync()
    {
        await fixture.ResetRegistryAsync();
        await using var dataSource = fixture.CreateDataSource();
        await new PostgreSqlOrganogramReadModelMigrator(dataSource).MigrateAsync();
    }

    private async Task PersistAsync(OrgMessage message)
    {
        await using var feed = new PostgreSqlPositionLiveStateProjectionFeed(fixture.ConnectionString);
        var fact = BlockedFacts.Fact(message, ++_offset);
        Assert.True(await feed.CapturePositionJournalAsync(_offset, [fact]));
    }

    private PostgreSqlPositionLiveStateHistory History() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:PostgreSql"] = fixture.ConnectionString }).Build());

    private static Task<DomainEventDetectionResult> DetectAsync(IPositionLiveStateHistory history, DateTimeOffset? at = null) =>
        new PositionBlockedProlongedDetector(new PersistedPositionBlockedSource(history)).EvaluateAsync(new(
            new(BlockedFacts.Org, OrganizationEventType.PositionBlockedProlonged),
            EventSubscriptionsSnapshot.CreateBuilder(BlockedFacts.Org).AddPosition(BlockedFacts.Position,
                [new(new PositionBlockedParameters(TimeSpan.FromMinutes(30)))]).Build(), null,
            at ?? BlockedFacts.At.AddHours(1))).AsTask();

    private static async Task InsertAsync(PositionLiveStateProjectionFact fact, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO organogram.position_state_projection_facts
                (source, source_offset, persistence_id, persistence_sequence, organization_id,
                 position_id, fact_type, message_id, thread_id, occurred_at_utc, payload)
            VALUES (@source, @offset, @persistence, @sequence, @organization,
                    @position, @type, @message, @thread, @at, @payload);
            """, connection, transaction);
        command.Parameters.AddWithValue("source", fact.Source.ToString());
        command.Parameters.AddWithValue("offset", fact.SourceOffset);
        command.Parameters.AddWithValue("persistence", fact.PersistenceId!);
        command.Parameters.AddWithValue("sequence", fact.PersistenceSequence!.Value);
        command.Parameters.AddWithValue("organization", fact.OrganizationId.Value);
        command.Parameters.AddWithValue("position", fact.PositionId!.Value);
        command.Parameters.AddWithValue("type", fact.FactType);
        command.Parameters.AddWithValue("message", fact.MessageId!.Value);
        command.Parameters.AddWithValue("thread", fact.ThreadId!.Value);
        command.Parameters.AddWithValue("at", fact.OccurredAtUtc);
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = fact.PayloadJson;
        await command.ExecuteNonQueryAsync();
    }
}
