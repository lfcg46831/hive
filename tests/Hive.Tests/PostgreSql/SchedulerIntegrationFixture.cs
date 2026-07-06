using System.Collections.Specialized;
using Akka.Actor;
using Akka.Quartz.Actor.Commands;
using Hive.Actors.Scheduling;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Domain.Scheduling;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Scheduling;
using Hive.Infrastructure.Scheduling.PostgreSql;
using Npgsql;
using Quartz;
using Quartz.Impl;

namespace Hive.Tests.PostgreSql;

internal sealed class SchedulerIntegrationFixture : IAsyncDisposable
{
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlSchedulerPulseDeliveryStore _deliveryStore;
    private readonly PositionShardingMultiNodeFixture _positions;
    private readonly Quartz.IScheduler _quartzScheduler;
    private readonly CapturingSchedulerPulseDispatcher _pulseDispatcher;
    private readonly IActorRef _coordinator;

    private SchedulerIntegrationFixture(
        NpgsqlDataSource dataSource,
        PostgreSqlSchedulerPulseDeliveryStore deliveryStore,
        PositionShardingMultiNodeFixture positions,
        Quartz.IScheduler quartzScheduler,
        CapturingSchedulerPulseDispatcher pulseDispatcher,
        IActorRef coordinator,
        SchedulerIntegrationClock clock)
    {
        _dataSource = dataSource;
        _deliveryStore = deliveryStore;
        _positions = positions;
        _quartzScheduler = quartzScheduler;
        _pulseDispatcher = pulseDispatcher;
        _coordinator = coordinator;
        Clock = clock;
    }

    public SchedulerIntegrationClock Clock { get; }

    public static async Task<SchedulerIntegrationFixture> StartAsync(
        string connectionString,
        bool resetSchemas = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var dataSource = NpgsqlDataSource.Create(connectionString);
        PostgreSqlSchedulerPulseDeliveryStore? deliveryStore = null;
        PositionShardingMultiNodeFixture? positions = null;
        Quartz.IScheduler? quartzScheduler = null;

        try
        {
            if (resetSchemas)
            {
                await ResetSchemasAsync(dataSource);
            }

            await new PostgreSqlSchedulerPulseDeliveryMigrator(dataSource).MigrateAsync();

            positions = await PositionShardingMultiNodeFixture.StartAsync(
                persistenceConnectionString: connectionString);
            quartzScheduler = await NewQuartzSchedulerAsync();
            deliveryStore = new PostgreSqlSchedulerPulseDeliveryStore(dataSource);

            var clock = new SchedulerIntegrationClock(
                new DateTimeOffset(2026, 7, 3, 16, 50, 0, TimeSpan.Zero));
            var pulseDispatcher = new CapturingSchedulerPulseDispatcher(
                AkkaClusterShardingSchedulerPulseDispatcher.Instance);
            var coordinator = positions.AgentNodes[0].System.ActorOf(
                SchedulerCoordinator.Props(
                    new InjectedQuartzSchedulerAdapter(quartzScheduler),
                    clock,
                    deliveryStore,
                    pulseDispatcher),
                $"scheduler-integration-{Guid.NewGuid():N}");

            return new SchedulerIntegrationFixture(
                dataSource,
                deliveryStore,
                positions,
                quartzScheduler,
                pulseDispatcher,
                coordinator,
                clock);
        }
        catch
        {
            if (quartzScheduler is not null)
            {
                await quartzScheduler.Shutdown(waitForJobsToComplete: false);
            }

            if (positions is not null)
            {
                await positions.DisposeAsync();
            }

            if (deliveryStore is not null)
            {
                await deliveryStore.DisposeAsync();
            }

            await dataSource.DisposeAsync();
            throw;
        }
    }

    public static async Task<OrganizationRegistrySnapshot> ImportedSnapshotAsync(
        ScheduleEntryConfiguration schedule,
        DateTimeOffset importAtUtc)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        RequireUtc(importAtUtc, nameof(importAtUtc));

        var registry = new InMemoryOrganizationRegistry();
        var imported = await new OrganizationConfigurationImporter(
                registry,
                new SchedulerIntegrationClock(importAtUtc))
            .ImportAsync(WithDeliveryLeadSchedules(ExampleConfiguration(), schedule));

