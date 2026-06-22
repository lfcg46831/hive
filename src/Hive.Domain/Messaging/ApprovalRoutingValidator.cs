using System.Collections.Immutable;
using Hive.Domain.Governance;

namespace Hive.Domain.Messaging;

/// <summary>
/// Validates governance routing for <see cref="ApprovalRequest"/> and <see cref="ApprovalDecision"/>
/// (US-F0-04-T07b). A request is checked against the authority that resolves its authorized approver
/// and against its own approval window; a decision is checked against correlation with the original
/// request, the original request's lifecycle state, the approval window and the recorded approver's
/// permission.
/// </summary>
/// <remarks>
/// The validator never re-resolves the approver of a decision: it correlates the decision with the
/// <see cref="ApprovalRequestRecord"/> recorded when the request was accepted and compares against the
/// approver and window in force then. Confirmed semantic failures (policy/action not authorized,
/// wrong approver, missing correlation, closed request, expired window) become structured
/// <see cref="ValidationResult"/> errors; cancellation and technical unavailability remain
/// exceptions subject to retry.
/// </remarks>
public sealed class ApprovalRoutingValidator
{
    private static readonly MessageRoutingRule RequestRule =
        MessageRoutingRules.For<ApprovalRequest>();

    private static readonly MessageRoutingRule DecisionRule =
        MessageRoutingRules.For<ApprovalDecision>();

    private static readonly ImmutableHashSet<Type> RequestFromTypes =
        RequestRule.Paths.Select(path => path.FromEndpointType).ToImmutableHashSet();

    private static readonly ImmutableHashSet<Type> RequestToTypes =
        RequestRule.Paths.Select(path => path.ToEndpointType).ToImmutableHashSet();

    private static readonly ImmutableHashSet<Type> DecisionFromTypes =
        DecisionRule.Paths.Select(path => path.FromEndpointType).ToImmutableHashSet();

    private static readonly ImmutableHashSet<Type> DecisionToTypes =
        DecisionRule.Paths.Select(path => path.ToEndpointType).ToImmutableHashSet();

    private readonly IApprovalAuthority _authority;
    private readonly IApprovalRequestLog _requestLog;
    private readonly TimeProvider _timeProvider;

    public ApprovalRoutingValidator(
        IApprovalAuthority authority,
        IApprovalRequestLog requestLog,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(requestLog);

        _authority = authority;
        _requestLog = requestLog;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ValidationResult> ValidateAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var shapeErrors = new List<ValidationError>();
        var from = RequireEndpoint(request.From, RequestFromTypes, "from", shapeErrors);
        _ = RequireEndpoint(request.To, RequestToTypes, "to", shapeErrors);

        if (shapeErrors.Count != 0)
        {
            return ValidationResult.Create(shapeErrors);
        }

        var errors = new List<ValidationError>();
        ApproverResolution resolution;
        try
        {
            resolution = await _authority.ResolveApproverAsync(
                new ApprovalAuthorityQuery(
                    request.OrganizationId,
                    request.Policy,
                    ((PositionEndpointRef)from!).PositionId,
                    request.To,
                    request.Action),
                cancellationToken);
        }
        catch (ApprovalAuthorityNotFoundException)
        {
            return ValidationResult.Create([OrganizationNotFound()]);
        }

        switch (resolution.Status)
        {
            case ApproverResolutionStatus.PolicyNotFound:
                errors.Add(ApprovalPolicyNotFound());
                break;
            case ApproverResolutionStatus.ActionNotAuthorized:
                errors.Add(ActionNotAuthorized());
                break;
            case ApproverResolutionStatus.Resolved:
                if (resolution.ResolvedApprover != request.To)
                {
                    errors.Add(AuthorizedApproverRequired());
                }

                break;
            default:
                throw new InvalidOperationException(
                    "Unexpected approver resolution status.");
        }

        if (IsExpired(request.Deadline))
        {
            errors.Add(RequestExpired());
        }

        return ValidationResult.Create(errors);
    }

    public async ValueTask<ValidationResult> ValidateAsync(
        ApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();

        var shapeErrors = new List<ValidationError>();
        _ = RequireEndpoint(decision.From, DecisionFromTypes, "from", shapeErrors);
        _ = RequireEndpoint(decision.To, DecisionToTypes, "to", shapeErrors);

        if (shapeErrors.Count != 0)
        {
            return ValidationResult.Create(shapeErrors);
        }

        var record = await _requestLog.FindRequestAsync(
            decision.OrganizationId,
            decision.RequestId,
            cancellationToken);

        if (record is null)
        {
            return ValidationResult.Create([RequestNotFound()]);
        }

        var errors = new List<ValidationError>();

        if (decision.Thread != record.Thread)
        {
            errors.Add(ThreadMismatch());
        }

        if (decision.From != record.ResolvedApprover)
        {
            errors.Add(UnauthorizedApprover());
        }

        if (decision.To is not PositionEndpointRef requester
            || requester.PositionId != record.Requester)
        {
            errors.Add(OriginalRequesterRequired());
        }

        if (!record.IsAwaitingDecision)
        {
            errors.Add(RequestNotOpen());
        }

        if (IsExpired(record.Deadline))
        {
            errors.Add(DecisionExpired());
        }

        return ValidationResult.Create(errors);
    }

    private bool IsExpired(DateTimeOffset? deadline) =>
        deadline is { } value && value <= _timeProvider.GetUtcNow();

    private static EndpointRef? RequireEndpoint(
        EndpointRef endpoint,
        ImmutableHashSet<Type> allowedTypes,
        string path,
        ICollection<ValidationError> errors)
    {
        if (allowedTypes.Contains(endpoint.GetType()))
        {
            return endpoint;
        }

        errors.Add(new ValidationError(
            "endpoint-not-allowed",
            path,
            RejectionReason.InvalidRoute));
        return null;
    }

    private static ValidationError OrganizationNotFound() =>
        new("organization-not-found", "organizationId", RejectionReason.InvalidRoute);

    private static ValidationError ApprovalPolicyNotFound() =>
        new("approval-policy-not-found", "policy", RejectionReason.InvalidRoute);

    private static ValidationError ActionNotAuthorized() =>
        new("action-not-authorized", "action", RejectionReason.InvalidRoute);

    private static ValidationError AuthorizedApproverRequired() =>
        new("authorized-approver-required", "to", RejectionReason.InvalidRoute);

    private static ValidationError RequestExpired() =>
        new("approval-request-expired", "deadline", RejectionReason.Expired);

    private static ValidationError RequestNotFound() =>
        new("approval-request-not-found", "requestId", RejectionReason.InvalidRoute);

    private static ValidationError ThreadMismatch() =>
        new("approval-thread-mismatch", "thread", RejectionReason.InvalidRoute);

    private static ValidationError UnauthorizedApprover() =>
        new("unauthorized-approver", "from", RejectionReason.Unauthorized);

    private static ValidationError OriginalRequesterRequired() =>
        new("original-requester-required", "to", RejectionReason.InvalidRoute);

    private static ValidationError RequestNotOpen() =>
        new("approval-request-not-open", "requestId", RejectionReason.InvalidRoute);

    private static ValidationError DecisionExpired() =>
        new("approval-decision-expired", "requestId", RejectionReason.Expired);
}
