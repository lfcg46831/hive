using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests.PostgreSql;

/// <summary>
/// Verifies US-F0-06-T14c: passivated sharded positions reactivate after an agent-node restart,
/// recover persisted state and reject redelivered messages instead of duplicating them.
/// </summary>
[Collection(AkkaPostgreSqlCollection.Name)]
public sealed class PostgreSqlPositionShardingMultiNodeTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset At = new(2026, 6, 28, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Passivated_position_recovers_after_agent_node_restart_and_rejects_duplicate_message()
    {
        await fixture.ResetRegistryAsync();
        await fixture.ResetPersistenceAsync();

        await using var cluster = await PositionShardingMultiNodeFixture.StartAsync(
            persistenceConnectionString: fixture.ConnectionString);
        var entity = PositionEntityId.From(
            OrganizationId.From("acme"),
            PositionId.From("restart-safe-position"));
        var message = SampleMessage(
            entity,
            MessageId("aaaaaaaa-0000-0000-0000-000000001401"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000001401"));

        await cluster.ChangeOccupantAsync(entity, OccupantId.From("agent-7"), OccupantType.AiAgent);
        await cluster.AcceptMessageAsync(entity, message);
        await cluster.WaitForMessageProcessingCompletedAsync(entity, message.Id);
        await cluster.PassivateAsync(entity, "idle-after-processing");
        await cluster.WaitForEntityInactiveAsync(entity);

        await cluster.RestartAgentNodeAsync(cluster.AgentNodes[0].Name);
        cluster.SendAcceptMessage(entity, message);

        await cluster.WaitForDuplicateRejectedAsync(entity, message.Id);

        Assert.Equal(1, cluster.CommittedEvents<MessageReceived>(entity)
            .Count(committed => committed.Event.Message.Id == message.Id));
        Assert.Contains(
            cluster.ProjectionEvents<PositionReactivated>(entity),
            reactivated => reactivated.LastConfigurationStamp is not null);
    }

    private static Memo SampleMessage(
        PositionEntityId entity,
        MessageId id,
        ThreadId thread) =>
        new(
            id,
            entity.Organization,
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(entity.Position),
            thread,
            Priority.Normal,
            schemaVersion: 1,
            sentAt: At,
            deadline: null,
            body: "Customer reported a regression.");

    private static MessageId MessageId(string value) =>
        Hive.Domain.Identity.MessageId.From(new Guid(value));

    private static ThreadId ThreadId(string value) =>
        Hive.Domain.Identity.ThreadId.From(new Guid(value));
}
