using Akka.Actor;
using Hive.Actors.Events;
using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;

namespace Hive.Tests;

public sealed class DomainEventsCoordinatorTests
{
    internal const string Declarations = "[{event: directive-deadline-approaching, within: PT1H}, "
        + "{event: position-blocked-prolonged, after: PT2H, critical: true, priority: high}, "
        + "{event: budget-threshold-reached, threshold_percent: 80}]";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Reconciliation_is_idempotent_preserves_typed_subscriptions_and_replaces_removed_declarations()
    {
        using var system = ActorSystem.Create("domain-events-reconcile");
        try
        {
            var actor = system.ActorOf(DomainEventsCoordinator.Props());
            var registry = new InMemoryOrganizationRegistry();
            var first = await ImportAsync(registry);
            var accepted = await ReconcileAsync(actor, first);
            Assert.True(accepted.IsAccepted);
            Assert.True(accepted.IsChanged);
            Assert.Same(first.EventSubscriptions, accepted.Materialization.Subscriptions);
            Assert.All(Enum.GetValues<OrganizationEventType>(), type =>
                Assert.Single(accepted.Materialization.Subscriptions.Resolve(type)));
            var blocked = Assert.Single(accepted.Materialization.Subscriptions.Resolve(OrganizationEventType.PositionBlockedProlonged));
            Assert.Equal(TimeSpan.FromHours(2), Assert.IsType<PositionBlockedParameters>(blocked.Subscription.Parameters).After);
            Assert.True(blocked.Subscription.IsCritical);
            Assert.Equal(Priority.High, blocked.Subscription.Priority);
            var duplicate = await ReconcileAsync(actor, await ImportAsync(registry));
            Assert.True(duplicate.IsAccepted);
            Assert.False(duplicate.IsChanged);
            Assert.Same(accepted.Materialization, duplicate.Materialization);

            var updated = await ImportAsync(registry, Declarations.Replace("PT1H", "PT3H"));
            var changed = await ReconcileAsync(actor, updated);
            Assert.True(changed.IsChanged);
            Assert.Equal(TimeSpan.FromHours(3), Assert.IsType<DirectiveDeadlineParameters>(Assert.Single(
                changed.Materialization.Subscriptions.Resolve(OrganizationEventType.DirectiveDeadlineApproaching)).Subscription.Parameters).Within);
            var removed = await ReconcileAsync(actor, await ImportAsync(registry, "[]"));
            Assert.True(removed.IsChanged);
            Assert.All(Enum.GetValues<OrganizationEventType>(), type => Assert.Empty(removed.Materialization.Subscriptions.Resolve(type)));
            Assert.Single(accepted.Materialization.Subscriptions.Resolve(OrganizationEventType.DirectiveDeadlineApproaching));
            Assert.Single((await actor.Ask<DomainEventsCoordinatorState>(GetDomainEventsCoordinatorState.Instance, Timeout)).Organizations);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task Organizations_are_isolated_ordered_and_stale_or_conflicting_snapshots_do_not_mutate_state()
    {
        using var system = ActorSystem.Create("domain-events-isolation");
        try
        {
            var actor = system.ActorOf(DomainEventsCoordinator.Props());
            var registry = new InMemoryOrganizationRegistry();
            var first = await ImportAsync(registry);
            var conflict = await ImportAsync(new InMemoryOrganizationRegistry(), "[]");
            await ReconcileAsync(actor, first);
            var rejected = await ReconcileAsync(actor, conflict);
            Assert.False(rejected.IsAccepted);
            Assert.False(rejected.IsChanged);
            Assert.Equal("domain-events-conflicting-registry-snapshot", rejected.ErrorCode);
            var updated = await ImportAsync(registry, "[]");
            await ReconcileAsync(actor, updated);
            var stale = await ReconcileAsync(actor, first);
            Assert.False(stale.IsAccepted);
            Assert.Equal("domain-events-stale-registry-snapshot", stale.ErrorCode);
            Assert.Equal(updated.Version, stale.Materialization.RegistryVersion);

            foreach (var org in new[] { "z-org", "a-org", "A-org" })
                await ReconcileAsync(actor, await ImportAsync(registry, organization: org));
            var state = await actor.Ask<DomainEventsCoordinatorState>(GetDomainEventsCoordinatorState.Instance, Timeout);
            Assert.Equal(new[] { "A-org", "a-org", "subscriptions-test", "z-org" },
                state.Organizations.Select(value => value.OrganizationId.Value));
            Assert.All(state.Organizations.Where(value => value.OrganizationId != first.OrganizationId), value =>
                Assert.Single(value.Subscriptions.Resolve(OrganizationEventType.BudgetThresholdReached)));
            Assert.Empty(state.Organizations.Single(value => value.OrganizationId == first.OrganizationId)
                .Subscriptions.Resolve(OrganizationEventType.BudgetThresholdReached));
        }
        finally { await system.Terminate(); }
    }

    internal static Task<DomainEventsReconciliationResult> ReconcileAsync(IActorRef actor, OrganizationRegistrySnapshot snapshot) =>
        actor.Ask<DomainEventsReconciliationResult>(new ReconcileDomainEventSubscriptions(snapshot), Timeout);

    internal static async Task<OrganizationRegistrySnapshot> ImportAsync(InMemoryOrganizationRegistry registry,
        string subscriptions = Declarations, string organization = "subscriptions-test")
    {
        var config = EventSubscriptionConfigurationTests.Configuration(subscriptions);
        config = new OrganizationConfiguration(new OrganizationHeader(OrganizationId.From(organization),
            config.Organization.RootUnit, config.Organization.Owner), config.Units, config.Positions, config.Prompts);
        var result = await new OrganizationConfigurationImporter(registry).ImportAsync(config);
        Assert.NotNull(result.Snapshot);
        return result.Snapshot!;
    }
}
