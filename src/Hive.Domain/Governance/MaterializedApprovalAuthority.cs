using Hive.Domain.Identity;

namespace Hive.Domain.Governance;

/// <summary>
/// Read-only <see cref="IApprovalAuthority"/> served over the materialized authority registry
/// (US-F0-04-T07a). It resolves approvers from one <see cref="ApprovalAuthoritySnapshot"/> per
/// organization and never mutates them, mirroring the contract's outcome/exception semantics.
/// </summary>
/// <remarks>
/// In F0 the snapshots are materialized in memory and supplied at construction; later phases
/// (US-F0-05 GitOps import, PostgreSQL read model) own how they are produced. Because every query is
/// a pure dictionary lookup, the implementation is thread-safe and the snapshots are immutable. The
/// authorized approver is resolved from the policy and action declared in the configuration; the
/// requester and proposed approver are carried on the query as context and are not consulted here.
/// </remarks>
public sealed class MaterializedApprovalAuthority : IApprovalAuthority
{
    private readonly IReadOnlyDictionary<OrganizationId, ApprovalAuthoritySnapshot> _snapshots;

    /// <summary>
    /// Creates a resolver over the supplied materialized authority snapshots.
    /// </summary>
    /// <exception cref="ArgumentException">Two snapshots describe the same organization.</exception>
    public MaterializedApprovalAuthority(IEnumerable<ApprovalAuthoritySnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var byOrganization = new Dictionary<OrganizationId, ApprovalAuthoritySnapshot>();
        foreach (var snapshot in snapshots)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            if (!byOrganization.TryAdd(snapshot.OrganizationId, snapshot))
            {
                throw new ArgumentException(
                    $"More than one snapshot was supplied for organization '{snapshot.OrganizationId.Value}'.",
                    nameof(snapshots));
            }
        }

        _snapshots = byOrganization;
    }

    /// <summary>Creates a resolver over a single organization snapshot.</summary>
    public MaterializedApprovalAuthority(ApprovalAuthoritySnapshot snapshot)
        : this(new[] { snapshot ?? throw new ArgumentNullException(nameof(snapshot)) })
    {
    }

    public ValueTask<ApproverResolution> ResolveApproverAsync(
        ApprovalAuthorityQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_snapshots.TryGetValue(query.OrganizationId, out var snapshot))
        {
            throw ApprovalAuthorityNotFoundException.ForOrganization(query.OrganizationId);
        }

        return new ValueTask<ApproverResolution>(snapshot.Resolve(query.Policy, query.Action));
    }
}
