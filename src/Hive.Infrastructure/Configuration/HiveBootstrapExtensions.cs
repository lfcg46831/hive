using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Infrastructure.Auditing;
using Hive.Infrastructure.Auditing.PostgreSql;
using Hive.Domain.Positions;
using Hive.Domain.Organization;
using Hive.Domain.Outcomes;
using Hive.Infrastructure.Ai;
using Hive.Infrastructure.Connectors;
using Hive.Infrastructure.Diagnostics;
using Hive.Infrastructure.Hosting;
using Hive.Infrastructure.Inbox.ReadModels.PostgreSql;
using Hive.Infrastructure.Governance;
using Hive.Infrastructure.Logging;
using Hive.Infrastructure.Organization.ReadModels;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Hive.Infrastructure.OccupantChannels;
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
        builder.Services.AddSingleton<
            IValidateOptions<DirectiveAuditExportOptions>,
            DirectiveAuditExportOptionsValidator>();
        builder.Services
            .AddOptions<HiveOptions>()
            .Bind(builder.Configuration.GetSection(HiveOptions.SectionName))
            .ValidateOnStart();
        builder.Services
            .AddOptions<DirectiveAuditExportOptions>()
            .Bind(builder.Configuration.GetSection(DirectiveAuditExportOptions.SectionName))
            .ValidateOnStart();

        builder.Services.TryAddSingleton(serviceProvider =>
            DirectiveAuditExportScopeCatalog.Load(
                serviceProvider
                    .GetRequiredService<IOptions<DirectiveAuditExportOptions>>()
                    .Value));

        builder.Services.AddSingleton<ActiveNodeRoles>();
        builder.Services.AddHiveOccupantChannelCorrelationTokens(builder.Configuration);
        builder.Services.AddHiveSmtpOccupantChannel(builder.Configuration);
        builder.Services.AddHiveImapInboundEmailSource(builder.Configuration);
        builder.Services.AddHiveConnectorPlugins(builder.Configuration);
        builder.Services.AddHiveActionDomainContracts();
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
        builder.Services.TryAddSingleton<IOrganizationReadModelChangeSink>(
            NoopOrganizationReadModelChangeSink.Instance);
        builder.Services.TryAddSingleton<IJourneyAuditReadModel>(serviceProvider =>
        {
            var connectionString = serviceProvider
                .GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringNames.PostgreSql);

            return string.IsNullOrWhiteSpace(connectionString)
                ? NoopJourneyAuditReadModel.Instance
                : new PostgreSqlJourneyAuditReadModel(connectionString);
        });
        builder.Services.TryAddSingleton(serviceProvider =>
        {
            var connectionString = serviceProvider
                .GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringNames.PostgreSql);
            return new DirectiveAuditExportStoreProvider(
                serviceProvider.GetRequiredService<DirectiveAuditExportScopeCatalog>(),
                connectionString);
        });
        builder.Services.TryAddSingleton<IDirectiveAuditExportReader>(serviceProvider =>
        {
            var catalog = serviceProvider
                .GetRequiredService<DirectiveAuditExportScopeCatalog>();
            var reader = serviceProvider
                .GetRequiredService<DirectiveAuditExportStoreProvider>()
                .Reader;
            return ReferenceEquals(reader, NoopDirectiveAuditExportStore.Instance)
                ? NoopDirectiveAuditExportStore.Instance
                : new ScopedDirectiveAuditExportReader(catalog, reader);
        });
        builder.Services.TryAddSingleton<IDirectiveAuditExportResultSink>(serviceProvider =>
        {
            var catalog = serviceProvider
                .GetRequiredService<DirectiveAuditExportScopeCatalog>();
            var sink = serviceProvider
                .GetRequiredService<DirectiveAuditExportStoreProvider>()
                .ResultSink;
            return ReferenceEquals(sink, NoopDirectiveAuditExportStore.Instance)
                ? NoopDirectiveAuditExportStore.Instance
                : new ScopedDirectiveAuditExportResultSink(catalog, sink);
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
        builder.Services.TryAddSingleton<IExecutionFactsMaterializer, ExecutionFactsMaterializer>();
        builder.Services.TryAddSingleton<OrganizationalOutcomeContextComposer>();
        builder.Services.TryAddSingleton<IOrganizationalOutcomeResolver, OrganizationalOutcomeResolver>();
        builder.Services.TryAddSingleton<IOutcomeVerifier, AiGatewayOutcomeVerifier>();
        builder.Services.TryAddSingleton<
            IOrganizationalOutcomeOrchestrator,
            OrganizationalOutcomeOrchestrator>();
        builder.Services.TryAddSingleton<IOutcomePolicyProvider>(serviceProvider =>
        {
            var connectionString = serviceProvider
                .GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringNames.PostgreSql);

            return string.IsNullOrWhiteSpace(connectionString)
                ? new UnavailableOutcomePolicyProvider(ConnectionStringNames.PostgreSql)
                : new PostgreSqlOutcomePolicyProvider(connectionString);
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
        builder.Services.TryAddSingleton<IOrganizationActionGateRuntimeProvider>(serviceProvider =>
        {
            var connectionString = serviceProvider
                .GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringNames.PostgreSql);

            return string.IsNullOrWhiteSpace(connectionString)
                ? UnavailableOrganizationActionGateRuntimeProvider.Instance
                : new PostgreSqlOrganizationActionGateRuntimeProvider(
                    connectionString,
                    serviceProvider.GetRequiredService<IActionDomainContractRegistry>());
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
        builder.Services.AddHostedService<PostgreSqlInboxProjectionMigrationHostedService>();
        builder.Services.AddHostedService<RoleWorkloadHostedService>();

        builder.Services.AddHiveHealthChecks();

        return builder;
    }
}
