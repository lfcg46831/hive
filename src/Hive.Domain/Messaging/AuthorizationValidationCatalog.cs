namespace Hive.Domain.Messaging;

/// <summary>Canonical routing rejection errors for authorization responses (US-F0-12-T04).</summary>
public static class AuthorizationValidationCatalog
{
    public static class Codes
    {
        public const string EndpointNotAllowed = "endpoint-not-allowed";
        public const string EscalationNotFound = "authorization-escalation-not-found";
        public const string OrganizationMismatch = "authorization-organization-mismatch";
        public const string ThreadMismatch = "authorization-thread-mismatch";
        public const string RetainedActionMismatch = "authorization-retained-action-mismatch";
        public const string UnauthorizedIssuer = "authorization-issuer-unauthorized";
        public const string OriginalRequesterRequired = "authorization-original-requester-required";
        public const string ResponseDuplicate = "authorization-response-duplicate";
        public const string GrantExpired = "authorization-grant-expired";
    }

    public static ValidationError EndpointNotAllowed(string path) =>
        new(Codes.EndpointNotAllowed, path, RejectionReason.InvalidRoute);

    public static ValidationError EscalationNotFound() =>
        new(Codes.EscalationNotFound, "inReplyTo", RejectionReason.InvalidRoute);

    public static ValidationError OrganizationMismatch() =>
        new(Codes.OrganizationMismatch, "organizationId", RejectionReason.InvalidRoute);

    public static ValidationError ThreadMismatch() =>
        new(Codes.ThreadMismatch, "thread", RejectionReason.InvalidRoute);

    public static ValidationError RetainedActionMismatch() =>
        new(Codes.RetainedActionMismatch, "retainedActionId", RejectionReason.InvalidRoute);

    public static ValidationError UnauthorizedIssuer() =>
        new(Codes.UnauthorizedIssuer, "from", RejectionReason.Unauthorized);

    public static ValidationError OriginalRequesterRequired() =>
        new(Codes.OriginalRequesterRequired, "to", RejectionReason.InvalidRoute);

    public static ValidationError ResponseDuplicate() =>
        new(Codes.ResponseDuplicate, "inReplyTo", RejectionReason.Duplicate);

    public static ValidationError GrantExpired() =>
        new(Codes.GrantExpired, "expiresAt", RejectionReason.Expired);
}
