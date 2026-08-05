using Hive.Actors.Inbox;
using Hive.Infrastructure.Inbox.ReadModels;
using Hive.Infrastructure.Inbox.ReadModels.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Hive.Actors;

public static class InboxProjectionBootstrapExtensions
{
    public static IHostApplicationBuilder AddHiveInboxProjection(
        this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IInboxProjectionFeed, PostgreSqlInboxProjectionFeed>();
        builder.Services.TryAddSingleton<
            IInboxProjectionSnapshotReader,
            PostgreSqlInboxProjectionSnapshotReader>();
        builder.Services.TryAddSingleton<IInboxProjectionJournal, AkkaInboxProjectionJournal>();
        builder.Services.TryAddSingleton<InboxProjectionWorker>();
        builder.Services.AddHostedService<InboxProjectionHostedService>();
        return builder;
    }
}
