using System.Collections.Immutable;
using Hive.Domain.Governance;
using Hive.Domain.Identity;

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

        return ValidateDecision(
            decision,
            record.Requester,
            record.ResolvedApprover,
            record.Thread,
            record.Deadline,
            record.State);
    }

    /// <summary>
    /// Validates a decision against the canonical approval request retained by the deciding
    /// position. The request destination is the approver resolved and admitted when the request was
    /// emitted; it is deliberately not re-resolved against a later policy revision.
    /// </summary>
    public ValueTask<ValidationResult> ValidateAsync(
        ApprovalDecision decision,
        ApprovalRequest request,
        MessageState requestState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(request);
        MessageStateContract.RequireDefined(requestState, nameof(requestState));
        cancellationToken.ThrowIfCancellationRequested();

        var shapeErrors = new List<ValidationError>();
        _ = RequireEndpoint(decision.From, DecisionFromTypes, "from", shapeErrors);
        _ = RequireEndpoint(decision.To, DecisionToTypes, "to", shapeErrors);
        if (shapeErrors.Count != 0)
        {
            return ValueTask.FromResult(ValidationResult.Create(shapeErrors));
        }

        if (request.OrganizationId != decision.OrganizationId || request.Id != decision.RequestId)
        {
            return ValueTask.FromResult(ValidationResult.Create(
                [ApprovalValidationCatalog.ApprovalRequestNotFound()]));
        }

        if (request.From is not PositionEndpointRef requester)
        {
            return ValueTask.FromResult(ValidationResult.Create(
                [ApprovalValidationCatalog.OriginalRequesterRequired()]));
        }

        return ValueTask.FromResult(ValidateDecision(
            decision,
            requester.PositionId,
            request.To,
            request.Thread,
            request.Deadline,
            requestState));
    }

    private bool IsExpired(DateTimeOffset? deadline) =>
        deadline is { } value && value <= _timeProvider.GetUtcNow();

    private ValidationResult ValidateDecision(
        ApprovalDecision decision,
        PositionId requester,
        EndpointRef resolvedApprover,
        ThreadId thread,
        DateTimeOffset? deadline,
        MessageState requestState)
    {
        var errors = new List<ValidationError>();

        if (decision.Thread != thread)
        {
            errors.Add(ApprovalValidationCatalog.ApprovalThreadMismatch());
        }

        if (decision.From != resolvedApprover)
        {
            errors.Add(ApprovalValidationCatalog.UnauthorizedApprover());
        }

        if (decision.To is not PositionEndpointRef decisionRequester
            || decisionRequester.PositionId != requester)
        {
            errors.Add(ApprovalValidationCatalog.OriginalRequesterRequired());
        }

        if (requestState is MessageState.Completed)
        {
            errors.Add(ApprovalValidationCatalog.ApprovalDecisionDuplicate());
        }
        else if (requestState is not (
                     MessageState.Received or MessageState.Accepted or MessageState.Processing))
        {
            errors.Add(ApprovalValidationCatalog.ApprovalRequestNotOpen());
        }

        if (IsExpired(deadline))
        {
            errors.Add(ApprovalValidationCatalog.ApprovalDecisionExpired());
        }

        return ValidationResult.Create(errors);
    }

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
