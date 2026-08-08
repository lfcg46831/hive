using Hive.Api.Authorization;
using Hive.Api.Directives;
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
            serviceProvider.GetService<IInboxProjectionSnapshotReader>() is { } snapshotReader &&
            serviceProvider.GetService<IInboxInteractionReader>() is { } interactionReader
                ? new ProjectionInboxReadModel(
                    snapshotReader,
                    interactionReader,
                    serviceProvider.GetRequiredService<TimeProvider>())
                : UnavailableInboxReadModel.Instance);
        services.TryAddSingleton<IInboxInteractionCommandSink>(serviceProvider =>
            serviceProvider.GetService<IInboxInteractionStore>() is { } interactionStore
                ? new DurableInboxInteractionCommandSink(interactionStore)
                : UnavailableInboxInteractionCommandSink.Instance);
        services.TryAddSingleton<IInboxReplyCommandSink>(serviceProvider =>
            serviceProvider.GetService<IPositionCommandRequester>() is { } requester
                ? new ShardedInboxReplyCommandSink(requester)
                : UnavailableInboxReplyCommandSink.Instance);
        return services;
    }
}
