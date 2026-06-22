using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Governance;

/// <summary>
/// Outcome of resolving the authorized approver of an <see cref="ApprovalRequest"/>.
/// </summary>
public enum ApproverResolutionStatus
{
    /// <summary>The policy authorized a concrete approver for the requested action.</summary>
    Resolved = 1,

    /// <summary>The <see cref="ApprovalPolicyRef"/> is not declared in the organization.</summary>
    PolicyNotFound = 2,

    /// <summary>The policy exists but does not authorize the requested action.</summary>
    ActionNotAuthorized = 3,
}

/// <summary>
/// The result of <see cref="IApprovalAuthority.ResolveApproverAsync"/> (US-F0-04-T07a): either the
/// concrete authorized approver endpoint together with the applied policy version/hash, or a
/// structured reason why no approver could be resolved.
/// </summary>
/// <remarks>
/// <para>
/// This type resolves and records; it does not accept or reject. The downstream
/// <see cref="ApprovalRequest"/>/<see cref="ApprovalDecision"/> validation (US-F0-04-T07b) compares
/// the proposed destination against <see cref="ResolvedApprover"/> and maps the unresolved statuses
/// to the canonical structured rejections of §9.8 (US-F0-04-T07c), while the resolved
/// <see cref="ResolvedApprover"/> and <see cref="AppliedVersion"/> feed the auditable routing
/// context of US-F0-04-T08.
/// </para>
/// <para>
/// <see cref="ResolvedApprover"/> is non-null only when <see cref="Status"/> is
/// <see cref="ApproverResolutionStatus.Resolved"/>. <see cref="AppliedVersion"/> is present whenever
/// the policy was found (both <see cref="ApproverResolutionStatus.Resolved"/> and
/// <see cref="ApproverResolutionStatus.ActionNotAuthorized"/>), so audit always records the version
/// of the configuration that was interpreted; it is <see langword="null"/> only when the policy
/// itself is absent.
/// </para>
/// </remarks>
public sealed record ApproverResolution
{
    private ApproverResolution(
        ApproverResolutionStatus status,
        ApprovalPolicyRef policy,
        EndpointRef? resolvedApprover,
        ApprovalPolicyVersion? appliedVersion)
    {
        Status = status;
        Policy = policy;
        ResolvedApprover = resolvedApprover;
        AppliedVersion = appliedVersion;
    }

    /// <summary>The resolution outcome.</summary>
    public ApproverResolutionStatus Status { get; }

    /// <summary>The policy selector that was resolved.</summary>
    public ApprovalPolicyRef Policy { get; }

    /// <summary>
    /// The concrete authorized approver endpoint, or <see langword="null"/> unless
    /// <see cref="Status"/> is <see cref="ApproverResolutionStatus.Resolved"/>.
    /// </summary>
    public EndpointRef? ResolvedApprover { get; }

    /// <summary>
    /// The version/hash of the applied authority configuration, present whenever the policy was
    /// found and <see langword="null"/> when it is absent.
    /// </summary>
    public ApprovalPolicyVersion? AppliedVersion { get; }

    /// <summary>Whether a concrete authorized approver was resolved.</summary>
    public bool IsResolved => Status == ApproverResolutionStatus.Resolved;

    /// <summary>
    /// Creates a resolved outcome with the authorized <paramref name="approver"/> and the applied
    /// <paramref name="version"/>.
    /// </summary>
    public static ApproverResolution Resolved(
        ApprovalPolicyRef policy,
        EndpointRef approver,
        ApprovalPolicyVersion version)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(version);

        return new ApproverResolution(
            ApproverResolutionStatus.Resolved,
            policy,
            approver,
            version);
    }

    /// <summary>Creates an outcome for a policy that is not declared in the organization.</summary>
    public static ApproverResolution PolicyNotFound(ApprovalPolicyRef policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return new ApproverResolution(
            ApproverResolutionStatus.PolicyNotFound,
            policy,
            resolvedApprover: null,
            appliedVersion: null);
    }

    /// <summary>
    /// Creates an outcome for a policy that exists, with applied <paramref name="version"/>, but
    /// does not authorize the requested action.
    /// </summary>
    public static ApproverResolution ActionNotAuthorized(
        ApprovalPolicyRef policy,
        ApprovalPolicyVersion version)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(version);

        return new ApproverResolution(
            ApproverResolutionStatus.ActionNotAuthorized,
            policy,
            resolvedApprover: null,
            version);
    }
}
