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
        services.AddSignalR();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<InboxRealtimeSubscriptionRegistry>();
        services.Replace(ServiceDescriptor.Singleton<
            IInboxReadModelChangeSink,
            SignalRInboxReadModelChangeSink>());
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
        services.TryAddSingleton<IInboxDecisionCommandSink>(serviceProvider =>
            serviceProvider.GetService<IPositionCommandRequester>() is { } requester
                ? new ShardedInboxDecisionCommandSink(requester)
                : UnavailableInboxDecisionCommandSink.Instance);
        return services;
    }
}
