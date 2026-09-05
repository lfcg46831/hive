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

        // Inactive sources must not contribute a dependency graph that requires email secrets,
        // including when the host validates service registrations before startup.
        if (!IsEnabledConnectorNode(configuration))
        {
            return services;
        }

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

    internal static bool IsEnabledConnectorNode(IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool>(
            $"{ImapInboundEmailOptions.SectionName}:Enabled");
        var roles = configuration
            .GetSection($"{HiveOptions.SectionName}:Node:Roles")
            .Get<string[]>() ?? [];

        return enabled && roles.Any(role => string.Equals(
            role?.Trim(),
            NodeRoleNames.Connectors,
            StringComparison.OrdinalIgnoreCase));
    }
}
