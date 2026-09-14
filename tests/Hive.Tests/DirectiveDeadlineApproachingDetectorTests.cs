using System.Collections.Immutable;
using Hive.Actors;
using Hive.Application.Events;
using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Infrastructure.Events;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class DirectiveDeadlineApproachingDetectorTests
{
    private static readonly OrganizationId Org = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("engineer");
    private static readonly DateTimeOffset Deadline = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly EventSourceCorrelation Correlation = new(MessageId.New(), ThreadId.New(), DirectiveId.New());
    private static readonly EventSubscription Subscription = new(new DirectiveDeadlineParameters(TimeSpan.FromMinutes(30)));

    [Theory]
    [InlineData(-18000000001L, 0)]
    [InlineData(-18000000000L, 1)]
    [InlineData(-1L, 1)]
    [InlineData(0L, 0)]
    [InlineData(1L, 0)]
    public async Task Window_start_is_inclusive_and_deadline_exclusive(long ticks, int count)
    {
        var source = new Source([Candidate()]);
        var result = await new DirectiveDeadlineApproachingDetector(source).EvaluateAsync(Context(Deadline.AddTicks(ticks)));
        Assert.Equal(count, result.Occurrences.Length);
        Assert.Null(result.Cursor);
        if (count == 0) return;
        var occurrence = Assert.Single(result.Occurrences);
        var payload = Assert.IsType<DirectiveDeadlineApproachingPayload>(occurrence.Payload);
        Assert.Equal(Correlation, payload.Correlation);
        Assert.Equal(Deadline.AddMinutes(-30), occurrence.WindowStartsAtUtc);
        Assert.Equal(Deadline, occurrence.WindowEndsAtUtc);
        Assert.Equal(TimeSpan.FromTicks(-ticks), payload.Remaining);
    }

    [Fact]
    public async Task Current_candidates_are_revisited_with_new_parameters_positions_and_after_restart()
    {
        var source = new Source([Candidate()]);
        var before = await new DirectiveDeadlineApproachingDetector(source).EvaluateAsync(Context(Deadline.AddMinutes(-31)));
        Assert.Empty(before.Occurrences);
        var at = Deadline.AddMinutes(-20);
        var first = await new DirectiveDeadlineApproachingDetector(source).EvaluateAsync(Context(at));
        var checkpoint = new DomainEventDetectionCheckpoint(Context(at).DetectorId, 1, null, at);
        var replay = await new DirectiveDeadlineApproachingDetector(source).EvaluateAsync(Context(at.AddMinutes(1), checkpoint: checkpoint));
        Assert.Equal(Assert.Single(first.Occurrences).Key, Assert.Single(replay.Occurrences).Key);

        var other = PositionId.From("lead");
        var extra = new EventSubscription(new DirectiveDeadlineParameters(TimeSpan.FromHours(1)));
        var subscriptions = EventSubscriptionsSnapshot.CreateBuilder(Org)
            .AddPosition(Position, [Subscription, extra]).AddPosition(other, [Subscription]).Build();
        source.Items = [Candidate(), new(Org, other, Correlation, Deadline),
            new(Org, Position, new(MessageId.New(), ThreadId.New(), DirectiveId.New()), Deadline.AddMinutes(5))];
        var expanded = await new DirectiveDeadlineApproachingDetector(source).EvaluateAsync(Context(at.AddMinutes(2), subscriptions, checkpoint));
        Assert.Equal(5, expanded.Occurrences.Length);
        Assert.Equal(5, expanded.Occurrences.Select(item => item.Key).Distinct().Count());
        Assert.Equal(expanded.Occurrences.OrderBy(item => item.Key.Value, StringComparer.Ordinal), expanded.Occurrences);
        Assert.Equal(2, source.Positions!.Count);
        source.Items = [];
        Assert.Empty((await new DirectiveDeadlineApproachingDetector(source).EvaluateAsync(Context(at.AddMinutes(3)))).Occurrences);
    }

    [Fact]
    public async Task Offset_changes_preserve_key_but_a_changed_deadline_creates_a_new_occurrence()
    {
        var source = new Source([Candidate()]);
        var detector = new DirectiveDeadlineApproachingDetector(source);
        var first = Assert.Single((await detector.EvaluateAsync(Context(Deadline.AddMinutes(-20)))).Occurrences);
        source.Items = [new(Org, Position, Correlation, Deadline.ToOffset(TimeSpan.FromHours(5)))];
        Assert.Equal(first.Key, Assert.Single((await detector.EvaluateAsync(Context(Deadline.AddMinutes(-19)))).Occurrences).Key);
        source.Items = [new(Org, Position, Correlation, Deadline.AddMinutes(1))];
        Assert.NotEqual(first.Key, Assert.Single((await detector.EvaluateAsync(Context(Deadline.AddMinutes(-19)))).Occurrences).Key);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Source_cannot_leak_organizations_or_unrequested_positions(bool otherOrganization)
    {
        var source = new Source([new(otherOrganization ? OrganizationId.From("other") : Org,
            otherOrganization ? Position : PositionId.From("other"), Correlation, Deadline)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DirectiveDeadlineApproachingDetector(source)
            .EvaluateAsync(Context(Deadline.AddMinutes(-20))).AsTask());
    }

    [Fact]
    public async Task Failures_and_cancellation_do_not_commit_and_empty_subscriptions_do_not_read()
    {
        var failure = new IOException("offline");
        var source = new Source([]) { Failure = failure };
        var detector = new DirectiveDeadlineApproachingDetector(source);
        var store = new NoCommitStore();
        var cycle = new DomainEventDetectionCycle(store, new Clock(Deadline.AddMinutes(-20)), TimeSpan.FromMinutes(1));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => cycle.EvaluateAsync(detector, Snapshot()).AsTask()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle.EvaluateAsync(detector, Snapshot(), cancellation.Token).AsTask());
        source.Failure = null;
        using var during = new CancellationTokenSource();
        source.AfterRead = during.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle.EvaluateAsync(detector, Snapshot(), during.Token).AsTask());
        Assert.Equal(0, store.Commits);
        var reads = source.Reads;
        Assert.Empty((await detector.EvaluateAsync(Context(Deadline, EventSubscriptionsSnapshot.CreateBuilder(Org).Build()))).Occurrences);
        Assert.Equal(reads, source.Reads);
    }

    [Fact]
    public async Task Wrong_detector_context_and_unconfigured_storage_fail_explicitly()
    {
        var context = new DomainEventDetectionContext(new(Org, OrganizationEventType.PositionBlockedProlonged), Snapshot(), null, Deadline);
        await Assert.ThrowsAsync<ArgumentException>(() => new DirectiveDeadlineApproachingDetector(new Source([])).EvaluateAsync(context).AsTask());
        await using var source = new PostgreSqlDirectiveDeadlineSource(new ConfigurationBuilder().Build());
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(Org, [Position], Deadline).AsTask());
        Assert.Throws<ArgumentNullException>(() => new OpenDirectiveDeadline(Org, Position, new(Correlation.MessageId, Correlation.ThreadId), Deadline));
    }

    [Fact]
    public async Task Bootstrap_composes_detector_with_persisted_source_without_starting_polling()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.AddHiveBootstrap();
        builder.AddHiveActorSystem();
        await using var services = builder.Services.BuildServiceProvider();
        Assert.IsType<PostgreSqlDirectiveDeadlineSource>(services.GetRequiredService<IDirectiveDeadlineSource>());
        Assert.IsType<DirectiveDeadlineApproachingDetector>(Assert.Single(services.GetServices<IDomainEventDetector>()));
    }

    private static OpenDirectiveDeadline Candidate() => new(Org, Position, Correlation, Deadline);
    private static EventSubscriptionsSnapshot Snapshot() => EventSubscriptionsSnapshot.CreateBuilder(Org).AddPosition(Position, [Subscription]).Build();
    private static DomainEventDetectionContext Context(DateTimeOffset at, EventSubscriptionsSnapshot? subscriptions = null,
        DomainEventDetectionCheckpoint? checkpoint = null) => new(new(Org, OrganizationEventType.DirectiveDeadlineApproaching), subscriptions ?? Snapshot(), checkpoint, at);

    private sealed class Source(ImmutableArray<OpenDirectiveDeadline> items) : IDirectiveDeadlineSource
    {
        public ImmutableArray<OpenDirectiveDeadline> Items { get; set; } = items;
        public Exception? Failure { get; set; }
        public Action? AfterRead { get; set; }
        public int Reads { get; private set; }
        public IReadOnlyCollection<PositionId>? Positions { get; private set; }
        public ValueTask<ImmutableArray<OpenDirectiveDeadline>> ReadAsync(OrganizationId organizationId,
            IReadOnlyCollection<PositionId> positionIds, DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken = default)
        {
            Reads++;
            Positions = positionIds;
            if (Failure is not null) throw Failure;
            AfterRead?.Invoke();
            return new(Items);
        }
    }
    private sealed class Clock(DateTimeOffset at) : TimeProvider { public override DateTimeOffset GetUtcNow() => at; }
    private sealed class NoCommitStore : IDomainEventDetectionStore
    {
        public int Commits { get; private set; }
        public ValueTask<DomainEventDetectionCheckpoint?> ReadCheckpointAsync(DomainEventDetectorId detectorId, CancellationToken cancellationToken = default) => new((DomainEventDetectionCheckpoint?)null);
        public ValueTask<bool> TryCommitAsync(DomainEventDetectionCommit commit, CancellationToken cancellationToken = default) { Commits++; return new(true); }
    }
}
