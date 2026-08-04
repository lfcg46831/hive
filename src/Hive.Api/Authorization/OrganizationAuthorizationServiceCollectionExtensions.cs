using Hive.Domain.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Api.Authorization;

public static class OrganizationAuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddHiveOrganizationAuthorization(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(static descriptor =>
                descriptor.ServiceType == typeof(OrganizationAuthorizationRegistrationMarker)))
        {
            return services;
        }

        services.AddSingleton<OrganizationAuthorizationRegistrationMarker>();

        services.AddOptions<OrganizationAuthorizationOptions>()
            .BindConfiguration(OrganizationAuthorizationOptions.SectionName)
            .Validate(
                static options => options.Credentials.All(static credential =>
                    !string.IsNullOrWhiteSpace(credential.Token) &&
                    credential.OrganizationIds.Count > 0 &&
                    credential.OrganizationIds.All(IsValidOrganizationId)),
                $"{OrganizationAuthorizationOptions.SectionName}:Credentials must contain a non-empty bearer token and at least one valid organization identifier.")
            .ValidateOnStart();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, StaticOrganizationBearerHandler>(
                OrganizationAuthorizationDefaults.AuthenticationScheme,
                displayName: null,
                configureOptions: null);
        services.AddAuthorization(options => options.AddPolicy(
            OrganizationAuthorizationDefaults.Policy,
            policy =>
            {
                policy.AddAuthenticationSchemes(
                    OrganizationAuthorizationDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            }));
        services.TryAddSingleton<
            IOrganizationPrincipalResolver,
            ClaimsOrganizationPrincipalResolver>();
        return services;
    }

    private sealed class OrganizationAuthorizationRegistrationMarker
    {
    }

    private static bool IsValidOrganizationId(string value)
    {
        try
        {
            _ = OrganizationId.From(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
