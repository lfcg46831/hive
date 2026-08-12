using Hive.Domain.OccupantChannels;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.OccupantChannels;

internal static class SmtpOccupantChannelServiceCollectionExtensions
{
    public static IServiceCollection AddHiveSmtpOccupantChannel(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<
            IValidateOptions<SmtpOccupantChannelOptions>,
            SmtpOccupantChannelOptionsValidator>();
        services
            .AddOptions<SmtpOccupantChannelOptions>()
            .Bind(configuration.GetSection(SmtpOccupantChannelOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton<IOccupantEmailBindingResolver>(
            UnavailableOccupantEmailBindingResolver.Instance);
        services.TryAddSingleton<ISmtpOccupantTransport, MailKitSmtpOccupantTransport>();
        services.TryAddSingleton<SmtpOccupantEmailRenderer>();
        services.TryAddSingleton<ISmtpRetryDelay>(SystemSmtpRetryDelay.Instance);

        if (IsEnabledConnectorNode(configuration))
        {
            services.TryAddSingleton<IOccupantChannel, SmtpOccupantChannel>();
        }

        return services;
    }

    private static bool IsEnabledConnectorNode(IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool>(
            $"{SmtpOccupantChannelOptions.SectionName}:Enabled");
        var roles = configuration
            .GetSection($"{HiveOptions.SectionName}:Node:Roles")
            .Get<string[]>() ?? [];

        return enabled && roles.Any(role => string.Equals(
            role?.Trim(),
            NodeRoleNames.Connectors,
            StringComparison.OrdinalIgnoreCase));
    }
}
