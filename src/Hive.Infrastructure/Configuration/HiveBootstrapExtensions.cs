using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Infrastructure.Auditing;
using Hive.Infrastructure.Auditing.PostgreSql;
using Hive.Domain.Positions;
using Hive.Domain.Organization;
using Hive.Infrastructure.Ai;
using Hive.Infrastructure.Diagnostics;
using Hive.Infrastructure.Hosting;
using Hive.Infrastructure.Logging;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Hive.Infrastructure.Persistence.PostgreSql;
using Hive.Infrastructure.Scheduling;
using Hive.Infrastructure.Scheduling.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Configuration;

public static class HiveBootstrapExtensions
{
    public static IHostApplicationBuilder AddHiveBootstrap(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddHiveStructuredLogging();

        builder.Services.AddSingleton<IValidateOptions<HiveOptions>, HiveOptionsValidator>();
        builder.Services
            .AddOptions<HiveOptions>()
            .Bind(builder.Configuration.GetSection(HiveOptions.SectionName))
            .ValidateOnStart();

        builder.Services.AddSingleton<ActiveNodeRoles>();
        builder.Services.AddHiveAiGateway(builder.Configuration);
        builder.Services.TryAddSingleton<IJourneyAuditLog>(serviceProvider =>
        {
            var connectionString = serviceProvider
                .GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringNames.PostgreSql);

            return string.IsNullOrWhiteSpace(connectionString)
                ? NoopJourneyAuditLog.Instance
                : new PostgreSqlJourneyAuditLog(connectionString);
        });
        builder.Services.TryAddSingleton<IJourneyAuditReadModel>(serviceProvider =>
        {
            var connectionString = serviceProvider
                .GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringNames.PostgreSql);

            return string.IsNullOrWhiteSpace(connectionString)
                ? NoopJourneyAuditReadModel.Instance
                : new PostgreSqlJourneyAuditReadModel(connectionString);
        });
        builder.Services.Replace(ServiceDescriptor.Singleton<
            JourneyAuditAiGatewayPublisher,
            JourneyAuditAiGatewayPublisher>());
        builder.Services.Replace(ServiceDescriptor.Singleton<IAiGatewayAuditPublisher>(
            serviceProvider => serviceProvider.GetRequiredService<JourneyAuditAiGatewayPublisher>()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IAiGatewayDetailedAuditPublisher>(
            serviceProvider => serviceProvider.GetRequiredService<JourneyAuditAiGatewayPublisher>()));
        builder.Services.TryAddSingleton<IPositionConfigurationProvider>(serviceProvider =>
        {
            var connectionString = serviceProvider
                .GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringNames.PostgreSql);
            var organizationsRoot = serviceProvider
                .GetRequiredService<IOptions<HiveOptions>>()
                .Value
                .Organizations
                .RootPath;

            return string.IsNullOrWhiteSpace(connectionString)
                ? new UnavailablePositionConfigurationProvider(ConnectionStringNames.PostgreSql)
                : new PostgreSqlPositionConfigurationProvider(connectionString, organizationsRoot);
        });
        builder.Services.TryAddSingleton<IOrganizationRelations>(serviceProvider =>
        {
            var connectionString = serviceProvider
                .GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringNames.PostgreSql);

            return string.IsNullOrWhiteSpace(connectionString)
                ? new UnavailableOrganizationRelations(ConnectionStringNames.PostgreSql)
                : new PostgreSqlOrganizationRelations(connectionString);
        });
        builder.Services.TryAddSingleton<ISchedulerPulseDeliveryStore>(serviceProvider =>
        {
            var connectionString = serviceProvider
                .GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringNames.PostgreSql);

            return string.IsNullOrWhiteSpace(connectionString)
                ? NoopSchedulerPulseDeliveryStore.Instance
                : new PostgreSqlSchedulerPulseDeliveryStore(connectionString);
        });
        builder.Services.AddHostedService<PostgreSqlOrganizationRegistryMigrationHostedService>();
        builder.Services.AddHostedService<PostgreSqlOrganizationRegistryImportHostedService>();
        builder.Services.AddHostedService<PostgreSqlPositionPersistenceMigrationHostedService>();
        builder.Services.AddHostedService<PostgreSqlSchedulerPulseDeliveryMigrationHostedService>();
        builder.Services.AddHostedService<PostgreSqlJourneyAuditLogMigrationHostedService>();
        builder.Services.AddHostedService<RoleWorkloadHostedService>();

        builder.Services.AddHiveHealthChecks();

        return builder;
    }
}
