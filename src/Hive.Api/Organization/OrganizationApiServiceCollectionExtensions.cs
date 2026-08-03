using Hive.Api.Authorization;
using Hive.Infrastructure.Organization.ReadModels;
using Hive.Infrastructure.Organization.ReadModels.PostgreSql;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Api.Organization;

public static class OrganizationApiServiceCollectionExtensions
{
    public static IServiceCollection AddHiveOrganizationApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHiveOrganizationAuthorization();
        services.TryAddSingleton<IOrganogramSnapshotReader, PostgreSqlOrganogramSnapshotReader>();
        services.TryAddSingleton<IOrganizationReadModel, OrganizationReadModel>();
        return services;
    }
}
