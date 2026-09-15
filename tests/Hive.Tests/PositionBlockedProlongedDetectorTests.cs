using System.Collections.Immutable;
using Hive.Actors;
using Hive.Actors.Events;
using Hive.Application.Events;
using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Organization.ReadModels;
using Hive.Infrastructure.Organization.ReadModels.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class PositionBlockedProlongedDetectorTests
{
    private static readonly OrganizationId Org = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("engineer");
    private static readonly DateTimeOffset Since = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly EventSourceCorrelation Correlation = new(MessageId.New(), ThreadId.New());
    private static readonly EventSubscription Subscription = new(new PositionBlockedParameters(TimeSpan.FromMinutes(30)));

    [Theory]
    [InlineData(-1L, 0)]
    [InlineData(0L, 1)]
    [InlineData(1L, 1)]
    public async Task Threshold_is_inclusive_and_the_current_period_has_an_open_window(long ticks, int count)
    {
        var at = Since.AddMinutes(30).AddTicks(ticks);
        var result = await new PositionBlockedProlongedDetector(new Source([Period()])).EvaluateAsync(Context(at));
        Assert.Equal(count, result.Occurrences.Length);
        Assert.Null(result.Cursor);
        if (count == 0) return;
        var occurrence = Assert.Single(result.Occurrences);
        var payload = Assert.IsType<PositionBlockedProlongedPayload>(occurrence.Payload);
        Assert.Equal(Since.AddMinutes(30), occurrence.WindowStartsAtUtc);
        Assert.Null(occurrence.WindowEndsAtUtc);
        Assert.Equal(Correlation, payload.Correlation);
        Assert.Equal(at - Since, payload.BlockedDuration);
        Assert.Equal(PositionBlockedCause.PendingEscalation, payload.Cause);
    }

    [Fact]
    public async Task Replay_cause_changes_offsets_and_new_subscriptions_preserve_period_identity()
    {
        var source = new Source([Period()]);
        var at = Since.AddHours(1);
        var first = Assert.Single((await new PositionBlockedProlongedDetector(source).EvaluateAsync(Context(at))).Occurrences);
        var checkpoint = new DomainEventDetectionCheckpoint(Context(at).DetectorId, 1, "old-cursor", at);
        source.Items = [new(Org, Position, Since.ToOffset(TimeSpan.FromHours(3)), PositionBlockedCause.ConfigurationBlocked)];
        var replay = Assert.Single((await new PositionBlockedProlongedDetector(source)
            .EvaluateAsync(Context(at.AddMinutes(1), checkpoint: checkpoint))).Occurrences);
        Assert.Equal(first.Key, replay.Key);
        Assert.Null(replay.Payload.Correlation);

        var other = PositionId.From("lead");
        var snapshot = EventSubscriptionsSnapshot.CreateBuilder(Org)
            .AddPosition(Position, [Subscription, new(new PositionBlockedParameters(TimeSpan.FromHours(1)), isCritical: true)])
            .AddPosition(other, [Subscription]).Build();
        source.Items = [Period(), new(Org, other, Since, PositionBlockedCause.ConfigurationBlocked)];
        var expanded = await new PositionBlockedProlongedDetector(source).EvaluateAsync(Context(at.AddMinutes(2), snapshot, checkpoint));
        Assert.Equal(3, expanded.Occurrences.Length);
        Assert.Equal(3, expanded.Occurrences.Select(item => item.Key).Distinct().Count());
        Assert.Equal(expanded.Occurrences.OrderBy(item => item.Key.Value, StringComparer.Ordinal), expanded.Occurrences);
        Assert.Contains(expanded.Occurrences, item => item.Subscriber.Subscription.IsCritical);
        Assert.Equal(2, source.Positions!.Count);

        source.Items = [];
        Assert.Empty((await new PositionBlockedProlongedDetector(source).EvaluateAsync(Context(at.AddMinutes(3)))).Occurrences);
        source.Items = [new(Org, Position, Since.AddMinutes(1), PositionBlockedCause.PendingEscalation, Correlation)];
        Assert.NotEqual(first.Key, Assert.Single((await new PositionBlockedProlongedDetector(source).EvaluateAsync(Context(at))).Occurrences).Key);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Invalid_scope_or_multiple_current_periods_fail_closed(int invalid)
    {
        var items = invalid switch
        {
            0 => ImmutableArray.Create(new CurrentPositionBlockedPeriod(OrganizationId.From("other"), Position, Since, PositionBlockedCause.ConfigurationBlocked)),
            1 => [new(Org, PositionId.From("other"), Since, PositionBlockedCause.ConfigurationBlocked)],
            _ => [Period(), Period()],
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PositionBlockedProlongedDetector(new Source(items))
            .EvaluateAsync(Context(Since.AddHours(1))).AsTask());
    }

    [Fact]
    public async Task Failures_and_cancellation_never_commit_and_empty_subscriptions_do_not_read()
    {
        var source = new Source([]) { Failure = new IOException("offline") };
        var detector = new PositionBlockedProlongedDetector(source);
        var store = new NoCommitStore();
        var cycle = new DomainEventDetectionCycle(store, new Clock(), TimeSpan.FromMinutes(1));
        Assert.Same(source.Failure, await Assert.ThrowsAsync<IOException>(() => cycle.EvaluateAsync(detector, Snapshot()).AsTask()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle.EvaluateAsync(detector, Snapshot(), cancellation.Token).AsTask());
        source.Failure = null;
        using var during = new CancellationTokenSource();
        source.AfterRead = during.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle.EvaluateAsync(detector, Snapshot(), during.Token).AsTask());
        Assert.Equal(0, store.Commits);
        var reads = source.Reads;
        Assert.Empty((await detector.EvaluateAsync(Context(Since, EventSubscriptionsSnapshot.CreateBuilder(Org).Build()))).Occurrences);
        Assert.Equal(reads, source.Reads);
    }

    [Fact]
    public async Task Invalid_contracts_and_missing_storage_fail_explicitly()
    {
        var detector = new PositionBlockedProlongedDetector(new Source([]));
        var wrong = new DomainEventDetectionContext(new(Org, OrganizationEventType.DirectiveDeadlineApproaching), Snapshot(), null, Since);
        await Assert.ThrowsAsync<ArgumentException>(() => detector.EvaluateAsync(wrong).AsTask());
        Assert.Throws<ArgumentException>(() => new CurrentPositionBlockedPeriod(Org, Position, Since, PositionBlockedCause.PendingEscalation));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CurrentPositionBlockedPeriod(Org, Position, Since, (PositionBlockedCause)99));
        Assert.Throws<ArgumentException>(() => new CurrentPositionBlockedPeriod(Org, Position, default, PositionBlockedCause.ConfigurationBlocked));
        await using var history = new PostgreSqlPositionLiveStateHistory(new ConfigurationBuilder().Build());
        await Assert.ThrowsAsync<InvalidOperationException>(() => history.ReadAsync(Org).AsTask());
        Assert.Empty((await new PositionBlockedProlongedDetector(new Source([Period()])).EvaluateAsync(Context(Since.AddTicks(-1)))).Occurrences);
    }

    [Fact]
    public async Task Bootstrap_composes_persisted_source_and_both_detectors()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.AddHiveBootstrap();
        builder.AddHiveActorSystem();
        await using var services = builder.Services.BuildServiceProvider();
        Assert.IsType<PostgreSqlPositionLiveStateHistory>(services.GetRequiredService<IPositionLiveStateHistory>());
        Assert.IsType<PersistedPositionBlockedSource>(services.GetRequiredService<IPositionBlockedSource>());
        Assert.IsType<PositionBlockedProlongedDetector>(Assert.Single(services.GetServices<IDomainEventDetector>(),
            detector => detector.EventType == OrganizationEventType.PositionBlockedProlonged));
        Assert.Equal(2, services.GetServices<IDomainEventDetector>().Count());
    }

    private static CurrentPositionBlockedPeriod Period() => new(Org, Position, Since, PositionBlockedCause.PendingEscalation, Correlation);
    private static EventSubscriptionsSnapshot Snapshot() => EventSubscriptionsSnapshot.CreateBuilder(Org).AddPosition(Position, [Subscription]).Build();
    private static DomainEventDetectionContext Context(DateTimeOffset at, EventSubscriptionsSnapshot? snapshot = null,
        DomainEventDetectionCheckpoint? checkpoint = null) => new(new(Org, OrganizationEventType.PositionBlockedProlonged), snapshot ?? Snapshot(), checkpoint, at);

    private sealed class Source(ImmutableArray<CurrentPositionBlockedPeriod> items) : IPositionBlockedSource
    {
        public ImmutableArray<CurrentPositionBlockedPeriod> Items { get; set; } = items;
        public Exception? Failure { get; set; }
        public Action? AfterRead { get; set; }
        public int Reads { get; private set; }
        public IReadOnlyCollection<PositionId>? Positions { get; private set; }
        public ValueTask<ImmutableArray<CurrentPositionBlockedPeriod>> ReadAsync(OrganizationId organizationId,
            IReadOnlyCollection<PositionId> positionIds, CancellationToken cancellationToken = default)
        {
            Reads++;
            Positions = positionIds;
            if (Failure is not null) throw Failure;
            AfterRead?.Invoke();
            return new(Items);
        }
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Since.AddHours(1); }
    private sealed class NoCommitStore : IDomainEventDetectionStore
    {
        public int Commits { get; private set; }
        public ValueTask<DomainEventDetectionCheckpoint?> ReadCheckpointAsync(DomainEventDetectorId detectorId, CancellationToken cancellationToken = default) => new((DomainEventDetectionCheckpoint?)null);
        public ValueTask<bool> TryCommitAsync(DomainEventDetectionCommit commit, CancellationToken cancellationToken = default) { Commits++; return new(true); }
    }
}
