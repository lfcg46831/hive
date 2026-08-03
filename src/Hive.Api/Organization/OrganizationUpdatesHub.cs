using System.Security.Claims;
using Hive.Api.Authorization;
using Hive.Contracts.Organization;
using Hive.Domain.Identity;
using Microsoft.AspNetCore.SignalR;

namespace Hive.Api.Organization;

public interface IOrganizationUpdatesClient
{
    Task OrganogramChanged(OrganogramChangedNotification notification);

    Task PositionStateChanged(PositionStateChangedNotification notification);
}

internal sealed class OrganizationUpdatesHub(
    IOrganizationPrincipalResolver principalResolver) : Hub<IOrganizationUpdatesClient>
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

    private static HubException OrganizationNotFound() =>
        new("Organization not found");
}
