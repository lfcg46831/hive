using Hive.Domain.Governance;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;

namespace Hive.Infrastructure.Governance;

public static class OrganizationActionDomainBinding
{
    public static ActionDomainCatalogBinding Create(
        OrganizationConfiguration configuration,
        IActionDomainContractRegistry contractRegistry)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(contractRegistry);

        return new ActionDomainCatalogBinding(
            authorities: configuration.Positions
                .Where(position => position.Occupant.Authority is not null)
                .Select(position => new ActionDomainAuthorityBinding(
                    $"positions[{position.Id.Value}].authority",
                    position.Occupant.Authority!.CanDecide,
                    position.Occupant.Authority.Overrides.Select(item =>
                        new ActionDomainAuthorityOverride(item.Key, item.Gate, item.Approver)).ToArray()))
                .ToArray(),
            declaredApprovers: configuration.Positions.Select(position => position.Id.Value).ToArray(),
            actionContracts: contractRegistry.ActionContracts,
            actionExtractors: contractRegistry.ActionExtractors);
    }

    public static ActionDomainCatalogBinding Create(
        OrganizationRegistrySnapshot snapshot,
        IActionDomainContractRegistry contractRegistry)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(contractRegistry);

        return new ActionDomainCatalogBinding(
            authorities: snapshot.Authorities
                .OrderBy(pair => pair.Key.Value, StringComparer.Ordinal)
                .Select(pair => new ActionDomainAuthorityBinding(
                    $"positions[{pair.Key.Value}].authority",
                    pair.Value.Value.CanDecide.Select(AuthorityKey.From).ToArray(),
                    pair.Value.Value.Overrides.Select(item => new ActionDomainAuthorityOverride(
                        AuthorityKey.From(item.Key), item.Gate, item.Approver)).ToArray()))
                .ToArray(),
            declaredApprovers: snapshot.Positions.Keys
                .Select(position => position.Value)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            actionContracts: contractRegistry.ActionContracts,
            actionExtractors: contractRegistry.ActionExtractors);
    }
}
