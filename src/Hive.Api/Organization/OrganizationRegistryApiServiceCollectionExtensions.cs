namespace Hive.Api.Organization;

public static class OrganizationRegistryApiServiceCollectionExtensions
{
    public static IServiceCollection AddHiveOrganizationRegistryApi(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<OrganizationRegistryApiReader>();
        return services;
    }
}
