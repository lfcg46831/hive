using Hive.Api.Authorization;
using Hive.Api.Inbox;
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
        services.AddSignalR();
        services.TryAddSingleton<InboxRealtimeSubscriptionRegistry>();
        services.TryAddSingleton<IOrganogramSnapshotReader, PostgreSqlOrganogramSnapshotReader>();
        services.TryAddSingleton<IOrganizationReadModel, OrganizationReadModel>();
        services.Replace(ServiceDescriptor.Singleton<
            IOrganizationReadModelChangeSink,
            SignalROrganizationReadModelChangeSink>());
        return services;
    }
}
