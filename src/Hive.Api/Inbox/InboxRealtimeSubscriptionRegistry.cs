using Hive.Api.Authorization;
using Hive.Domain.Identity;

namespace Hive.Api.Inbox;

internal readonly record struct InboxPrincipalGroup(
    OrganizationId OrganizationId,
    string PersonId)
{
    public string SignalRGroupName =>
        $"inbox-principal:{OrganizationId.Value}:{PersonId}";
}

/// <summary>
/// Tracks only live, authorized inbox subscriptions so projection changes can be routed from a
/// recipient position to the connected principal-person groups that currently occupy it.
/// </summary>
internal sealed class InboxRealtimeSubscriptionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<ConnectionSubscription>> _connections =
        new(StringComparer.Ordinal);
    private readonly Dictionary<InboxPosition, Dictionary<InboxPrincipalGroup, int>> _positions = [];

    public InboxPrincipalGroup Register(
        string connectionId,
        PersonOrganizationScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(scope);
        var principalGroup = new InboxPrincipalGroup(scope.OrganizationId, scope.PersonId);
        var positionIds = scope.PositionIds.ToArray();

        lock (_gate)
        {
            if (!_connections.TryGetValue(connectionId, out var subscriptions))
            {
                subscriptions = [];
                _connections.Add(connectionId, subscriptions);
            }

            if (subscriptions.Any(subscription => subscription.Group == principalGroup))
            {
                return principalGroup;
            }

            subscriptions.Add(new ConnectionSubscription(principalGroup, positionIds));
            foreach (var positionId in positionIds)
            {
                var position = new InboxPosition(scope.OrganizationId, positionId);
                if (!_positions.TryGetValue(position, out var principals))
                {
                    principals = [];
                    _positions.Add(position, principals);
                }

                principals[principalGroup] = principals.GetValueOrDefault(principalGroup) + 1;
            }
        }

        return principalGroup;
    }

    public bool Unregister(string connectionId, InboxPrincipalGroup principalGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            if (!_connections.TryGetValue(connectionId, out var subscriptions))
            {
                return false;
            }

            var index = subscriptions.FindIndex(subscription => subscription.Group == principalGroup);
            if (index < 0)
            {
                return false;
            }

            var subscription = subscriptions[index];
            subscriptions.RemoveAt(index);
            RemovePositions(subscription);
            if (subscriptions.Count == 0)
            {
                _connections.Remove(connectionId);
            }

            return true;
        }
    }

    public void RemoveConnection(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            if (!_connections.Remove(connectionId, out var subscriptions))
            {
                return;
            }

            foreach (var subscription in subscriptions)
            {
                RemovePositions(subscription);
            }
        }
    }

    public IReadOnlyList<InboxPrincipalGroup> PrincipalsFor(
        OrganizationId organizationId,
        PositionId positionId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);

        lock (_gate)
        {
            return _positions.TryGetValue(
                    new InboxPosition(organizationId, positionId),
                    out var principals)
                ? principals.Keys
                    .OrderBy(static principal => principal.PersonId, StringComparer.Ordinal)
                    .ToArray()
                : [];
        }
    }

    public bool IsSubscribed(InboxPrincipalGroup principalGroup)
    {
        lock (_gate)
        {
            return _connections.Values.Any(subscriptions =>
                subscriptions.Any(subscription => subscription.Group == principalGroup));
        }
    }

    private void RemovePositions(ConnectionSubscription subscription)
    {
        foreach (var positionId in subscription.PositionIds)
        {
            var position = new InboxPosition(subscription.Group.OrganizationId, positionId);
            var principals = _positions[position];
            if (principals[subscription.Group] == 1)
            {
                principals.Remove(subscription.Group);
            }
            else
            {
                principals[subscription.Group]--;
            }

            if (principals.Count == 0)
            {
                _positions.Remove(position);
            }
        }
    }

    private readonly record struct InboxPosition(
        OrganizationId OrganizationId,
        PositionId PositionId);

    private sealed record ConnectionSubscription(
        InboxPrincipalGroup Group,
        IReadOnlyList<PositionId> PositionIds);
}
