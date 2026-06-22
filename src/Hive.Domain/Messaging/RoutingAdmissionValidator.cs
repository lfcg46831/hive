namespace Hive.Domain.Messaging;

/// <summary>
/// Integrates vertical and governance routing validation at the position's inbox entry point
/// (US-F0-04-T09): before a message is accepted into the inbox it is dispatched, by concrete type, to
/// the matching routing validator and either admitted or rejected.
/// </summary>
/// <remarks>
/// <para>
/// The seam composes the focused validators built in US-F0-04-T04–T07 without re-implementing their
/// rules. A <see cref="Directive"/>, <see cref="Report"/> or <see cref="Escalation"/> is validated by
/// the vertical validators; an <see cref="ApprovalRequest"/> or <see cref="ApprovalDecision"/> by the
/// governance validator. A message type with no vertical or governance routing rule is outside this
/// gate and is admitted unchanged — horizontal and system routing gating belong to later stories.
/// </para>
/// <para>
/// On an invalid <see cref="ValidationResult"/> the admission carries a <see cref="RoutingRejection"/>
/// pairing the detailed audit result with the auditable <see cref="RoutingValidationContext"/> of the
/// original message and the sanitized public result. Cancellation and the validators' technical
/// failures (registry/authority/log unavailability) propagate as exceptions subject to retry; only
/// confirmed routing violations become rejections, in line with §9.8.
/// </para>
/// </remarks>
public sealed class RoutingAdmissionValidator
{
    private readonly DirectiveRoutingValidator _directive;
    private readonly ReportRoutingValidator _report;
    private readonly EscalationRoutingValidator _escalation;
    private readonly ApprovalRoutingValidator _approval;

    public RoutingAdmissionValidator(
        DirectiveRoutingValidator directive,
        ReportRoutingValidator report,
        EscalationRoutingValidator escalation,
        ApprovalRoutingValidator approval)
    {
        ArgumentNullException.ThrowIfNull(directive);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(escalation);
        ArgumentNullException.ThrowIfNull(approval);

        _directive = directive;
        _report = report;
        _escalation = escalation;
        _approval = approval;
    }

    /// <summary>
    /// Validates the routing of <paramref name="message"/> at the inbox entry point and returns
    /// whether it may be accepted. A failing validation yields a <see cref="RoutingAdmission"/> that
    /// carries the <see cref="RoutingRejection"/>; a message outside vertical/governance routing is
    /// admitted unchanged.
    /// </summary>
    public async ValueTask<RoutingAdmission> AdmitAsync(
        OrgMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var result = message switch
        {
            Directive directive => await _directive.ValidateAsync(directive, cancellationToken),
            Report report => await _report.ValidateAsync(report, cancellationToken),
            Escalation escalation => await _escalation.ValidateAsync(escalation, cancellationToken),
            ApprovalRequest request => await _approval.ValidateAsync(request, cancellationToken),
            ApprovalDecision decision => await _approval.ValidateAsync(decision, cancellationToken),
            _ => ValidationResult.Valid,
        };

        if (result.IsValid)
        {
            return RoutingAdmission.Admitted;
        }

        var context = RoutingValidationContext.ForMessage(message);
        return RoutingAdmission.Reject(RoutingRejection.Create(context, result));
    }
}
