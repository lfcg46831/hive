using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Npgsql;

namespace Hive.Infrastructure.Governance;

public sealed record OrganizationActionGateRuntimeSnapshot(
    long Version,
    string Fingerprint,
    ActionDomainCatalog Catalog,
    ActionDomainCatalogBinding Binding);

public interface IOrganizationActionGateRuntimeProvider
{
    ValueTask<OrganizationActionGateRuntimeSnapshot?> FindAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default);
}

public sealed class RegistryOrganizationActionGateRuntimeProvider
    : IOrganizationActionGateRuntimeProvider
{
    private readonly IOrganizationRegistryReader _reader;
    private readonly IActionDomainContractRegistry _contractRegistry;

    public RegistryOrganizationActionGateRuntimeProvider(
        IOrganizationRegistryReader reader,
        IActionDomainContractRegistry contractRegistry)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _contractRegistry = contractRegistry ?? throw new ArgumentNullException(nameof(contractRegistry));
    }

    public async ValueTask<OrganizationActionGateRuntimeSnapshot?> FindAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        var snapshot = await _reader.FindSnapshotAsync(organizationId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        var binding = OrganizationActionDomainBinding.Create(snapshot, _contractRegistry);
        var validation = ActionDomainCatalogValidator.Validate(
            snapshot.ActionDomainCatalog.Value,
            binding);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(
                $"Organization '{organizationId.Value}' action-domain snapshot is invalid: "
                + string.Join("; ", validation.Errors.Select(error => $"{error.Path}:{error.Code}")));
        }

        return new OrganizationActionGateRuntimeSnapshot(
            snapshot.Version,
            snapshot.Fingerprint,
            snapshot.ActionDomainCatalog.Value,
            binding);
    }
}

public sealed class PostgreSqlOrganizationActionGateRuntimeProvider
    : IOrganizationActionGateRuntimeProvider, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly RegistryOrganizationActionGateRuntimeProvider _inner;

    public PostgreSqlOrganizationActionGateRuntimeProvider(
        string connectionString,
        IActionDomainContractRegistry contractRegistry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(contractRegistry);
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _inner = new RegistryOrganizationActionGateRuntimeProvider(
            new PostgreSqlOrganizationRegistry(_dataSource),
            contractRegistry);
    }

    public ValueTask<OrganizationActionGateRuntimeSnapshot?> FindAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        _inner.FindAsync(organizationId, cancellationToken);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

public sealed class UnavailableOrganizationActionGateRuntimeProvider
    : IOrganizationActionGateRuntimeProvider
{
    public static UnavailableOrganizationActionGateRuntimeProvider Instance { get; } = new();

    private UnavailableOrganizationActionGateRuntimeProvider()
    {
    }

    public ValueTask<OrganizationActionGateRuntimeSnapshot?> FindAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OrganizationActionGateRuntimeSnapshot?>(
            new InvalidOperationException("The organization action-domain registry is unavailable."));
}
