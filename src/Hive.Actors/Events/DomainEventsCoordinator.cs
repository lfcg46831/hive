using System.Collections.Immutable;
using Akka.Actor;
using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Infrastructure.Organization.Registry;

namespace Hive.Actors.Events;

internal static class DomainEventsCoordinatorIdentity
{
    public const string LogicalName = "domain-events-coordinator";
    public const string SingletonManagerName = LogicalName;
    public const string SingletonName = "coordinator";
    public const string ProxyName = "domain-events-coordinator-proxy";
    public const string SingletonManagerPath = "/user/" + SingletonManagerName;
}

/// <summary>Rebuildable registry projection. Detection and durable checkpoints belong to T05.</summary>
internal sealed class DomainEventsCoordinator : ReceiveActor
{
    private ImmutableDictionary<OrganizationId, DomainEventsMaterialization> _organizations =
        ImmutableDictionary<OrganizationId, DomainEventsMaterialization>.Empty;

    public DomainEventsCoordinator()
    {
        Receive<ReconcileDomainEventSubscriptions>(Reconcile);
        Receive<GetDomainEventsCoordinatorState>(_ => Sender.Tell(new DomainEventsCoordinatorState(
            _organizations.Values.OrderBy(value => value.OrganizationId.Value, StringComparer.Ordinal)
                .ToImmutableArray())));
        Receive<WhereIsDomainEventsCoordinator>(_ => Sender.Tell(Self));
    }

    public static Props Props() => Akka.Actor.Props.Create(() => new DomainEventsCoordinator());

    private void Reconcile(ReconcileDomainEventSubscriptions command)
    {
        var snapshot = command.Snapshot;
        if (_organizations.TryGetValue(snapshot.OrganizationId, out var current))
        {
            if (snapshot.Version < current.RegistryVersion)
            {
                Sender.Tell(new DomainEventsReconciliationResult(false, false, current,
                    "domain-events-stale-registry-snapshot"));
                return;
            }

            if (snapshot.Version == current.RegistryVersion)
            {
                var same = string.Equals(snapshot.Fingerprint, current.RegistryFingerprint, StringComparison.Ordinal);
                Sender.Tell(new DomainEventsReconciliationResult(same, false, current,
                    same ? null : "domain-events-conflicting-registry-snapshot"));
                return;
            }
        }

        var materialization = new DomainEventsMaterialization(snapshot.OrganizationId, snapshot.Version,
            snapshot.Fingerprint, snapshot.EventSubscriptions);
        _organizations = _organizations.SetItem(snapshot.OrganizationId, materialization);
        Sender.Tell(new DomainEventsReconciliationResult(true, true, materialization));
    }
}

internal sealed record ReconcileDomainEventSubscriptions
{
    public ReconcileDomainEventSubscriptions(OrganizationRegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
    }

    public OrganizationRegistrySnapshot Snapshot { get; }
}

internal sealed record DomainEventsMaterialization(OrganizationId OrganizationId, long RegistryVersion,
    string RegistryFingerprint, EventSubscriptionsSnapshot Subscriptions);

internal sealed record DomainEventsReconciliationResult(bool IsAccepted, bool IsChanged,
    DomainEventsMaterialization Materialization, string? ErrorCode = null);

internal sealed record DomainEventsCoordinatorState(ImmutableArray<DomainEventsMaterialization> Organizations);

internal sealed record GetDomainEventsCoordinatorState
{
    public static GetDomainEventsCoordinatorState Instance { get; } = new();
}

internal sealed record WhereIsDomainEventsCoordinator
{
    public static WhereIsDomainEventsCoordinator Instance { get; } = new();
}
