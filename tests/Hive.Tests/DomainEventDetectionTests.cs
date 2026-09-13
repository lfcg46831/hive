using Hive.Application.Events;
using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class DomainEventDetectionTests
{
    private static readonly OrganizationId Org = OrganizationId.From("detection");
    private static readonly PositionId Position = PositionId.From("lead");
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly EventSubscription Subscription = new(new DirectiveDeadlineParameters(TimeSpan.FromHours(1)));
    private static readonly EventSubscriber Subscriber = new(Position, Subscription);
    private static readonly EventSourceCorrelation Correlation = new(MessageId.New(), ThreadId.New(), DirectiveId.New());
    private static readonly DomainEventDetectorId Id = new(Org, Subscription.EventType);

    [Fact]
    public async Task First_cycle_and_exact_interval_revisit_candidates_without_new_facts_and_preserve_first_occurrence()
    {
        var store = new TestStore();
        var clock = new TestClock(Now);
        var detector = new TestDetector(context => new("v1:42", [Occurrence(context.EvaluatedAtUtc)]));
        var cycle = new DomainEventDetectionCycle(store, clock, Interval);
        Assert.Equal(DomainEventDetectionCycleOutcome.Committed, await cycle.EvaluateAsync(detector, Snapshot()));
        var first = Assert.Single(store.Pending.Values);
        Assert.Equal(1, store.Checkpoints[Id].Revision);
        Assert.Equal(Now, store.Checkpoints[Id].EvaluatedAtUtc);

        clock.Now = Now.AddTicks(-1);
        Assert.Equal(DomainEventDetectionCycleOutcome.NotDue, await cycle.EvaluateAsync(detector, Snapshot()));
        clock.Now = Now + Interval - TimeSpan.FromTicks(1);
        Assert.Equal(DomainEventDetectionCycleOutcome.NotDue, await cycle.EvaluateAsync(detector, Snapshot()));
        Assert.Equal(1, detector.Calls);

        clock.Now = Now + Interval;
        // A replacement cycle has no local progress or candidate state to recover.
        cycle = new(store, clock, Interval);
        Assert.Equal(DomainEventDetectionCycleOutcome.Committed, await cycle.EvaluateAsync(detector, Snapshot()));
        Assert.Equal(2, detector.Calls);
        Assert.Equal("v1:42", detector.LastContext!.Checkpoint!.Cursor);
        Assert.Equal(2, store.Checkpoints[Id].Revision);
        Assert.Same(first, Assert.Single(store.Pending.Values));
        Assert.Equal(4, clock.Reads);
    }

    [Fact]
    public async Task Empty_batches_and_empty_subscriptions_advance_scoped_checkpoints()
    {
        var store = new TestStore();
        var cycle = new DomainEventDetectionCycle(store, new TestClock(Now), Interval);
        var deadline = new TestDetector(_ => new(null, []));
        var blocked = new TestDetector(_ => new("v1:0", []), OrganizationEventType.PositionBlockedProlonged);
        var other = OrganizationId.From("Detection");
        Assert.Equal(DomainEventDetectionCycleOutcome.Committed, await cycle.EvaluateAsync(deadline, Snapshot()));
        Assert.Equal(DomainEventDetectionCycleOutcome.Committed, await cycle.EvaluateAsync(blocked, Snapshot()));
        Assert.Empty(blocked.LastContext!.Subscribers);
        Assert.Equal(DomainEventDetectionCycleOutcome.Committed,
            await cycle.EvaluateAsync(deadline, EventSubscriptionsSnapshot.CreateBuilder(other).Build()));
        Assert.Equal(3, store.Checkpoints.Count);
        Assert.Empty(store.Pending);
        Assert.All(store.Checkpoints.Values, checkpoint => Assert.Equal(1, checkpoint.Revision));
    }

    [Fact]
    public async Task New_subscription_is_visible_on_next_cycle_with_unchanged_source_cursor()
    {
        var store = new TestStore();
        var clock = new TestClock(Now);
        var cycle = new DomainEventDetectionCycle(store, clock, Interval);
        var detector = new TestDetector(context => new("v1:42",
            context.Subscribers.Select(item => Occurrence(context.EvaluatedAtUtc, subscriber: item))));
        await cycle.EvaluateAsync(detector, Snapshot());
        clock.Now += Interval;
        var snapshot = EventSubscriptionsSnapshot.CreateBuilder(Org).AddPosition(Position, [Subscription])
            .AddPosition(PositionId.From("other"), [Subscription]).Build();
        await cycle.EvaluateAsync(detector, snapshot);
        Assert.Equal(2, store.Pending.Count);
        Assert.Equal("v1:42", store.Checkpoints[Id].Cursor);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("detector")]
    [InlineData("commit")]
    public async Task Technical_failure_does_not_become_absence_or_advance_progress(string stage)
    {
        var failure = new IOException("unavailable");
        var store = new TestStore { FailureStage = stage, Failure = failure };
        var cycle = new DomainEventDetectionCycle(store, new TestClock(Now), Interval);
        var detector = new TestDetector(_ => stage == "detector" ? throw failure : new("v1:42", [Occurrence(Now)]));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => cycle.EvaluateAsync(detector, Snapshot()).AsTask()));
        Assert.Empty(store.Checkpoints);
        Assert.Empty(store.Pending);
        store.FailureStage = null;
        Assert.Equal(DomainEventDetectionCycleOutcome.Committed, await cycle.EvaluateAsync(
            new TestDetector(_ => new("v1:42", [Occurrence(Now)])), Snapshot()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_or_after_detection_cannot_commit(bool cancelDuringDetection)
    {
        using var cancellation = new CancellationTokenSource();
        var store = new TestStore();
        var detector = new TestDetector(_ =>
        {
            cancellation.Cancel();
            return new("v1:42", [Occurrence(Now)]);
        });
        if (!cancelDuringDetection) cancellation.Cancel();
        var cycle = new DomainEventDetectionCycle(store, new TestClock(Now), Interval);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cycle.EvaluateAsync(detector, Snapshot(), cancellation.Token).AsTask());
        Assert.Equal(cancelDuringDetection ? 1 : 0, detector.Calls);
        Assert.Empty(store.Checkpoints);
        Assert.Empty(store.Pending);
    }

    [Fact]
    public async Task Competing_cycles_cannot_overwrite_newer_checkpoint_or_pending_work()
    {
        var store = new TestStore();
        var clock = new TestClock(Now);
        var firstCycle = new DomainEventDetectionCycle(store, clock, Interval);
        var secondCycle = new DomainEventDetectionCycle(store, clock, Interval);
        var detector = new PausedDetector();
        var first = firstCycle.EvaluateAsync(detector, Snapshot()).AsTask();
        await detector.Started.Task;
        Assert.Equal(DomainEventDetectionCycleOutcome.Committed, await secondCycle.EvaluateAsync(
            new TestDetector(_ => new("v1:winner", [Occurrence(Now)])), Snapshot()));
        detector.Result.SetResult(new("v1:loser", []));
        Assert.Equal(DomainEventDetectionCycleOutcome.Conflict, await first);
        Assert.Equal("v1:winner", store.Checkpoints[Id].Cursor);
        Assert.Single(store.Pending);
    }

    [Fact]
    public async Task Lost_commit_response_recovers_from_store_without_replacing_first_materialization()
    {
        var store = new TestStore { FailureStage = "after-commit", Failure = new IOException("lost response") };
        var clock = new TestClock(Now);
        var detector = new TestDetector(context => new("v1:42", [Occurrence(context.EvaluatedAtUtc)]));
        await Assert.ThrowsAsync<IOException>(() => new DomainEventDetectionCycle(store, clock, Interval)
            .EvaluateAsync(detector, Snapshot()).AsTask());
        var first = Assert.Single(store.Pending.Values);
        store.FailureStage = null;
        var recovered = new DomainEventDetectionCycle(store, clock, Interval);
        Assert.Equal(DomainEventDetectionCycleOutcome.NotDue, await recovered.EvaluateAsync(detector, Snapshot()));
        clock.Now += Interval;
        await recovered.EvaluateAsync(detector, Snapshot());
        Assert.Same(first, Assert.Single(store.Pending.Values));
    }

    [Fact]
    public async Task Foreign_checkpoint_is_rejected_even_when_it_would_suppress_evaluation()
    {
        var store = new TestStore
        {
            ReadOverride = new(new(OrganizationId.From("foreign"), Id.EventType), 1, null, Now.AddDays(1))
        };
        var cycle = new DomainEventDetectionCycle(store, new TestClock(Now), Interval);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cycle.EvaluateAsync(
            new TestDetector(_ => new(null, [])), Snapshot()).AsTask());
        Assert.Empty(store.Checkpoints);
    }

    [Fact]
    public void Commit_rejects_foreign_scope_type_subscription_and_observation_and_copies_results()
    {
        var context = new DomainEventDetectionContext(Id, Snapshot(), null, Now);
        Assert.Throws<ArgumentException>(() => Commit(Occurrence(Now, OrganizationId.From("foreign"))));
        Assert.Throws<ArgumentException>(() => Commit(Occurrence(Now.AddTicks(1))));
        Assert.Throws<ArgumentException>(() => Commit(Occurrence(Now, subscriber: new(Position,
            new(Subscription.Parameters, true, Priority.High)))));
        var blockedSubscription = new EventSubscriber(Position, new(new PositionBlockedParameters(Interval)));
        Assert.Throws<ArgumentException>(() => Commit(new(Org, blockedSubscription,
            new PositionBlockedProlongedPayload(Position, Now - Interval, (PositionBlockedParameters)blockedSubscription.Subscription.Parameters,
                PositionBlockedCause.ConfigurationBlocked, Now))));

        var occurrences = new List<DomainEventOccurrence> { Occurrence(Now) };
        var result = new DomainEventDetectionResult("v1:42", occurrences);
        occurrences.Clear();
        Assert.Single(new DomainEventDetectionCommit(context, result).Occurrences);
        Assert.Throws<ArgumentException>(() => new DomainEventDetectionResult(null, [Occurrence(Now), Occurrence(Now)]));
        void Commit(DomainEventOccurrence occurrence) => _ = new DomainEventDetectionCommit(context, new(null, [occurrence]));
    }

    [Fact]
    public void Occurrences_enforce_typed_parameters_and_exclusive_deadline_windows()
    {
        var occurrence = Occurrence(Now);
        Assert.Equal(Now.AddMinutes(-30), occurrence.WindowStartsAtUtc);
        Assert.Equal(Now.AddMinutes(30), occurrence.WindowEndsAtUtc);
        Assert.Equal(occurrence.Key, Occurrence(Now.AddMinutes(1)).Key);
        Assert.Equal(occurrence.Key, Occurrence(Now.ToOffset(TimeSpan.FromHours(3))).Key);
        Assert.Throws<ArgumentException>(() => new DomainEventOccurrence(Org, Subscriber, occurrence.Payload));
        Assert.Throws<ArgumentException>(() => new DomainEventOccurrence(Org, Subscriber, occurrence.Payload, Now));
        Assert.Throws<ArgumentException>(() => new DomainEventOccurrence(Org,
            new(Position, new(new DirectiveDeadlineParameters(Interval))), occurrence.Payload, occurrence.WindowEndsAtUtc));
        Assert.Throws<ArgumentException>(() => Occurrence(Now.AddMinutes(30)));
    }

    [Theory]
    [InlineData("2026-03-29T00:00:00Z", "2026-03-29T23:00:00Z")]
    [InlineData("2026-10-25T00:00:00Z", "2026-10-26T00:00:00Z")]
    public void Budget_window_respects_civil_day_boundary_across_dst(string occurred, string end)
    {
        var instant = DateTimeOffset.Parse(occurred);
        var boundary = DateTimeOffset.Parse(end);
        var parameters = new BudgetThresholdParameters(80);
        var subscriber = new EventSubscriber(Position, new(parameters));
        var payload = new BudgetThresholdReachedPayload(Position, Correlation, DailyBudgetKind.Total,
            DateOnly.FromDateTime(instant.UtcDateTime), "Europe/Lisbon", parameters, 10m, 8m, instant, instant);
        var occurrence = new DomainEventOccurrence(Org, subscriber, payload, boundary);
        Assert.Equal(boundary, occurrence.WindowEndsAtUtc);
        Assert.Throws<ArgumentException>(() => new DomainEventOccurrence(Org, subscriber, payload, boundary.AddTicks(-1)));
        Assert.Throws<ArgumentException>(() => new DomainEventOccurrence(Org, subscriber, payload, boundary.AddTicks(1)));
        Assert.Throws<ArgumentException>(() => new DomainEventOccurrence(Org, new(PositionId.From("other"), subscriber.Subscription), payload, boundary));
    }

    [Fact]
    public async Task Evaluation_uses_one_clock_observation_even_if_time_changes_while_reading_facts()
    {
        var store = new TestStore();
        var clock = new TestClock(Now);
        var detector = new TestDetector(context =>
        {
            clock.Now += TimeSpan.FromHours(1);
            return new("v1:42", [Occurrence(context.EvaluatedAtUtc)]);
        });
        await new DomainEventDetectionCycle(store, clock, Interval).EvaluateAsync(detector, Snapshot());
        Assert.Equal(1, clock.Reads);
        Assert.Equal(Now, store.Checkpoints[Id].EvaluatedAtUtc);
        Assert.Equal(Now, Assert.Single(store.Pending.Values).Payload.ObservedAtUtc);
    }

    [Fact]
    public void Commit_order_is_independent_of_detector_enumeration_order()
    {
        var second = new EventSubscriber(PositionId.From("second"), Subscription);
        var snapshot = EventSubscriptionsSnapshot.CreateBuilder(Org).AddPosition(Position, [Subscription])
            .AddPosition(second.PositionId, [Subscription]).Build();
        var context = new DomainEventDetectionContext(Id, snapshot, null, Now);
        var firstOccurrence = Occurrence(Now);
        var secondOccurrence = Occurrence(Now, subscriber: second);
        var first = new DomainEventDetectionCommit(context, new(null, [firstOccurrence, secondOccurrence]));
        var reversed = new DomainEventDetectionCommit(context, new(null, [secondOccurrence, firstOccurrence]));
        Assert.Equal(first.Occurrences.ToArray(), reversed.Occurrences.ToArray());
    }

    [Fact]
    public void Invalid_progress_and_context_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DomainEventDetectionCycle(new TestStore(), TimeProvider.System, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DomainEventDetectorId(Org, (OrganizationEventType)0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DomainEventDetectionCheckpoint(Id, 0, null, Now));
        Assert.Throws<ArgumentException>(() => new DomainEventDetectionCheckpoint(Id, 1, " ", Now));
        Assert.Throws<ArgumentException>(() => new DomainEventDetectionContext(Id, Snapshot(), new(Id, 1, null, Now), Now.AddTicks(-1)));
        Assert.Throws<ArgumentException>(() => new DomainEventDetectionContext(Id,
            EventSubscriptionsSnapshot.CreateBuilder(OrganizationId.From("foreign")).Build(), null, Now));
        var checkpoint = new DomainEventDetectionCheckpoint(Id, 1, null, Now.ToOffset(TimeSpan.FromHours(3)));
        Assert.Equal(TimeSpan.Zero, checkpoint.EvaluatedAtUtc.Offset);
    }

    private static EventSubscriptionsSnapshot Snapshot() => EventSubscriptionsSnapshot.CreateBuilder(Org)
        .AddPosition(Position, [Subscription]).Build();

    private static DomainEventOccurrence Occurrence(DateTimeOffset observedAt, OrganizationId? org = null,
        EventSubscriber? subscriber = null) => new(org ?? Org, subscriber ?? Subscriber,
        new DirectiveDeadlineApproachingPayload(Correlation, Now.AddMinutes(30),
            (DirectiveDeadlineParameters)Subscription.Parameters, observedAt), Now.AddMinutes(30));

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() { Reads++; return Now; }
    }

    private sealed class TestDetector(Func<DomainEventDetectionContext, DomainEventDetectionResult> evaluate,
        OrganizationEventType eventType = OrganizationEventType.DirectiveDeadlineApproaching) : IDomainEventDetector
    {
        public OrganizationEventType EventType => eventType;
        public int Calls { get; private set; }
        public DomainEventDetectionContext? LastContext { get; private set; }
        public ValueTask<DomainEventDetectionResult> EvaluateAsync(DomainEventDetectionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastContext = context;
            return new(evaluate(context));
        }
    }

    private sealed class PausedDetector : IDomainEventDetector
    {
        public OrganizationEventType EventType => OrganizationEventType.DirectiveDeadlineApproaching;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DomainEventDetectionResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<DomainEventDetectionResult> EvaluateAsync(DomainEventDetectionContext context, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            return new(Result.Task);
        }
    }

    // Contract double shared between cycle instances; this is not evidence of PostgreSQL durability.
    private sealed class TestStore : IDomainEventDetectionStore
    {
        public Dictionary<DomainEventDetectorId, DomainEventDetectionCheckpoint> Checkpoints { get; } = new();
        public Dictionary<DomainEventIdempotencyKey, DomainEventOccurrence> Pending { get; } = new();
        public string? FailureStage { get; set; }
        public Exception Failure { get; set; } = new IOException();
        public DomainEventDetectionCheckpoint? ReadOverride { get; init; }
        public ValueTask<DomainEventDetectionCheckpoint?> ReadCheckpointAsync(DomainEventDetectorId detectorId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailureStage == "read") throw Failure;
            return new(ReadOverride ?? Checkpoints.GetValueOrDefault(detectorId));
        }
        public ValueTask<bool> TryCommitAsync(DomainEventDetectionCommit commit, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailureStage == "commit") throw Failure;
            if ((Checkpoints.GetValueOrDefault(commit.Checkpoint.DetectorId)?.Revision ?? 0) != commit.ExpectedRevision)
                return new(false);
            foreach (var occurrence in commit.Occurrences) Pending.TryAdd(occurrence.Key, occurrence);
            Checkpoints[commit.Checkpoint.DetectorId] = commit.Checkpoint;
            if (FailureStage == "after-commit") throw Failure;
            return new(true);
        }
    }
}
