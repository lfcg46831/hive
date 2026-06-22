using System.Collections.Immutable;
using Hive.Domain.Governance;

namespace Hive.Domain.Messaging;

/// <summary>
/// Validates governance routing for <see cref="ApprovalRequest"/> and <see cref="ApprovalDecision"/>
/// (US-F0-04-T07b/T07c). A request is checked against the authority that resolves its authorized
/// approver and against its own approval window; a decision is checked against correlation with the
/// original request, the original request's lifecycle state, the approval window and the recorded
/// approver's permission. A decision for an already-decided request is rejected as a duplicate
/// (US-F0-04-T07c).
/// </summary>
/// <remarks>
/// The validator never re-resolves the approver of a decision: it correlates the decision with the
/// <see cref="ApprovalRequestRecord"/> recorded when the request was accepted and compares against the
/// approver and window in force then. Confirmed semantic failures (policy/action not authorized,
/// wrong approver, missing correlation, duplicate decision, closed request, expired window) become
/// structured <see cref="ValidationResult"/> errors drawn from <see cref="ApprovalValidationCatalog"/>
/// so audit reuses the same stable codes; cancellation and technical unavailability remain exceptions
/// subject to retry.
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
            return ValidationResult.Create([ApprovalValidationCatalog.OrganizationNotFound()]);
        }

        switch (resolution.Status)
        {
            case ApproverResolutionStatus.PolicyNotFound:
                errors.Add(ApprovalValidationCatalog.ApprovalPolicyNotFound());
                break;
            case ApproverResolutionStatus.ActionNotAuthorized:
                errors.Add(ApprovalValidationCatalog.ActionNotAuthorized());
                break;
            case ApproverResolutionStatus.Resolved:
                if (resolution.ResolvedApprover != request.To)
                {
                    errors.Add(ApprovalValidationCatalog.AuthorizedApproverRequired());
                }

                break;
            default:
                throw new InvalidOperationException(
                    "Unexpected approver resolution status.");
        }

        if (IsExpired(request.Deadline))
        {
            errors.Add(ApprovalValidationCatalog.ApprovalRequestExpired());
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
            return ValidationResult.Create([ApprovalValidationCatalog.ApprovalRequestNotFound()]);
        }

        var errors = new List<ValidationError>();

        if (decision.Thread != record.Thread)
        {
            errors.Add(ApprovalValidationCatalog.ApprovalThreadMismatch());
        }

        if (decision.From != record.ResolvedApprover)
        {
            errors.Add(ApprovalValidationCatalog.UnauthorizedApprover());
        }

        if (decision.To is not PositionEndpointRef requester
            || requester.PositionId != record.Requester)
        {
            errors.Add(ApprovalValidationCatalog.OriginalRequesterRequired());
        }

        // A request that was already decided (Completed) rejects any further decision as a
        // duplicate; other terminal states were closed without a decision and are merely not open.
        if (record.IsDecided)
        {
            errors.Add(ApprovalValidationCatalog.ApprovalDecisionDuplicate());
        }
        else if (!record.IsAwaitingDecision)
        {
            errors.Add(ApprovalValidationCatalog.ApprovalRequestNotOpen());
        }

        if (IsExpired(record.Deadline))
        {
            errors.Add(ApprovalValidationCatalog.ApprovalDecisionExpired());
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

        errors.Add(ApprovalValidationCatalog.EndpointNotAllowed(path));
        return null;
    }
}
