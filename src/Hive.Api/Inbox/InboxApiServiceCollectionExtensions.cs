using Hive.Api.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Api.Inbox;

public static class InboxApiServiceCollectionExtensions
{
    public static IServiceCollection AddHiveInboxApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHiveOrganizationAuthorization();
        services.TryAddSingleton<IInboxReadModel>(
            _ => UnavailableInboxReadModel.Instance);
        return services;
    }
}
