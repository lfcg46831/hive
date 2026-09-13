using System.Globalization;
using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Organization.Registry;

namespace Hive.Tests;

public sealed class EventSubscriptionsTests
{
    private static readonly OrganizationId Org = OrganizationId.From("subscriptions-test");
    private static readonly PositionId Position = PositionId.From("ceo");
    private const OrganizationEventType Deadline = OrganizationEventType.DirectiveDeadlineApproaching;

    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("pt-PT")]
    public void Resolution_is_typed_complete_and_independent_of_declaration_order_and_culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var positions = new[] { "z", "a", "A" }.Select(PositionId.From).ToArray();
            EventSubscription[] subscriptions = [Window(10), Window(2),
                new(new PositionBlockedParameters(TimeSpan.FromHours(12)), true, Priority.High),
                new(new BudgetThresholdParameters(80))];
            var first = EventSubscriptionsSnapshot.CreateBuilder(Org);
            var second = EventSubscriptionsSnapshot.CreateBuilder(Org);
            foreach (var position in positions) first.AddPosition(position, subscriptions);
            foreach (var position in positions.Reverse()) second.AddPosition(position, subscriptions.Reverse());
            var snapshot = first.Build();
            foreach (var type in Enum.GetValues<OrganizationEventType>())
                Assert.Equal(snapshot.Resolve(type).ToArray(), second.Build().Resolve(type).ToArray());
            var deadlines = snapshot.Resolve(Deadline);
            Assert.Equal(new[] { "A", "A", "a", "a", "z", "z" }, deadlines.Select(item => item.PositionId.Value));
            Assert.Equal(new[] { 2d, 10d, 2d, 10d, 2d, 10d }, deadlines.Select(item =>
                Assert.IsType<DirectiveDeadlineParameters>(item.Subscription.Parameters).Within.TotalHours));
            Assert.All(snapshot.Resolve(OrganizationEventType.PositionBlockedProlonged), item =>
            {
                Assert.Equal(TimeSpan.FromHours(12), Assert.IsType<PositionBlockedParameters>(item.Subscription.Parameters).After);
                Assert.True(item.Subscription.IsCritical);
                Assert.Equal(Priority.High, item.Subscription.Priority);
            });
            Assert.All(snapshot.Resolve(OrganizationEventType.BudgetThresholdReached), item =>
            {
                Assert.Equal(80, Assert.IsType<BudgetThresholdParameters>(item.Subscription.Parameters).ThresholdPercent);
                Assert.False(item.Subscription.IsCritical);
                Assert.Equal(Priority.Normal, item.Subscription.Priority);
            });
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task Snapshots_are_isolated_and_cannot_be_mutated_by_inputs_builder_or_results()
    {
        var declarations = new List<EventSubscription> { Window(2) };
        var builder = EventSubscriptionsSnapshot.CreateBuilder(Org).AddPosition(Position, declarations);
        declarations.Clear();
        var first = builder.Build();
        var second = builder.AddPosition(PositionId.From("other"), [Window(10)]).Build();
        var otherOrg = OrganizationId.From("Subscriptions-test");
        var snapshots = new List<EventSubscriptionsSnapshot> { first, EventSubscriptionsSnapshot.CreateBuilder(otherOrg).Build() };
        IEventSubscriptions seam = new MaterializedEventSubscriptions(snapshots);
        snapshots.Clear();
        Assert.Single((await seam.GetSnapshotAsync(Org)).Resolve(Deadline));
        Assert.Equal(2, second.Resolve(Deadline).Length);
        Assert.Empty((await seam.GetSnapshotAsync(otherOrg)).Resolve(Deadline));
        Assert.Empty(first.Resolve(OrganizationEventType.BudgetThresholdReached));
        Assert.Throws<NotSupportedException>(() => ((IList<EventSubscriber>)first.Resolve(Deadline)).Clear());
        await Assert.ThrowsAsync<EventSubscriptionsNotFoundException>(() => seam.GetSnapshotAsync(OrganizationId.From("unknown")).AsTask());
    }

    [Fact]
    public void Construction_rejects_duplicate_identities_and_invalid_arguments()
    {
        var builder = EventSubscriptionsSnapshot.CreateBuilder(Org).AddPosition(Position, [Window(2)]);
        Assert.Throws<ArgumentException>(() => builder.AddPosition(Position, []));
        var duplicate = new EventSubscription(new DirectiveDeadlineParameters(TimeSpan.FromMinutes(120)), true, Priority.Critical);
        Assert.Throws<ArgumentException>(() => EventSubscriptionsSnapshot.CreateBuilder(Org).AddPosition(Position, [Window(2), duplicate]));
        Assert.Throws<ArgumentNullException>(() => EventSubscriptionsSnapshot.CreateBuilder(null!));
        Assert.Throws<ArgumentNullException>(() => builder.AddPosition(null!, []));
        Assert.Throws<ArgumentNullException>(() => builder.AddPosition(PositionId.From("other"), [null!]));
        Assert.Throws<ArgumentNullException>(() => builder.AddPosition(PositionId.From("other"), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Build().Resolve((OrganizationEventType)0));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Build().Resolve((OrganizationEventType)99));
        Assert.Throws<ArgumentException>(() => new MaterializedEventSubscriptions([builder.Build(), builder.Build()]));
    }

    [Fact]
    public async Task Registry_observes_updates_and_removal_and_preserves_older_snapshots()
    {
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationImporter(registry);
        var declaration = EventSubscriptionConfigurationTests.Deadline;
        await importer.ImportAsync(EventSubscriptionConfigurationTests.Configuration($"[{declaration}]"));
        IEventSubscriptions seam = new RegistryEventSubscriptions(registry);
        var old = await seam.GetSnapshotAsync(Org);
        await importer.ImportAsync(EventSubscriptionConfigurationTests.Configuration($"[{declaration.Replace("PT1H", "PT2H")}]"));
        var updated = Assert.Single((await seam.GetSnapshotAsync(Org)).Resolve(Deadline));
        Assert.Equal(TimeSpan.FromHours(2), Assert.IsType<DirectiveDeadlineParameters>(updated.Subscription.Parameters).Within);
        await importer.ImportAsync(EventSubscriptionConfigurationTests.Configuration("[]"));
        Assert.Empty((await seam.GetSnapshotAsync(Org)).Resolve(Deadline));
        Assert.Equal(TimeSpan.FromHours(1), Assert.IsType<DirectiveDeadlineParameters>(Assert.Single(old.Resolve(Deadline)).Subscription.Parameters).Within);
        await Assert.ThrowsAsync<EventSubscriptionsNotFoundException>(() => seam.GetSnapshotAsync(OrganizationId.From("unknown")).AsTask());
    }

    [Fact]
    public async Task Reader_is_called_once_with_scope_and_token_and_technical_failure_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new IOException("registry unavailable");
        var reader = new ProbeReader((org, token) =>
        {
            Assert.Equal(Org, org);
            Assert.Equal(cancellation.Token, token);
            return ValueTask.FromException<OrganizationRegistrySnapshot?>(failure);
        });
        var seam = new RegistryEventSubscriptions(reader);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => seam.GetSnapshotAsync(Org, cancellation.Token).AsTask()));
        Assert.Equal(1, reader.Calls);
        cancellation.Cancel();
        foreach (IEventSubscriptions cancelled in new IEventSubscriptions[] { seam,
            new MaterializedEventSubscriptions(EventSubscriptionsSnapshot.CreateBuilder(Org).Build()), new UnavailableEventSubscriptions() })
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.GetSnapshotAsync(Org, cancellation.Token).AsTask());
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task Reader_success_is_one_snapshot_and_wrong_scope_or_late_cancellation_is_rejected()
    {
        var import = await new OrganizationConfigurationImporter(new InMemoryOrganizationRegistry())
            .ImportAsync(EventSubscriptionConfigurationTests.Configuration("[]"));
        var reader = new ProbeReader((_, _) => new(import.Snapshot));
        var seam = new RegistryEventSubscriptions(reader);
        Assert.Same(import.Snapshot!.EventSubscriptions, await seam.GetSnapshotAsync(Org));
        Assert.Equal(1, reader.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => seam.GetSnapshotAsync(OrganizationId.From("other")).AsTask());
        using var cancellation = new CancellationTokenSource();
        var late = new RegistryEventSubscriptions(new ProbeReader((_, _) =>
        {
            cancellation.Cancel();
            return new(import.Snapshot);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late.GetSnapshotAsync(Org, cancellation.Token).AsTask());
    }

    private static EventSubscription Window(int hours) => new(new DirectiveDeadlineParameters(TimeSpan.FromHours(hours)));

    private sealed class ProbeReader(Func<OrganizationId, CancellationToken, ValueTask<OrganizationRegistrySnapshot?>> read) : IOrganizationRegistryReader
    {
        public int Calls { get; private set; }
        public ValueTask<OrganizationRegistrySnapshot?> FindSnapshotAsync(OrganizationId organizationId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return read(organizationId, cancellationToken);
        }
    }
}
