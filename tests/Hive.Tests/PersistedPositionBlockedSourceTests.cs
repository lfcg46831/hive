using System.Collections.Immutable;
using System.Text;
using Hive.Actors.Events;
using Hive.Actors.Positions;
using Hive.Actors.Serialization;
using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Organization.ReadModels;

namespace Hive.Tests;

public sealed class PersistedPositionBlockedSourceTests
{
    [Fact]
    public async Task Partial_resolution_noise_and_replay_preserve_start_and_select_pending_correlation()
    {
        var first = BlockedFacts.Escalation(0);
        var second = BlockedFacts.Escalation(1);
        var third = BlockedFacts.Escalation(3);
        var history = new History([BlockedFacts.Fact(first, 1), BlockedFacts.Fact(second, 2)]);
        var period = Assert.Single(await ReadAsync(history));
        Assert.Equal(first.SentAt, period.BlockedSinceUtc);
        Assert.Equal(first.Id, period.Correlation!.MessageId);
        // Receipt at the lead affects the emitting engineer, never the lead itself.
        Assert.Empty(await new PersistedPositionBlockedSource(history).ReadAsync(BlockedFacts.Org, [BlockedFacts.Lead]));
        history.Facts = history.Facts.Add(BlockedFacts.Fact(BlockedFacts.Resolution(first, 2), 3))
            .Add(BlockedFacts.Fact(third, 4)).Add(BlockedFacts.Fact(second, 5));
        period = Assert.Single(await ReadAsync(history));
        Assert.Equal(first.SentAt, period.BlockedSinceUtc);
        Assert.Equal(second.Id, period.Correlation!.MessageId);
        Assert.Equal(second.Thread, period.Correlation.ThreadId);
        var unrelated = BlockedFacts.Escalation(4);
        history.Facts = history.Facts.Add(BlockedFacts.Fact(BlockedFacts.Resolution(unrelated, 5), 6));
        Assert.Equal(period, Assert.Single(await ReadAsync(history)));
        history.Facts = history.Facts.Add(BlockedFacts.Fact(BlockedFacts.Resolution(second, 6), 7));
        Assert.Equal(third.Id, Assert.Single(await ReadAsync(history)).Correlation!.MessageId);
        history.Facts = history.Facts.Add(BlockedFacts.Fact(BlockedFacts.Resolution(third, 7), 8));
        Assert.Empty(await ReadAsync(history));
        var next = BlockedFacts.Escalation(8);
        history.Facts = history.Facts.Add(BlockedFacts.Fact(next, 9));
        Assert.Equal(next.SentAt, Assert.Single(await ReadAsync(history)).BlockedSinceUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Canonical_fold_preserves_start_across_causes_and_restarts_after_offline(int offlineCondition)
    {
        var offline = (PositionLiveStateCondition)offlineCondition;
        var mapper = new PositionLiveStateFactMapper();
        var entity = PositionEntityId.From(BlockedFacts.Org, BlockedFacts.Position);
        var escalation = BlockedFacts.Escalation(0);
        mapper.Apply(BlockedFacts.Fact(escalation, 1));
        mapper.Apply(new PositionLiveStateConditionFact(entity, PositionLiveStateCondition.ConfigurationBlocked, true, BlockedFacts.At.AddMinutes(1)));
        var configured = mapper.CurrentBlockedPeriod(entity)!;
        Assert.Equal(BlockedFacts.At, configured.BlockedSinceUtc);
        Assert.Equal(PositionBlockedCause.ConfigurationBlocked, configured.Cause);
        Assert.Null(configured.Correlation);
        mapper.Apply(new PositionLiveStateConditionFact(entity, PositionLiveStateCondition.ConfigurationBlocked, false, BlockedFacts.At.AddMinutes(2)));
        Assert.Equal(BlockedFacts.At, mapper.CurrentBlockedPeriod(entity)!.BlockedSinceUtc);
        Assert.Equal(escalation.Id, mapper.CurrentBlockedPeriod(entity)!.Correlation!.MessageId);
        mapper.Apply(new PositionLiveStateConditionFact(entity, offline, true, BlockedFacts.At.AddMinutes(3)));
        Assert.Null(mapper.CurrentBlockedPeriod(entity));
        mapper.Apply(new PositionLiveStateConditionFact(entity, offline, false, BlockedFacts.At.AddMinutes(4)));
        Assert.Equal(BlockedFacts.At.AddMinutes(4), mapper.CurrentBlockedPeriod(entity)!.BlockedSinceUtc);
        mapper.Apply(new PositionLiveStateConditionFact(entity, PositionLiveStateCondition.ConfigurationBlocked, true, BlockedFacts.At.AddMinutes(5)));
        mapper.Apply(BlockedFacts.Fact(BlockedFacts.Resolution(escalation, 6), 2));
        Assert.Equal(BlockedFacts.At.AddMinutes(4), mapper.CurrentBlockedPeriod(entity)!.BlockedSinceUtc);
        mapper.Apply(new PositionLiveStateConditionFact(entity, PositionLiveStateCondition.ConfigurationBlocked, false, BlockedFacts.At.AddMinutes(7)));
        Assert.Null(mapper.CurrentBlockedPeriod(entity));
    }

    [Fact]
    public async Task Invalid_history_and_cross_organization_data_fail_and_cancellation_propagates()
    {
        var foreign = BlockedFacts.Escalation(0, OrganizationId.From("other"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(new History([BlockedFacts.Fact(foreign, 1)])));
        var malformed = new PositionLiveStateProjectionFact(PositionLiveStateProjectionSource.OrganizationalMessage,
            1, BlockedFacts.Org, "escalation", BlockedFacts.At, "{}", BlockedFacts.Lead, "position:acme/lead", 1,
            MessageId.New(), ThreadId.New());
        await Assert.ThrowsAnyAsync<Exception>(() => ReadAsync(new History([malformed])));
        using var cancellation = new CancellationTokenSource();
        var history = new History([]) { AfterRead = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PersistedPositionBlockedSource(history)
            .ReadAsync(BlockedFacts.Org, [BlockedFacts.Position], cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PersistedPositionBlockedSource(history)
            .ReadAsync(BlockedFacts.Org, [BlockedFacts.Position], cancellation.Token).AsTask());
    }

    private static Task<ImmutableArray<CurrentPositionBlockedPeriod>> ReadAsync(History history) =>
        new PersistedPositionBlockedSource(history).ReadAsync(BlockedFacts.Org, [BlockedFacts.Position]).AsTask();

    private sealed class History(ImmutableArray<PositionLiveStateProjectionFact> facts) : IPositionLiveStateHistory
    {
        public ImmutableArray<PositionLiveStateProjectionFact> Facts { get; set; } = facts;
        public Action? AfterRead { get; init; }
        public ValueTask<ImmutableArray<PositionLiveStateProjectionFact>> ReadAsync(OrganizationId organizationId, CancellationToken cancellationToken = default)
        {
            AfterRead?.Invoke();
            return new(Facts);
        }
    }
}

internal static class BlockedFacts
{
    internal static readonly DateTimeOffset At = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
    internal static readonly OrganizationId Org = OrganizationId.From("acme");
    internal static readonly PositionId Position = PositionId.From("engineer");
    internal static readonly PositionId Lead = PositionId.From("lead");

    internal static Escalation Escalation(int minute, OrganizationId? organization = null, PositionId? position = null) => new(
        MessageId.New(), organization ?? Org, new PositionEndpointRef(position ?? Position), new PositionEndpointRef(Lead),
        ThreadId.New(), Priority.High, 1, At.AddMinutes(minute), null, "private issue", "private context", ["private option"]);

    internal static Directive Resolution(Escalation escalation, int minute) => new(MessageId.New(), escalation.OrganizationId,
        escalation.To, escalation.From, escalation.Thread, Priority.Normal, 1, At.AddMinutes(minute), null,
        DirectiveId.New(), null, "private resolution", "private context");

    internal static PositionLiveStateProjectionFact Fact(OrgMessage message, long offset) => new(
        PositionLiveStateProjectionSource.OrganizationalMessage, offset, message.OrganizationId,
        OrgMessageManifests.ForType(message.GetType()), message.SentAt.AddSeconds(1),
        Encoding.UTF8.GetString(OrgMessageJsonFormat.Serialize(message)), ((PositionEndpointRef)message.To).PositionId,
        $"position:{message.OrganizationId}/{((PositionEndpointRef)message.To).PositionId}", offset, message.Id, message.Thread);
}
