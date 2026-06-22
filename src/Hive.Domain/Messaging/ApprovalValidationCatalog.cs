namespace Hive.Domain.Messaging;

/// <summary>
/// Canonical catalog of the structured rejection reasons produced when validating governance
/// routing for <see cref="ApprovalRequest"/> and <see cref="ApprovalDecision"/>
/// (US-F0-04-T07a/T07b/T07c). The routing validator and the audit trail consume this single source,
/// so a given stable, machine-readable <see cref="ValidationError.Code"/> always carries the same
/// canonical <see cref="ValidationError.Path"/> and coarse-grained <see cref="RejectionReason"/>.
/// </summary>
/// <remarks>
/// Each factory returns a fresh <see cref="ValidationError"/> with the contract defined in §9.8 of
/// the bible. The nested <see cref="Codes"/> constants let audit match on a rejection without
/// re-declaring literals, keeping payloads, metrics and the audit trail aligned with the validator.
/// </remarks>
public static class ApprovalValidationCatalog
{
    /// <summary>
    /// Stable, machine-readable error codes (lowercase/kebab-case) shared by the validator and the
    /// audit trail. They distinguish specific violations within a single <see cref="RejectionReason"/>.
    /// </summary>
    public static class Codes
    {
        public const string EndpointNotAllowed = "endpoint-not-allowed";
        public const string OrganizationNotFound = "organization-not-found";
        public const string ApprovalPolicyNotFound = "approval-policy-not-found";
        public const string ActionNotAuthorized = "action-not-authorized";
        public const string AuthorizedApproverRequired = "authorized-approver-required";
        public const string ApprovalRequestExpired = "approval-request-expired";
        public const string ApprovalRequestNotFound = "approval-request-not-found";
        public const string ApprovalThreadMismatch = "approval-thread-mismatch";
        public const string UnauthorizedApprover = "unauthorized-approver";
        public const string OriginalRequesterRequired = "original-requester-required";
        public const string ApprovalRequestNotOpen = "approval-request-not-open";
        public const string ApprovalDecisionExpired = "approval-decision-expired";
        public const string ApprovalDecisionDuplicate = "approval-decision-duplicate";
    }

    /// <summary>The endpoint variant at <paramref name="path"/> is not allowed for the message type.</summary>
    public static ValidationError EndpointNotAllowed(string path) =>
        new(Codes.EndpointNotAllowed, path, RejectionReason.InvalidRoute);

    /// <summary>A successful query confirmed the organization does not exist.</summary>
    public static ValidationError OrganizationNotFound() =>
        new(Codes.OrganizationNotFound, "organizationId", RejectionReason.InvalidRoute);

    /// <summary>The approval policy referenced by the request is not declared.</summary>
    public static ValidationError ApprovalPolicyNotFound() =>
        new(Codes.ApprovalPolicyNotFound, "policy", RejectionReason.InvalidRoute);

    /// <summary>The declared policy does not authorize the requested action.</summary>
    public static ValidationError ActionNotAuthorized() =>
        new(Codes.ActionNotAuthorized, "action", RejectionReason.InvalidRoute);

    /// <summary>The request target is not the approver resolved by the authority.</summary>
    public static ValidationError AuthorizedApproverRequired() =>
        new(Codes.AuthorizedApproverRequired, "to", RejectionReason.InvalidRoute);

    /// <summary>The request's own approval window has already expired.</summary>
    public static ValidationError ApprovalRequestExpired() =>
        new(Codes.ApprovalRequestExpired, "deadline", RejectionReason.Expired);

    /// <summary>No original request is recorded for the decision's correlation identifier.</summary>
    public static ValidationError ApprovalRequestNotFound() =>
        new(Codes.ApprovalRequestNotFound, "requestId", RejectionReason.InvalidRoute);

    /// <summary>The decision's thread does not match the recorded request's thread.</summary>
    public static ValidationError ApprovalThreadMismatch() =>
        new(Codes.ApprovalThreadMismatch, "thread", RejectionReason.InvalidRoute);

    /// <summary>The decision did not originate from the approver recorded at acceptance.</summary>
    public static ValidationError UnauthorizedApprover() =>
        new(Codes.UnauthorizedApprover, "from", RejectionReason.Unauthorized);

    /// <summary>The decision is not addressed to the original requester.</summary>
    public static ValidationError OriginalRequesterRequired() =>
        new(Codes.OriginalRequesterRequired, "to", RejectionReason.InvalidRoute);

    /// <summary>
    /// The original request is in a terminal state that was closed without a recorded decision
    /// (admission refusal or processing failure), so it no longer accepts one.
    /// </summary>
    public static ValidationError ApprovalRequestNotOpen() =>
        new(Codes.ApprovalRequestNotOpen, "requestId", RejectionReason.InvalidRoute);

    /// <summary>The decision arrived after the recorded request's approval window closed.</summary>
    public static ValidationError ApprovalDecisionExpired() =>
        new(Codes.ApprovalDecisionExpired, "requestId", RejectionReason.Expired);

    /// <summary>
    /// A further decision arrived for a request that was already decided
    /// (<see cref="MessageState.Completed"/>); the duplicate does not create a second lifecycle
    /// (US-F0-04-T07c).
    /// </summary>
    public static ValidationError ApprovalDecisionDuplicate() =>
        new(Codes.ApprovalDecisionDuplicate, "requestId", RejectionReason.Duplicate);
}
