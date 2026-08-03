using Hive.Domain.Identity;

namespace Hive.Api.Authorization;

internal sealed class OrganizationReadAuthorizationFilter(
    IOrganizationPrincipalResolver principalResolver) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var rawOrganizationId = context.HttpContext.Request.RouteValues["organizationId"] as string;
        if (!TryParseOrganizationId(rawOrganizationId, out var organizationId))
        {
            return await next(context).ConfigureAwait(false);
        }

        var principal = await principalResolver.ResolveAsync(
                context.HttpContext.User,
                context.HttpContext.RequestAborted)
            .ConfigureAwait(false);
        if (!principal.CanRead(organizationId!))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Organization not found");
        }

        return await next(context).ConfigureAwait(false);
    }

    private static bool TryParseOrganizationId(
        string? value,
        out OrganizationId? organizationId)
    {
        try
        {
            organizationId = OrganizationId.From(value!);
            return true;
        }
        catch (ArgumentException)
        {
            organizationId = null;
            return false;
        }
    }
}
