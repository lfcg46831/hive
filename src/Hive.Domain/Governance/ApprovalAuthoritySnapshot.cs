using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Governance;

/// <summary>
/// Immutable, materialized read model of the approval authority of a single organization, holding
/// exactly the policy entries that governance resolution (US-F0-04-T07a) queries: for each declared
/// <see cref="ApprovalPolicyRef"/>, the concrete authorized approver endpoint, the set of actions it
/// authorizes, and the version/hash of the configuration it was materialized from.
/// </summary>
/// <remarks>
/// <para>
/// This is the authority counterpart of <c>OrganizationRelationsSnapshot</c>. In F0 the snapshot is
/// held in memory and built directly; the GitOps/YAML import of US-F0-05 and the PostgreSQL-backed
/// materialization are responsible for populating it later from the <c>authority</c> sections of the
/// organization configuration. Building a snapshot is the only place that touches the structure —
/// every query is a pure lookup.
/// </para>
/// <para>
/// The builder enforces the minimal invariants the read model must always hold: every policy
/// declares a non-null approver endpoint, a non-empty set of authorized actions and an applied
/// version, and no policy key is declared twice. An organization may have no approval policies, in
/// which case every resolution yields <see cref="ApproverResolutionStatus.PolicyNotFound"/>.
/// </para>
/// </remarks>
public sealed class ApprovalAuthoritySnapshot
{
    private readonly IReadOnlyDictionary<ApprovalPolicyRef, PolicyEntry> _policies;

    private ApprovalAuthoritySnapshot(
        OrganizationId organizationId,
        IReadOnlyDictionary<ApprovalPolicyRef, PolicyEntry> policies)
    {
        OrganizationId = organizationId;
        _policies = policies;
    }

    /// <summary>The organization this snapshot describes. Every query is scoped to it.</summary>
    public OrganizationId OrganizationId { get; }

    /// <summary>Starts building a snapshot for <paramref name="organizationId"/>.</summary>
    public static Builder CreateBuilder(OrganizationId organizationId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        return new Builder(organizationId);
    }

    /// <summary>
    /// Resolves <paramref name="policy"/> against <paramref name="action"/>.
    /// </summary>
    /// <returns>
    /// A resolved outcome with the authorized approver and applied version when the policy exists
    /// and authorizes the action; otherwise the matching unresolved outcome.
    /// </returns>
    public ApproverResolution Resolve(ApprovalPolicyRef policy, string action)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(action);

        if (!_policies.TryGetValue(policy, out var entry))
        {
            return ApproverResolution.PolicyNotFound(policy);
        }

        return entry.Actions.Contains(action)
            ? ApproverResolution.Resolved(policy, entry.Approver, entry.Version)
            : ApproverResolution.ActionNotAuthorized(policy, entry.Version);
    }

    private sealed record PolicyEntry(
        EndpointRef Approver,
        IReadOnlySet<string> Actions,
        ApprovalPolicyVersion Version);

    /// <summary>
    /// Accumulates approval policies and validates the minimal invariants of the materialized read
    /// model when <see cref="Build"/> is called.
    /// </summary>
    public sealed class Builder
    {
        private readonly OrganizationId _organizationId;
        private readonly Dictionary<ApprovalPolicyRef, PolicyEntry> _policies = new();

        internal Builder(OrganizationId organizationId)
        {
            _organizationId = organizationId;
        }

        /// <summary>
        /// Declares <paramref name="policy"/> as authorizing <paramref name="approver"/> for the
        /// given <paramref name="actions"/>, materialized from configuration
        /// <paramref name="version"/>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The policy was already added or <paramref name="actions"/> is empty.
        /// </exception>
        public Builder AddPolicy(
            ApprovalPolicyRef policy,
            EndpointRef approver,
            ApprovalPolicyVersion version,
            IEnumerable<string> actions)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(approver);
            ArgumentNullException.ThrowIfNull(version);
            ArgumentNullException.ThrowIfNull(actions);

            if (approver is not (PositionEndpointRef or OrganizationOwnerEndpointRef))
            {
                throw new ArgumentException(
                    "An approval policy can only authorize a position or the organization owner as "
                    + $"approver, not '{approver.GetType().Name}'.",
                    nameof(approver));
            }

            if (_policies.ContainsKey(policy))
            {
                throw new ArgumentException(
                    $"Approval policy '{policy.Value}' was already added to the snapshot.",
                    nameof(policy));
            }

            var authorizedActions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in actions)
            {
                authorizedActions.Add(IdentityValue.RequireStructural(action, nameof(actions)));
            }

            if (authorizedActions.Count == 0)
            {
                throw new ArgumentException(
                    $"Approval policy '{policy.Value}' must authorize at least one action.",
                    nameof(actions));
            }

            _policies[policy] = new PolicyEntry(approver, authorizedActions, version);
            return this;
        }

        /// <summary>Produces an immutable snapshot of the accumulated policies.</summary>
        public ApprovalAuthoritySnapshot Build() =>
            new(_organizationId, new Dictionary<ApprovalPolicyRef, PolicyEntry>(_policies));
    }
}
