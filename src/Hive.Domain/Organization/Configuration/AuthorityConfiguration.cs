namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// The decision authority of a position (§6.2 <c>occupant.authority</c>): the action labels the
/// occupant <see cref="CanDecide"/> autonomously, those it <see cref="MustEscalate"/>, and those
/// that <see cref="RequiresHumanApproval"/>. Lists default to empty when absent; the labels are
/// opaque to the loaded model and their controlled vocabulary is enforced elsewhere (§4.4).
/// </summary>
public sealed record AuthorityConfiguration
{
    /// <summary>Creates an authority block from the three optional action-label lists.</summary>
    public AuthorityConfiguration(
        IReadOnlyList<string>? canDecide = null,
        IReadOnlyList<string>? mustEscalate = null,
        IReadOnlyList<string>? requiresHumanApproval = null)
    {
        CanDecide = canDecide ?? Array.Empty<string>();
        MustEscalate = mustEscalate ?? Array.Empty<string>();
        RequiresHumanApproval = requiresHumanApproval ?? Array.Empty<string>();
    }

    /// <summary>Action labels the occupant may decide autonomously.</summary>
    public IReadOnlyList<string> CanDecide { get; }

    /// <summary>Action labels the occupant must escalate to its superior.</summary>
    public IReadOnlyList<string> MustEscalate { get; }

    /// <summary>Action labels that require human approval before taking effect.</summary>
    public IReadOnlyList<string> RequiresHumanApproval { get; }
}
