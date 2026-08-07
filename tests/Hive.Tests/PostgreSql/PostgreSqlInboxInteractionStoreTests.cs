using Hive.Domain.Identity;
using Hive.Infrastructure.Inbox.ReadModels;
using Hive.Infrastructure.Inbox.ReadModels.PostgreSql;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlInboxInteractionStoreTests(PostgreSqlFixture fixture)
{
    private static readonly OrganizationId OrganizationId = OrganizationId.From("acme");

    private static readonly PositionId PositionId = PositionId.From("delivery-lead");

    private static readonly MessageId MessageId =
        MessageId.From(Guid.Parse("8f308049-e1ce-4a62-b8f2-d44a15268d9d"));

    private static readonly InboxProjectionItemKey ItemKey =
        new(OrganizationId, PositionId, MessageId);

    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 7, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Interaction_state_is_person_scoped_audited_and_survives_store_restart()
    {
        await ResetMigrateAndInsertItemAsync();
        await using (var store = new PostgreSqlInboxInteractionStore(fixture.ConnectionString))
        {
            await store.ApplyAsync(Mutation(InboxInteractionAction.MarkRead, minute: 1));
            await store.ApplyAsync(Mutation(InboxInteractionAction.StartReply, minute: 2));
            await store.ApplyAsync(Mutation(
                InboxInteractionAction.SaveDraft,
                minute: 3,
                "Initial draft"));
            await store.ApplyAsync(Mutation(
                InboxInteractionAction.SaveDraft,
                minute: 4,
                "Revised draft"));
            await store.ApplyAsync(Mutation(InboxInteractionAction.MarkUnread, minute: 5));
        }

        await using var restarted =
            new PostgreSqlInboxInteractionStore(fixture.ConnectionString);
        var states = await restarted.ReadAsync(
            OrganizationId,
            "person-alice",
            [ItemKey]);
        var state = Assert.Single(states).Value;

        Assert.Equal(InboxInteractionReadState.Unread, state.ReadState);
        Assert.Equal(InboxInteractionReplyState.InProgress, state.ReplyState);
        Assert.Equal("Revised draft", state.DraftText);
        Assert.Equal(StartedAt.AddMinutes(5), state.UpdatedAtUtc);
        Assert.Empty(await restarted.ReadAsync(
            OrganizationId,
            "person-bob",
            [ItemKey]));

        var audit = await restarted.ReadAuditAsync(ItemKey, "person-alice");
        Assert.Equal(
            [
                InboxInteractionAction.MarkRead,
                InboxInteractionAction.StartReply,
                InboxInteractionAction.SaveDraft,
                InboxInteractionAction.SaveDraft,
                InboxInteractionAction.MarkUnread,
            ],
            audit.Select(static entry => entry.Action));
        Assert.False(audit[1].PreviousDraftPresent);
        Assert.True(audit[2].DraftPresent);
        Assert.True(audit[3].PreviousDraftPresent);
        Assert.Equal(InboxInteractionReadState.Read, audit[^1].PreviousReadState);
        Assert.Equal(InboxInteractionReadState.Unread, audit[^1].ReadState);

        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            "SELECT count(*) FROM inbox.human_interactions;");
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    private static InboxInteractionMutation Mutation(
        InboxInteractionAction action,
        int minute,
        string? draftText = null) =>
        new(ItemKey, "person-alice", action, StartedAt.AddMinutes(minute), draftText);

    private async Task ResetMigrateAndInsertItemAsync()
    {
        await fixture.ResetInboxAsync();
        await using var dataSource = fixture.CreateDataSource();
        await new PostgreSqlInboxProjectionMigrator(dataSource).MigrateAsync();
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO inbox.items (
                organization_id,
                assigned_position_id,
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
                last_fact_type,
                last_changed_at_utc)
            VALUES (
                'acme',
                'delivery-lead',
                '8f308049-e1ce-4a62-b8f2-d44a15268d9d',
                'Directive',
                'Position',
                'ceo',
                'Position',
                'delivery-lead',
                'ee8e8d50-2c29-4737-8508-48f9d5d5e67d',
                'Normal',
                '2026-08-07T08:00:00Z',
                NULL,
                FALSE,
                'AwaitingResponse',
                'directive',
                '2026-08-07T08:00:00Z');
            """);
        await command.ExecuteNonQueryAsync();
    }
}
