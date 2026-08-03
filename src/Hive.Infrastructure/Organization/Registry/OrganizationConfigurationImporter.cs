using Hive.Domain.Organization.Configuration;
using Hive.Domain.Organization.Configuration.Validation;
using Hive.Domain.Governance;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Hive.Infrastructure.Organization.ReadModels;

namespace Hive.Infrastructure.Organization.Registry;

public sealed class OrganizationConfigurationImporter
{
    private readonly IOrganizationRegistryStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly IOrganizationReadModelChangeSink _changeSink;

    public OrganizationConfigurationImporter(
        InMemoryOrganizationRegistry registry,
        TimeProvider? timeProvider = null)
        : this(
            (IOrganizationRegistryStore)registry,
            NoopOrganizationReadModelChangeSink.Instance,
            timeProvider)
    {
    }

    internal OrganizationConfigurationImporter(
        InMemoryOrganizationRegistry registry,
        IOrganizationReadModelChangeSink changeSink,
        TimeProvider? timeProvider = null)
        : this((IOrganizationRegistryStore)registry, changeSink, timeProvider)
    {
    }

    public OrganizationConfigurationImporter(
        PostgreSqlOrganizationRegistry registry,
        TimeProvider? timeProvider = null)
        : this(
            (IOrganizationRegistryStore)registry,
            NoopOrganizationReadModelChangeSink.Instance,
            timeProvider)
    {
    }

    internal OrganizationConfigurationImporter(
        PostgreSqlOrganizationRegistry registry,
        IOrganizationReadModelChangeSink changeSink,
        TimeProvider? timeProvider = null)
        : this((IOrganizationRegistryStore)registry, changeSink, timeProvider)
    {
    }

    private OrganizationConfigurationImporter(
        IOrganizationRegistryStore store,
        IOrganizationReadModelChangeSink changeSink,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _changeSink = changeSink ?? throw new ArgumentNullException(nameof(changeSink));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<OrganizationImportResult> ImportAsync(
        OrganizationConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        ImportAsync(
            configuration,
            new ActionDomainCatalog(
                1,
                new ActionDomainCatalogDefaults(ActionDomainGate.Escalate),
                []),
            new ActionDomainCatalogBinding(),
            cancellationToken);

    public async ValueTask<OrganizationImportResult> ImportAsync(
        OrganizationConfiguration configuration,
        ActionDomainCatalog actionDomainCatalog,
        ActionDomainCatalogBinding actionDomainBinding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(actionDomainCatalog);
        ArgumentNullException.ThrowIfNull(actionDomainBinding);
        cancellationToken.ThrowIfCancellationRequested();

        var validation = Validate(configuration);
        if (!validation.IsValid)
        {
            return Invalid(validation.Errors);
        }

        var actionDomainValidation = ActionDomainCatalogValidator.Validate(
            actionDomainCatalog,
            actionDomainBinding);
        if (!actionDomainValidation.IsValid)
        {
            return Invalid(actionDomainValidation.Errors.Select(error =>
                new OrganizationConfigurationValidationError(
                    error.Code,
                    $"action-domains.{error.Path}",
                    error.Message)));
        }

        OrganizationRegistryProjection target;
        try
        {
            target = OrganizationRegistryProjection.Create(configuration, actionDomainCatalog);
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            return Invalid(
            [
                new OrganizationConfigurationValidationError(
                    "command-relations-invalid",
                    "positions[].reports_to",
                    exception.Message),
            ]);
        }

        var result = await _store.ApplyAsync(
            target,
            _timeProvider.GetUtcNow(),
            cancellationToken);
        if (result.Status == OrganizationImportStatus.Applied)
        {
            var snapshot = result.Snapshot!;
            await _changeSink.OrganogramChangedAsync(
                snapshot.OrganizationId,
                snapshot.Version,
                snapshot.Fingerprint,
                snapshot.ImportedAt,
                cancellationToken);
        }

        return result;
    }

    private static OrganizationConfigurationValidationResult Validate(
        OrganizationConfiguration configuration) =>
        OrganizationConfigurationValidationResult.Create(
            OrganizationConfigurationUniquenessValidator.Validate(configuration).Errors
                .Concat(OrganizationConfigurationCrossReferenceValidator.Validate(configuration).Errors)
                .Concat(OrganizationConfigurationStructuralValidator.Validate(configuration).Errors));

    private static OrganizationImportResult Invalid(
        IEnumerable<OrganizationConfigurationValidationError> errors)
    {
        var validation = OrganizationConfigurationValidationResult.Create(errors);
        return new OrganizationImportResult(
            OrganizationImportStatus.Invalid,
            plan: null,
            snapshot: null,
            validation.Errors);
    }

}
