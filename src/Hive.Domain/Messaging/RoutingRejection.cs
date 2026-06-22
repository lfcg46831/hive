using System.Collections.Immutable;

namespace Hive.Domain.Messaging;

/// <summary>
/// A rejected routing validation outcome (US-F0-04-T08): the detailed, structured
/// <see cref="ValidationResult"/> together with its auditable <see cref="RoutingValidationContext"/>,
/// plus a sanitized <see cref="PublicResult"/> safe to return to the sender. It reuses the
/// <see cref="ValidationResult"/>/<see cref="ValidationError"/> contract of §9.8 across vertical and
/// governance routing without exposing sensitive detail in the public surface.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AuditResult"/> keeps the canonical, fine-grained errors (stable
/// <see cref="ValidationError.Code"/> and dotted <see cref="ValidationError.Path"/>) drawn from
/// <see cref="RoutingValidationCatalog"/> and <see cref="ApprovalValidationCatalog"/>, alongside the
/// identifiers in <see cref="Context"/> — original message, sender, recipient, policy, applied
/// version/hash, resolved approver and thread. This detail is for the authorized audit trail only.
/// </para>
/// <para>
/// <see cref="PublicResult"/> normalizes every audit error to its coarse-grained
/// <see cref="RejectionReason"/>: the <see cref="ValidationError.Code"/> becomes the reason's wire
/// value and the <see cref="ValidationError.Path"/> becomes <c>$</c>. This prevents enumeration of
/// organizations, positions, policies and routes (§9.8) while preserving the coarse classification
/// the sender, routing and metrics legitimately need. Distinct reasons collapse to one public error
/// each, deterministically ordered by <see cref="ValidationResult"/>.
/// </para>
/// <para>
/// A <see cref="RoutingRejection"/> models a rejection: it is created only from an invalid
/// <see cref="ValidationResult"/>. A valid result is not a rejection and is reported through the
/// validators' <see cref="ValidationResult"/> directly.
/// </para>
/// </remarks>
public sealed record RoutingRejection
{
    private RoutingRejection(
        RoutingValidationContext context,
        ValidationResult auditResult,
        ValidationResult publicResult)
    {
        Context = context;
        AuditResult = auditResult;
        PublicResult = publicResult;
    }

    /// <summary>The auditable context of the rejected message.</summary>
    public RoutingValidationContext Context { get; }

    /// <summary>
    /// The detailed, fine-grained validation errors for the authorized audit trail. Always invalid.
    /// </summary>
    public ValidationResult AuditResult { get; }

    /// <summary>
    /// The sanitized result safe to return to the sender: one error per coarse-grained
    /// <see cref="RejectionReason"/>, with a generic <see cref="ValidationError.Code"/> and
    /// <see cref="ValidationError.Path"/> <c>$</c>.
    /// </summary>
    public ValidationResult PublicResult { get; }

    /// <summary>
    /// Creates a rejection from the auditable <paramref name="context"/> and the invalid
    /// <paramref name="auditResult"/>, deriving the sanitized public result.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="auditResult"/> is valid (no errors).</exception>
    public static RoutingRejection Create(
        RoutingValidationContext context,
        ValidationResult auditResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(auditResult);

        if (auditResult.IsValid)
        {
            throw new ArgumentException(
                "A routing rejection requires an invalid validation result.",
                nameof(auditResult));
        }

        return new RoutingRejection(context, auditResult, Sanitize(auditResult));
    }

    private static ValidationResult Sanitize(ValidationResult auditResult)
    {
        var sanitized = auditResult.Errors
            .Select(error => new ValidationError(
                RejectionReasonContract.ToWireValue(error.Reason),
                "$",
                error.Reason))
            .ToImmutableArray();

        return ValidationResult.Create(sanitized);
    }
}
