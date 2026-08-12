using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Identity;
using Hive.Infrastructure.OccupantChannels.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.OccupantChannels;

internal static class ImapInboundEmailServiceCollectionExtensions
{
    public static IServiceCollection AddHiveImapInboundEmailSource(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<
            IValidateOptions<ImapInboundEmailOptions>,
            ImapInboundEmailOptionsValidator>();
        services
            .AddOptions<ImapInboundEmailOptions>()
            .Bind(configuration.GetSection(ImapInboundEmailOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton<IImapInboundEmailClient, MailKitImapInboundEmailClient>();
        services.TryAddSingleton<IImapInboundEmailStore>(serviceProvider =>
        {
            var connectionString = serviceProvider
                .GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringNames.PostgreSql);
            return string.IsNullOrWhiteSpace(connectionString)
                ? UnavailableImapInboundEmailStore.Instance
                : new PostgreSqlImapInboundEmailStore(connectionString);
        });
        services.TryAddSingleton<IImapInboundEmailPoller, ImapInboundEmailPoller>();
        services.TryAddSingleton<IInboundOccupantEmailIdentityResolver>(
            UnavailableInboundOccupantEmailIdentityResolver.Instance);
        services.TryAddSingleton<IInboundOccupantEmailParser, InboundOccupantEmailParser>();
        services.TryAddSingleton<IInboundOccupantEmailProcessor, InboundOccupantEmailProcessor>();
        return services;
    }
}
