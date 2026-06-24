using Hive.Domain.Organization.Configuration;
using Hive.Domain.Organization.Configuration.Validation;
using Hive.Infrastructure.Organization.Registry.PostgreSql;

namespace Hive.Infrastructure.Organization.Registry;

public sealed class OrganizationConfigurationImporter
{
    private readonly IOrganizationRegistryStore _store;
    private readonly TimeProvider _timeProvider;

    public OrganizationConfigurationImporter(
        InMemoryOrganizationRegistry registry,
        TimeProvider? timeProvider = null)
        : this((IOrganizationRegistryStore)registry, timeProvider)
    {
    }

    public OrganizationConfigurationImporter(
        PostgreSqlOrganizationRegistry registry,
        TimeProvider? timeProvider = null)
        : this((IOrganizationRegistryStore)registry, timeProvider)
    {
    }

    private OrganizationConfigurationImporter(
        IOrganizationRegistryStore store,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<OrganizationImportResult> ImportAsync(
        OrganizationConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();

        var validation = Validate(configuration);
        if (!validation.IsValid)
        {
            return Invalid(validation.Errors);
        }

        OrganizationRegistryProjection target;
        try
        {
            target = OrganizationRegistryProjection.Create(configuration);
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

        return await _store.ApplyAsync(
            target,
            _timeProvider.GetUtcNow(),
            cancellationToken);
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
