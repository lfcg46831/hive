using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Api.Organization;

public static class OrganizationApiServiceCollectionExtensions
{
    public static IServiceCollection AddHiveOrganizationApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IOrganizationReadModel>(
            _ => UnavailableOrganizationReadModel.Instance);
        return services;
    }
}
