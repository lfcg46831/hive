namespace Hive.Domain.Messaging;

/// <summary>
/// Canonical catalog of the structured rejection reasons produced when validating vertical routing
/// for <see cref="Directive"/>, <see cref="Report"/> and <see cref="Escalation"/>
/// (US-F0-04-T04/T05/T06), consolidated by US-F0-04-T08. The routing validators and the audit trail
/// consume this single source, so a given stable, machine-readable <see cref="ValidationError.Code"/>
/// always carries the same canonical <see cref="ValidationError.Path"/> and coarse-grained
/// <see cref="RejectionReason"/>.
/// </summary>
/// <remarks>
/// Each factory returns a fresh <see cref="ValidationError"/> with the contract defined in §9.8 of the
/// bible, including the canonical mapping of confirmed missing organizations/positions to
/// <see cref="RejectionReason.InvalidRoute"/>. The nested <see cref="Codes"/> constants let audit
/// match on a rejection without re-declaring literals, keeping payloads, metrics and the audit trail
/// aligned with the validators. Governance routing (<see cref="ApprovalRequest"/>/
/// <see cref="ApprovalDecision"/>) keeps its own already-consolidated catalog
/// (<see cref="ApprovalValidationCatalog"/>, US-F0-04-T07c); both share the same §9.8 canonical
/// not-found contract.
/// </remarks>
public static class RoutingValidationCatalog
{
    /// <summary>
    /// Stable, machine-readable error codes (lowercase/kebab-case) shared by the vertical routing
    /// validators and the audit trail. They distinguish specific violations within a single
    /// <see cref="RejectionReason"/>.
    /// </summary>
    public static class Codes
    {
        public const string EndpointNotAllowed = "endpoint-not-allowed";
        public const string OrganizationNotFound = "organization-not-found";
        public const string PositionNotFound = "position-not-found";
        public const string DirectSubordinateRequired = "direct-subordinate-required";
        public const string DirectSuperiorRequired = "direct-superior-required";
        public const string RootLeadershipRequired = "root-leadership-required";
    }

    /// <summary>The endpoint variant at <paramref name="path"/> is not allowed for the message type.</summary>
    public static ValidationError EndpointNotAllowed(string path) =>
        new(Codes.EndpointNotAllowed, path, RejectionReason.InvalidRoute);

    /// <summary>A successful query confirmed the organization does not exist.</summary>
    public static ValidationError OrganizationNotFound() =>
        new(Codes.OrganizationNotFound, "organizationId", RejectionReason.InvalidRoute);

    /// <summary>
    /// A successful query confirmed the position at <paramref name="path"/>
    /// (<c>from.positionId</c> or <c>to.positionId</c>) does not exist.
    /// </summary>
    public static ValidationError PositionNotFound(string path) =>
        new(Codes.PositionNotFound, path, RejectionReason.InvalidRoute);

    /// <summary>The directive's destination is not a direct subordinate of its source.</summary>
    public static ValidationError DirectSubordinateRequired() =>
        new(Codes.DirectSubordinateRequired, "to.positionId", RejectionReason.InvalidRoute);

    /// <summary>The report/escalation's destination is not the direct superior of its source.</summary>
    public static ValidationError DirectSuperiorRequired() =>
        new(Codes.DirectSuperiorRequired, "to.positionId", RejectionReason.InvalidRoute);

    /// <summary>The escalation-to-owner source is not the root unit leadership.</summary>
    public static ValidationError RootLeadershipRequired() =>
        new(Codes.RootLeadershipRequired, "from.positionId", RejectionReason.InvalidRoute);
}
