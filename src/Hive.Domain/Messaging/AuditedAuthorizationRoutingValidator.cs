using Hive.Domain.Auditing;

namespace Hive.Domain.Messaging;

/// <summary>
/// Adds durable, redacted admission audit around the pure authorization routing validator.
/// Technical failures and cancellation deliberately remain retryable and are not audited as
/// semantic rejections (US-F0-12-T08).
/// </summary>
public sealed class AuditedAuthorizationRoutingValidator
{
    public const string AcceptedCode = "authorization-resolution-accepted";
    public const string RejectedCode = "authorization-resolution-rejected";

    private readonly AuthorizationRoutingValidator _validator;
    private readonly IJourneyAuditLog _auditLog;
    private readonly TimeProvider _timeProvider;

    public AuditedAuthorizationRoutingValidator(
        AuthorizationRoutingValidator validator,
        IJourneyAuditLog auditLog,
        TimeProvider? timeProvider = null)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ValidationResult> ValidateAsync(
        AuthorizationGrant grant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var result = await _validator.ValidateAsync(grant, cancellationToken).ConfigureAwait(false);
        Audit(grant, result, grant.Key.Value);
        return result;
    }

    public async ValueTask<ValidationResult> ValidateAsync(
        AuthorizationDenial denial,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(denial);
        var result = await _validator.ValidateAsync(denial, cancellationToken).ConfigureAwait(false);
        Audit(denial, result, authorityKey: null);
        return result;
    }

    private void Audit(OrgMessage resolution, ValidationResult result, string? authorityKey)
    {
        var validationCodes = string.Join(
            ",",
            result.Errors.Select(error => error.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["retainedActionId"] = resolution switch
            {
                AuthorizationGrant grant => grant.RetainedActionId.ToString(),
                AuthorizationDenial denial => denial.RetainedActionId.ToString(),
                _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
            },
            ["resolutionType"] = resolution.GetType().Name,
            ["resolutionMessageId"] = resolution.Id.ToString(),
            ["inReplyTo"] = resolution switch
            {
                AuthorizationGrant grant => grant.InReplyTo.ToString(),
                AuthorizationDenial denial => denial.InReplyTo.ToString(),
                _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
            },
            ["validationCodes"] = validationCodes,
            ["redactions"] = "reason,fingerprint,message.payload",
        };
        if (authorityKey is not null)
        {
            payload["authorityKey"] = authorityKey;
        }

        _auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.AuthorizationResolution,
            result.IsValid ? JourneyAuditOutcome.Accepted : JourneyAuditOutcome.Rejected,
            resolution.OrganizationId,
            resolution.Thread,
            resolution.Id,
            positionId: (resolution.To as PositionEndpointRef)?.PositionId,
            reasonCode: result.IsValid ? AcceptedCode : RejectedCode,
            messageType: resolution.GetType().Name,
            payload: payload,
            occurredAtUtc: _timeProvider.GetUtcNow(),
            idempotencyDiscriminator: $"{resolution.GetType().Name}:{validationCodes}"));
    }
}
