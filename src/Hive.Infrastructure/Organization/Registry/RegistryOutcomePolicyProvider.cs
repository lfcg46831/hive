using Hive.Domain.Identity;
using Hive.Domain.Outcomes;

namespace Hive.Infrastructure.Organization.Registry;

/// <summary>Reads versioned outcome-policy overlays from an organization registry snapshot.</summary>
public sealed class RegistryOutcomePolicyProvider : IOutcomePolicyProvider
{
    private readonly IOrganizationRegistryReader _registryReader;

    public RegistryOutcomePolicyProvider(IOrganizationRegistryReader registryReader)
    {
        _registryReader = registryReader
            ?? throw new ArgumentNullException(nameof(registryReader));
    }

    public async ValueTask<OutcomePolicySnapshot> GetPolicyAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);

        var snapshot = await _registryReader
            .FindSnapshotAsync(organizationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Organization '{organizationId.Value}' has no registry policy snapshot.");

        if (snapshot.OrganizationId != organizationId ||
            snapshot.Organization?.Value is not { } organization ||
            organization.Id != organizationId)
        {
            throw new InvalidDataException(
                $"Registry policy snapshot for organization '{organizationId.Value}' is incoherent.");
        }

        if (snapshot.Occupants is null ||
            !snapshot.Occupants.TryGetValue(positionId, out var occupantEntry) ||
            occupantEntry?.Value is not { } occupant ||
            occupant.PositionId != positionId)
        {
            throw new InvalidDataException(
                $"Registry policy snapshot has no coherent occupant for position '{positionId.Value}'.");
        }

        return OutcomePolicyComposer.ComposeV1(
            snapshot.Version,
            snapshot.Fingerprint,
            organization.OutcomePolicy,
            occupant.OutcomePolicy,
            occupant.Ai?.MaxIterations);
    }
}
