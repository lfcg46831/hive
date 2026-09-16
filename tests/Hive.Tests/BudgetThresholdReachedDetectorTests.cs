using System.Collections.Immutable;
using Hive.Actors;
using Hive.Application.Events;
using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class BudgetThresholdReachedDetectorTests
{
    private static readonly OrganizationId Org = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("engineer");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly CivilDay Today = CivilDay.Containing(Now, Utc);
    private static readonly EventSubscription Eighty = new(new BudgetThresholdParameters(80));

    [Theory]
    [InlineData(3.99, 0)]
    [InlineData(4.00, 1)]
    [InlineData(4.01, 1)]
    public async Task Threshold_is_inclusive_and_identifies_the_crossing_fact(double second, int count)
    {
        var first = Fact(Now.AddHours(-3), 1m);
        var crossing = Fact(Now.AddHours(-2), (decimal)second - 1m);
        var result = await Detect(new Source([Usage(total: 5m, facts: [crossing, first, Fact(Now.AddHours(-1), 3m)])]),
            Context(Now));
        var occurrences = result.Occurrences.Where(item => ((BudgetThresholdReachedPayload)item.Payload).ConsumedEur == (decimal)second).ToArray();
        Assert.Null(result.Cursor);
        Assert.Single(result.Occurrences);
        if (count == 0)
        {
            // Not reached at the second fact: the third fact becomes the crossing fact.
            Assert.Empty(occurrences);
            Assert.Equal(Now.AddHours(-1), Assert.Single(result.Occurrences).WindowStartsAtUtc);
            return;
        }

        var occurrence = Assert.Single(occurrences);
        var payload = Assert.IsType<BudgetThresholdReachedPayload>(occurrence.Payload);
        Assert.Equal(crossing.Correlation, payload.Correlation);
        Assert.Equal(DailyBudgetKind.Total, payload.Budget);
        Assert.Equal(Today.Date, payload.CivilDate);
        Assert.Equal("UTC", payload.TimeZone);
        Assert.Equal(5m, payload.LimitEur);
        Assert.Equal(Now.AddHours(-2), occurrence.WindowStartsAtUtc);
        Assert.Equal(Today.EndsAtUtc, occurrence.WindowEndsAtUtc);
        Assert.Equal(Now, payload.ObservedAtUtc);
    }

    [Fact]
    public async Task Budgets_use_their_own_categories_and_skip_absent_or_zero_caps()
    {
        var reactive = Fact(Now.AddHours(-4), 2m, BudgetCostCategory.Reactive);
        var proactive = Fact(Now.AddHours(-3), 1m, BudgetCostCategory.Proactive);
        var unclassified = Fact(Now.AddHours(-2), 3m, BudgetCostCategory.Unclassified);
        var usage = Usage(reactive: 2m, proactive: 1m, total: 6m, facts: [reactive, proactive, unclassified]);
        var result = await Detect(new Source([usage]), Context(Now));
        var byBudget = result.Occurrences.ToDictionary(item => ((BudgetThresholdReachedPayload)item.Payload).Budget);
        Assert.Equal(3, byBudget.Count);
        Assert.Equal(reactive.Correlation, byBudget[DailyBudgetKind.Reactive].Payload.Correlation);
        Assert.Equal(proactive.Correlation, byBudget[DailyBudgetKind.Proactive].Payload.Correlation);
        Assert.Equal(unclassified.Correlation, byBudget[DailyBudgetKind.Total].Payload.Correlation);
        Assert.Equal(6m, ((BudgetThresholdReachedPayload)byBudget[DailyBudgetKind.Total].Payload).ConsumedEur);
        Assert.Equal(result.Occurrences.OrderBy(item => item.Key.Value, StringComparer.Ordinal), result.Occurrences);

        // Unclassified cost never reaches reactive/proactive; zero and absent caps never alert.
        var onlyUnclassified = Usage(reactive: 1m, proactive: 0m, facts: [unclassified]);
        Assert.Empty((await Detect(new Source([onlyUnclassified]), Context(Now))).Occurrences);
        Assert.Empty((await Detect(new Source([Usage(total: 0m, facts: [unclassified])]), Context(Now))).Occurrences);
    }

    [Fact]
    public async Task Multiple_percentages_positions_and_critical_subscriptions_produce_distinct_keys()
    {
        var other = PositionId.From("lead");
        var snapshot = EventSubscriptionsSnapshot.CreateBuilder(Org)
            .AddPosition(Position, [Eighty, new(new BudgetThresholdParameters(100), isCritical: true)])
            .AddPosition(other, [Eighty]).Build();
        var source = new Source([
            Usage(total: 10m, facts: [Fact(Now.AddHours(-2), 8m), Fact(Now.AddHours(-1), 2m)]),
            Usage(position: other, total: 1m, facts: [Fact(Now.AddMinutes(-1), 0.8m)])]);
        var result = await Detect(source, Context(Now, snapshot));
        Assert.Equal(3, result.Occurrences.Length);
        Assert.Equal(3, result.Occurrences.Select(item => item.Key).Distinct().Count());
        Assert.Contains(result.Occurrences, item => item.Subscriber.Subscription.IsCritical
            && ((BudgetThresholdReachedPayload)item.Payload).ConsumedEur == 10m);
        Assert.Equal(2, source.Positions!.Count);
    }

    [Fact]
    public async Task Replay_late_facts_and_cap_changes_preserve_the_daily_identity()
    {
        var source = new Source([Usage(total: 10m, facts: [Fact(Now.AddHours(-2), 9m)])]);
        var first = Assert.Single((await Detect(source, Context(Now))).Occurrences);
        var checkpoint = new DomainEventDetectionCheckpoint(Context(Now).DetectorId, 1, null, Now);
        var late = Fact(Now.AddHours(-5), 8m);
        source.Items = [Usage(total: 9m, facts: [Fact(Now.AddHours(-2), 9m), late])];
        var replay = Assert.Single((await Detect(source, Context(Now.AddMinutes(1), checkpoint: checkpoint))).Occurrences);
        Assert.Equal(first.Key, replay.Key);
        Assert.Equal(late.Correlation, replay.Payload.Correlation);

        var tomorrow = CivilDay.Containing(Today.EndsAtUtc, Utc);
        source.Items = [Usage(total: 10m, day: tomorrow, facts: [Fact(tomorrow.StartsAtUtc, 9m)])];
        var next = Assert.Single((await Detect(source, Context(tomorrow.StartsAtUtc))).Occurrences);
        Assert.NotEqual(first.Key, next.Key);
    }

    [Fact]
    public async Task Civil_day_uses_the_position_timezone_across_daylight_saving_changes()
    {
        var lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
        var shortDay = CivilDay.Containing(new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero), lisbon);
        Assert.Equal(new DateOnly(2026, 3, 29), shortDay.Date);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero), shortDay.StartsAtUtc);
        Assert.Equal(TimeSpan.FromHours(23), shortDay.EndsAtUtc - shortDay.StartsAtUtc);
        var longDay = CivilDay.Containing(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero), lisbon);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 23, 0, 0, TimeSpan.Zero), longDay.StartsAtUtc);
        Assert.Equal(TimeSpan.FromHours(25), longDay.EndsAtUtc - longDay.StartsAtUtc);
        Assert.Equal(longDay, CivilDay.Containing(longDay.EndsAtUtc.AddTicks(-1), lisbon));
        Assert.NotEqual(longDay, CivilDay.Containing(longDay.EndsAtUtc, lisbon));

        var summer = CivilDay.Containing(Now, lisbon);
        var fact = Fact(summer.StartsAtUtc, 5m);
        var usage = new PositionDailyBudgetUsage(Org, Position, "Europe/Lisbon", summer, null, null, 5m, [fact]);
        var occurrence = Assert.Single((await Detect(new Source([usage]), Context(Now))).Occurrences);
        var payload = (BudgetThresholdReachedPayload)occurrence.Payload;
        Assert.Equal("Europe/Lisbon", payload.TimeZone);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 23, 0, 0, TimeSpan.Zero), occurrence.WindowStartsAtUtc);
        Assert.Equal(summer.EndsAtUtc, occurrence.WindowEndsAtUtc);
        Assert.Throws<ArgumentException>(() => new PositionDailyBudgetUsage(Org, Position, "Europe/Lisbon", Today, null, null, 5m, []));
        Assert.Throws<ArgumentException>(() => Usage(total: 1m, facts: [Fact(Today.EndsAtUtc, 1m)]));
    }

    [Fact]
    public void Threshold_arithmetic_is_exact_for_tiny_and_large_amounts()
    {
        Assert.True(BudgetThreshold.IsReached(0.0000000000000000000000000008m, 0.000000000000000000000000001m, 80));
        Assert.False(BudgetThreshold.IsReached(0.0000000000000000000000000007m, 0.000000000000000000000000001m, 80));
        Assert.True(BudgetThreshold.IsReached(decimal.MaxValue, decimal.MaxValue, 100));
        Assert.False(BudgetThreshold.IsReached(decimal.MaxValue - 1m, decimal.MaxValue, 100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Invalid_source_scope_fails_closed(int invalid)
    {
        var usage = invalid switch
        {
            0 => ImmutableArray.Create(new PositionDailyBudgetUsage(OrganizationId.From("other"), Position, "UTC", Today, null, null, 1m, [])),
            1 => [Usage(position: PositionId.From("other"), total: 1m)],
            2 => [Usage(total: 1m), Usage(total: 1m)],
            3 => [Usage(total: 1m, day: CivilDay.Containing(Today.EndsAtUtc, Utc))],
            _ => [Usage(total: 1m, facts: [Fact(Now.AddTicks(1), 1m)])],
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Detect(new Source(usage), Context(Now)));
    }

    [Fact]
    public async Task Failures_and_cancellation_never_commit_and_empty_subscriptions_do_not_read()
    {
        var source = new Source([]) { Failure = new IOException("offline") };
        var detector = new BudgetThresholdReachedDetector(source);
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
        Assert.Empty((await detector.EvaluateAsync(Context(Now, EventSubscriptionsSnapshot.CreateBuilder(Org).Build()))).Occurrences);
        Assert.Equal(reads, source.Reads);
    }

    [Fact]
    public async Task Invalid_contracts_and_missing_storage_fail_explicitly()
    {
        var detector = new BudgetThresholdReachedDetector(new Source([]));
        var wrong = new DomainEventDetectionContext(new(Org, OrganizationEventType.PositionBlockedProlonged), Snapshot(), null, Now);
        await Assert.ThrowsAsync<ArgumentException>(() => detector.EvaluateAsync(wrong).AsTask());
        Assert.Throws<ArgumentOutOfRangeException>(() => Fact(Now, -0.01m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fact(Now, 1m, (BudgetCostCategory)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Usage(total: -1m));
        await using var source = new PostgreSqlBudgetThresholdSource(new ConfigurationBuilder().Build());
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(Org, [Position], Now).AsTask());
    }

    [Fact]
    public async Task Bootstrap_composes_the_persisted_source_and_one_detector_per_type()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.AddHiveBootstrap();
        builder.AddHiveActorSystem();
        await using var services = builder.Services.BuildServiceProvider();
        Assert.IsType<PostgreSqlBudgetThresholdSource>(services.GetRequiredService<IBudgetThresholdSource>());
        var detectors = services.GetServices<IDomainEventDetector>().ToArray();
        Assert.IsType<BudgetThresholdReachedDetector>(Assert.Single(detectors,
            detector => detector.EventType == OrganizationEventType.BudgetThresholdReached));
        Assert.Equal(Enum.GetValues<OrganizationEventType>().Order(), detectors.Select(item => item.EventType).Order());
    }

    private static Task<DomainEventDetectionResult> Detect(IBudgetThresholdSource source, DomainEventDetectionContext context) =>
        new BudgetThresholdReachedDetector(source).EvaluateAsync(context).AsTask();

    private static BudgetCostFact Fact(DateTimeOffset at, decimal amount,
        BudgetCostCategory category = BudgetCostCategory.Reactive) =>
        new(new EventSourceCorrelation(MessageId.New(), ThreadId.New(), DirectiveId.New()), "call:1:0-1", at, amount, category);

    private static PositionDailyBudgetUsage Usage(PositionId? position = null, decimal? reactive = null,
        decimal? proactive = null, decimal? total = null, CivilDay? day = null, IEnumerable<BudgetCostFact>? facts = null) =>
        new(Org, position ?? Position, "UTC", day ?? Today, reactive, proactive, total, facts ?? []);

    private static EventSubscriptionsSnapshot Snapshot() =>
        EventSubscriptionsSnapshot.CreateBuilder(Org).AddPosition(Position, [Eighty]).Build();

    private static DomainEventDetectionContext Context(DateTimeOffset at, EventSubscriptionsSnapshot? snapshot = null,
        DomainEventDetectionCheckpoint? checkpoint = null) =>
        new(new(Org, OrganizationEventType.BudgetThresholdReached), snapshot ?? Snapshot(), checkpoint, at);

    private sealed class Source(ImmutableArray<PositionDailyBudgetUsage> items) : IBudgetThresholdSource
    {
        public ImmutableArray<PositionDailyBudgetUsage> Items { get; set; } = items;
        public Exception? Failure { get; set; }
        public Action? AfterRead { get; set; }
        public int Reads { get; private set; }
        public IReadOnlyCollection<PositionId>? Positions { get; private set; }

        public ValueTask<ImmutableArray<PositionDailyBudgetUsage>> ReadAsync(OrganizationId organizationId,
            IReadOnlyCollection<PositionId> positionIds, DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken = default)
        {
            Reads++;
            Positions = positionIds;
            if (Failure is not null) throw Failure;
            AfterRead?.Invoke();
            return new(Items);
        }
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class NoCommitStore : IDomainEventDetectionStore
    {
        public int Commits { get; private set; }
        public ValueTask<DomainEventDetectionCheckpoint?> ReadCheckpointAsync(DomainEventDetectorId detectorId,
            CancellationToken cancellationToken = default) => new((DomainEventDetectionCheckpoint?)null);
        public ValueTask<bool> TryCommitAsync(DomainEventDetectionCommit commit, CancellationToken cancellationToken = default)
        {
            Commits++;
            return new(true);
        }
    }
}
