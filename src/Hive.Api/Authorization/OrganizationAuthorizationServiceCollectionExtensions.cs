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
                static options =>
                    options.Credentials.All(IsValidCredential) &&
                    options.Credentials
                        .Select(static credential => credential.Token)
                        .Distinct(StringComparer.Ordinal)
                        .Count() == options.Credentials.Count,
                $"{OrganizationAuthorizationOptions.SectionName}:Credentials must contain unique non-empty bearer tokens, at least one valid organization identifier, and either no person binding or one valid person identifier with occupied positions inside that organization scope.")
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

    private static bool IsValidCredential(OrganizationBearerCredentialOptions credential)
    {
        if (credential is null ||
            string.IsNullOrWhiteSpace(credential.Token) ||
            credential.OrganizationIds is null ||
            credential.OrganizationIds.Count == 0 ||
            !credential.OrganizationIds.All(IsValidOrganizationId))
        {
            return false;
        }

        if (credential.PersonId is null)
        {
            return credential.Positions is { Count: 0 };
        }

        if (string.IsNullOrWhiteSpace(credential.PersonId) ||
            !string.Equals(
                credential.PersonId,
                credential.PersonId.Trim(),
                StringComparison.Ordinal) ||
            credential.PersonId.Length > 256 ||
            credential.Positions is not { Count: > 0 })
        {
            return false;
        }

        var organizationIds = credential.OrganizationIds.ToHashSet(StringComparer.Ordinal);
        return credential.Positions.All(position =>
                position is not null &&
                organizationIds.Contains(position.OrganizationId) &&
                IsValidOrganizationId(position.OrganizationId) &&
                IsValidPositionId(position.PositionId)) &&
            credential.Positions
                .Select(static position => (position.OrganizationId, position.PositionId))
                .Distinct()
                .Count() == credential.Positions.Count;
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

    private static bool IsValidPositionId(string value)
    {
        try
        {
            _ = PositionId.From(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
