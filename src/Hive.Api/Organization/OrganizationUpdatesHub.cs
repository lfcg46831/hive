using System.Security.Claims;
using Hive.Api.Authorization;
using Hive.Api.Inbox;
using Hive.Contracts.Inbox;
using Hive.Contracts.Organization;
using Hive.Domain.Identity;
using Microsoft.AspNetCore.SignalR;

namespace Hive.Api.Organization;

public interface IOrganizationUpdatesClient
{
    Task OrganogramChanged(OrganogramChangedNotification notification);

    Task PositionStateChanged(PositionStateChangedNotification notification);

    Task InboxChanged(InboxChangedNotification notification);
}

internal sealed class OrganizationUpdatesHub(
    IOrganizationPrincipalResolver principalResolver,
    InboxRealtimeSubscriptionRegistry inboxSubscriptions) : Hub<IOrganizationUpdatesClient>
{
    public async Task SubscribeToOrganization(string organizationId)
    {
        var parsed = await ParseAuthorizedOrganizationIdAsync(organizationId);
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            GroupName(parsed),
            Context.ConnectionAborted);
    }

    public async Task UnsubscribeFromOrganization(string organizationId)
    {
        var parsed = await ParseAuthorizedOrganizationIdAsync(organizationId);
        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            GroupName(parsed),
            Context.ConnectionAborted);
    }

    public async Task SubscribeToInbox(string organizationId)
    {
        var scope = await ParseAuthorizedInboxScopeAsync(organizationId);
        var principalGroup = inboxSubscriptions.Register(Context.ConnectionId, scope);
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            principalGroup.SignalRGroupName,
            Context.ConnectionAborted);
    }

    public async Task UnsubscribeFromInbox(string organizationId)
    {
        var scope = await ParseAuthorizedInboxScopeAsync(organizationId);
        var principalGroup = new InboxPrincipalGroup(scope.OrganizationId, scope.PersonId);
        if (inboxSubscriptions.Unregister(Context.ConnectionId, principalGroup))
        {
            await Groups.RemoveFromGroupAsync(
                Context.ConnectionId,
                principalGroup.SignalRGroupName,
                Context.ConnectionAborted);
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        inboxSubscriptions.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    internal static string GroupName(OrganizationId organizationId) =>
        $"organization:{organizationId.Value}";

    private async ValueTask<OrganizationId> ParseAuthorizedOrganizationIdAsync(string value)
    {
        OrganizationId organizationId;
        try
        {
            organizationId = OrganizationId.From(value);
        }
        catch (ArgumentException)
        {
            throw OrganizationNotFound();
        }

        var principal = await principalResolver.ResolveAsync(
            Context.User ?? new ClaimsPrincipal(),
            Context.ConnectionAborted);
        if (!principal.CanRead(organizationId))
        {
            throw OrganizationNotFound();
        }

        return organizationId;
    }

    private async ValueTask<PersonOrganizationScope> ParseAuthorizedInboxScopeAsync(string value)
    {
        var organizationId = await ParseAuthorizedOrganizationIdAsync(value);
        var principal = await principalResolver.ResolveAsync(
            Context.User ?? new ClaimsPrincipal(),
            Context.ConnectionAborted);
        var scope = principal.PersonScopeFor(organizationId);
        if (scope is null || scope.PositionIds.Count == 0)
        {
            throw OrganizationNotFound();
        }

        return scope;
    }

    private static HubException OrganizationNotFound() =>
        new("Organization not found");
}