        Assert.Equal(OrganizationImportStatus.Applied, imported.Status);
        return imported.Snapshot!;
    }

    public async Task<SchedulerReconciliationResult> ReconcileAsync(OrganizationRegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            return await _coordinator.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                AskTimeout);
        }
        catch (AskTimeoutException exception)
        {
            var rows = await DescribeSchedulerDeliveriesAsync();
            var deliveryErrors = _pulseDispatcher.Failures.Count == 0
                ? "<none>"
                : string.Join(", ", _pulseDispatcher.Failures.Select(failure => failure.ToString()));
            throw new TimeoutException(
                $"Scheduler reconciliation did not complete. Rows: {rows}. "
                + $"Dispatcher failures: {deliveryErrors}",
                exception);
        }
    }

    public async Task WaitForQuartzJobAsync(SchedulerScheduleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var identity = SchedulerQuartzIdentity.From(key);
        var jobKey = new JobKey(identity.JobName, identity.JobGroup);
        var triggerKey = new TriggerKey(identity.TriggerName, identity.TriggerGroup);

        await WaitForAsync(async () =>
            await _quartzScheduler.CheckExists(jobKey)
            && await _quartzScheduler.CheckExists(triggerKey));
    }

    public async Task TriggerQuartzAsync(SchedulerScheduleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        await WaitForQuartzJobAsync(key);
        var identity = SchedulerQuartzIdentity.From(key);
        await _quartzScheduler.TriggerJob(new JobKey(identity.JobName, identity.JobGroup));
    }

    public Task<SchedulerPulseDeliveryState> WaitForDeliveryAsync(
        PulseIdempotencyKey idempotencyKey,
        SchedulerPulseDeliveryStatus status)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);

        return WaitForDeliveryCoreAsync(idempotencyKey, status);
    }

    public Task<SchedulerPulseDeliveryState?> FindDeliveryAsync(PulseIdempotencyKey idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);

        return _deliveryStore.FindAsync(idempotencyKey);
    }

    public Task<IReadOnlyList<SchedulerPulseDeliveryHistoryEntry>> ReadDeliveryHistoryAsync(
        PulseIdempotencyKey idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);

        return _deliveryStore.ReadHistoryAsync(idempotencyKey);
    }

    private async Task<SchedulerPulseDeliveryState> WaitForDeliveryCoreAsync(
        PulseIdempotencyKey idempotencyKey,
        SchedulerPulseDeliveryStatus status)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await _deliveryStore.FindAsync(idempotencyKey);
            if (state?.Status == status)
            {
                return state;
            }

            await Task.Delay(100);
        }

        var coordinatorState = await _coordinator.Ask<SchedulerCoordinatorState>(
            GetSchedulerCoordinatorState.Instance,
            AskTimeout);
        var rows = await DescribeSchedulerDeliveriesAsync();
        var history = await DescribeSchedulerDeliveryHistoryAsync(idempotencyKey);
        var deliveryErrors = _pulseDispatcher.Failures.Count == 0
            ? "<none>"
            : string.Join(", ", _pulseDispatcher.Failures.Select(failure => failure.ToString()));
        throw new TimeoutException(
            $"Delivery '{idempotencyKey.Value}' did not reach status '{status}'. "
            + $"Pending dispatches: {coordinatorState.PendingDispatches.Length}. "
            + $"Rows: {rows}. "
            + $"History: {history}. "
            + $"Dispatcher failures: {deliveryErrors}");
    }

    public Task<MessageReceived> WaitForMessageReceivedAsync(
        PositionEntityId entity,
        MessageId messageId)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(messageId);

        return WaitForAsync(() =>
        {
            var received = _positions
                .CommittedEvents<MessageReceived>(entity)
                .Select(committed => committed.Event)
                .FirstOrDefault(candidate => candidate.Message.Id == messageId);

            return Task.FromResult(received);
        });
    }

    public Task WaitForDuplicateRejectedAsync(PositionEntityId entity, MessageId messageId)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(messageId);

        return _positions.WaitForDuplicateRejectedAsync(entity, messageId);
    }

    public IReadOnlyList<MessageReceived> CommittedMessages(PositionEntityId entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return _positions
            .CommittedEvents<MessageReceived>(entity)
            .Select(committed => committed.Event)
            .ToArray();
    }

    public IReadOnlyList<MessageReceived> CommittedMessages(
        PositionEntityId entity,
        MessageId messageId)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(messageId);

        return CommittedMessages(entity)
            .Where(received => received.Message.Id == messageId)
            .ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _quartzScheduler.Shutdown(waitForJobsToComplete: false);
        await _positions.DisposeAsync();
        await _deliveryStore.DisposeAsync();
        await _dataSource.DisposeAsync();
    }

    private static async Task ResetSchemasAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            DROP SCHEMA IF EXISTS scheduler CASCADE;
            DROP SCHEMA IF EXISTS persistence CASCADE;
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Quartz.IScheduler> NewQuartzSchedulerAsync()
    {
        var properties = new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"hive-scheduler-integration-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        };
        var scheduler = await new StdSchedulerFactory(properties).GetScheduler();
        await scheduler.Start();
        return scheduler;
    }

    private static OrganizationConfiguration WithDeliveryLeadSchedules(
        OrganizationConfiguration configuration,
        params ScheduleEntryConfiguration[] schedules) =>
        new(
            configuration.Organization,
            configuration.Units,
            configuration.Positions
                .Select(position => position.Id.Value == "delivery-lead"
                    ? new PositionConfiguration(
                        position.Id,
                        position.Unit,
                        new OccupantConfiguration(
                            position.Occupant.Type,
                            position.Occupant.IdentityPromptRef,
                            position.Occupant.Ai,
                            new WorkingHoursConfiguration("09:00", "18:00"),
                            position.Occupant.Authority,
                            schedules,
                            position.Occupant.Subscriptions,
                            position.Occupant.Tools),
                        position.ReportsTo,
                        position.Name,
                        "Europe/Lisbon")
                    : position)
                .ToArray(),
            configuration.Prompts);

    private static OrganizationConfiguration ExampleConfiguration()
    {
        var result = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(RepositoryRoot, "config", "organizations", "acme-delivery", "organization.yaml"));

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        return result.Configuration!;
    }

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate the Hive repository root.");
        }
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Condition was not met within the allotted time.");
    }

    private async Task<string> DescribeSchedulerDeliveriesAsync()
    {
        var rows = new List<string>();
        await using var command = _dataSource.CreateCommand(
            """
            SELECT idempotency_key, status, attempt_count, reason_code
            FROM scheduler.pulse_deliveries
            ORDER BY idempotency_key;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(
                $"{reader.GetString(0)}:{reader.GetString(1)}:{reader.GetInt32(2)}:{(reader.IsDBNull(3) ? "" : reader.GetString(3))}");
        }

        return rows.Count == 0 ? "<none>" : string.Join(", ", rows);
    }

    private async Task<string> DescribeSchedulerDeliveryHistoryAsync(PulseIdempotencyKey idempotencyKey)
    {
        var rows = new List<string>();
        await using var command = _dataSource.CreateCommand(
            """
            SELECT sequence, status, reason_code
            FROM scheduler.pulse_delivery_history
            WHERE idempotency_key = $1
            ORDER BY sequence;
            """);
        command.Parameters.AddWithValue(idempotencyKey.Value);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(
                $"{reader.GetInt32(0)}:{reader.GetString(1)}:{(reader.IsDBNull(2) ? "" : reader.GetString(2))}");
        }

        return rows.Count == 0 ? "<none>" : string.Join(", ", rows);
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> read)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = await read();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Condition was not met within the allotted time.");
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Scheduler integration fixture timestamps must be expressed as UTC offsets.",
                parameterName);
        }
    }

    private sealed class InjectedQuartzSchedulerAdapter(Quartz.IScheduler scheduler) : ISchedulerQuartzAdapter
    {
        public void Schedule(IActorContext context, IActorRef receiver, SchedulerQuartzJob job)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(receiver);
            ArgumentNullException.ThrowIfNull(job);

            GetOrCreateQuartzActor(context).Tell(new CreateJob(
                receiver,
                new SchedulerQuartzScheduleFired(job.Key),
                AkkaQuartzSchedulerAdapter.BuildTrigger(job)));
        }

        public void Unschedule(IActorContext context, SchedulerScheduleKey key)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(key);

            var identity = SchedulerQuartzIdentity.From(key);
            GetOrCreateQuartzActor(context).Tell(new RemoveJob(
                new JobKey(identity.JobName, identity.JobGroup),
                new TriggerKey(identity.TriggerName, identity.TriggerGroup)));
        }

        private IActorRef GetOrCreateQuartzActor(IActorContext context)
        {
            var child = context.Child(SchedulerCoordinatorIdentity.QuartzActorName);
            return child.Equals(ActorRefs.Nobody)
                ? context.ActorOf(
                    Props.Create(() => new HiveQuartzActor(scheduler)),
                    SchedulerCoordinatorIdentity.QuartzActorName)
                : child;
        }
    }

    private sealed class CapturingSchedulerPulseDispatcher(ISchedulerPulseDispatcher inner)
        : ISchedulerPulseDispatcher
    {
        private readonly object _gate = new();
        private readonly List<Exception> _failures = [];

        public IReadOnlyList<Exception> Failures
        {
            get
            {
                lock (_gate)
                {
                    return _failures.ToArray();
                }
            }
        }

        public async Task DeliverAsync(
            IActorContext context,
            Pulse pulse,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await inner.DeliverAsync(context, pulse, cancellationToken);
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    _failures.Add(exception);
                }

                throw;
            }
        }
    }

}

internal sealed class SchedulerIntegrationClock(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = RequireUtc(utcNow, nameof(utcNow));

    public void SetUtcNow(DateTimeOffset utcNow) =>
        _utcNow = RequireUtc(utcNow, nameof(utcNow));

    public override DateTimeOffset GetUtcNow() => _utcNow;

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Scheduler integration fixture timestamps must be expressed as UTC offsets.",
                parameterName);
        }

        return value;
    }
}
