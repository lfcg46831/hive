namespace Hive.Domain.Messaging;

/// <summary>
/// The outcome of validating an incoming <see cref="OrgMessage"/> at the position's inbox entry point
/// (US-F0-04-T09): the message is either admitted to the inbox or rejected before acceptance.
/// </summary>
/// <remarks>
/// <para>
/// A rejected admission carries the <see cref="RoutingRejection"/> built from the failing
/// <see cref="ValidationResult"/> and the auditable <see cref="RoutingValidationContext"/> of the
/// original message, so the entry point can return the sanitized public result to the sender while the
/// detailed audit result feeds the routing audit event (US-F0-04-T10). An admitted message has no
/// rejection.
/// </para>
/// <para>
/// Messages whose type has no vertical or governance routing rule in <see cref="MessageRoutingRules"/>
/// are not gated by this seam and are admitted unchanged; horizontal and system routing gating is the
/// responsibility of later stories.
/// </para>
/// </remarks>
public sealed record RoutingAdmission
{
    private RoutingAdmission(RoutingRejection? rejection) => Rejection = rejection;

    /// <summary>The shared admitted outcome, carrying no rejection.</summary>
    public static RoutingAdmission Admitted { get; } = new((RoutingRejection?)null);

    /// <summary>
    /// The rejection detail when the message was not admitted; otherwise <see langword="null"/>.
    /// </summary>
    public RoutingRejection? Rejection { get; }

    /// <summary>Whether the message may be accepted into the inbox.</summary>
    public bool IsAdmitted => Rejection is null;

    /// <summary>
    /// Builds a rejected admission from the given <paramref name="rejection"/>.
    /// </summary>
    public static RoutingAdmission Reject(RoutingRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        return new RoutingAdmission(rejection);
    }
}
