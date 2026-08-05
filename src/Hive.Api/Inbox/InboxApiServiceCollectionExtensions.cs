using Hive.Api.Authorization;
using Hive.Infrastructure.Inbox.ReadModels;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Api.Inbox;

public static class InboxApiServiceCollectionExtensions
{
    public static IServiceCollection AddHiveInboxApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHiveOrganizationAuthorization();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IInboxReadModel>(serviceProvider =>
            serviceProvider.GetService<IInboxProjectionSnapshotReader>() is { } snapshotReader
                ? new ProjectionInboxReadModel(
                    snapshotReader,
                    serviceProvider.GetRequiredService<TimeProvider>())
                : UnavailableInboxReadModel.Instance);
        return services;
    }
}
