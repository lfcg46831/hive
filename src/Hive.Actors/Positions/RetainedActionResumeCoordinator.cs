using Hive.Domain.Auditing;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

internal interface IRetainedActionPolicyEvaluator
{
    ValueTask<ActionGateOutcome> EvaluateAsync(
        PersistedRetainedAction action,
        PositionRuntimeConfiguration runtimeConfiguration,
        CancellationToken cancellationToken = default);
}

internal interface IRetainedActionExecutor
{
    ValueTask<RetainedActionExecutionResult> ExecuteAsync(
        RetainedActionExecutionRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed record RetainedActionExecutionRequest
{
    public RetainedActionExecutionRequest(PersistedRetainedAction action, MessageId grantId)
    {
        Action = action ?? throw new ArgumentNullException(nameof(action));
        GrantId = grantId ?? throw new ArgumentNullException(nameof(grantId));
        IdempotencyKey = $"retained-action:{action.Id}:grant:{grantId}";
    }

    public PersistedRetainedAction Action { get; }
    public MessageId GrantId { get; }
    public string IdempotencyKey { get; }
}

internal sealed record RetainedActionExecutionResult
{
    private RetainedActionExecutionResult(bool succeeded, string? failureCode)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
    }

    public bool Succeeded { get; }
    public string? FailureCode { get; }

    public static RetainedActionExecutionResult Success() => new(true, null);

    public static RetainedActionExecutionResult Failed(string failureCode) =>
        new(false, NormalizeCode(failureCode));

    private static string NormalizeCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Trim() != code
            || code.Length > 100
            || code.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException(
                "Failure code must contain only ASCII letters, digits, or hyphens.",
                nameof(code));
        }

        return code;
    }
}

internal enum RetainedActionResumeOutcome
{
    Noop = 1,
    Consumed = 2,
    Expired = 3,
    Returned = 4,
    Retry = 5,
}

internal sealed record RetainedActionResumeResult(
    RetainedActionResumeOutcome Outcome,
    MessageId? GrantId,
    string Code)
{
    public bool RequiresTransition =>
        Outcome is RetainedActionResumeOutcome.Consumed
            or RetainedActionResumeOutcome.Expired
            or RetainedActionResumeOutcome.Returned;
}

internal sealed class RetainedActionResumeCoordinator
{
    internal const string NotAuthorizedCode = "retained-action-not-authorized";
    internal const string FingerprintMismatchCode = "retained-action-fingerprint-mismatch";
    internal const string AuthorizationExpiredCode = "retained-action-authorization-expired";
    internal const string HumanApprovalRequiredCode = "retained-action-human-approval-required";
    internal const string PolicyFailureCode = "retained-action-policy-evaluation-failed";
    internal const string ExecutionFailureCode = "retained-action-execution-failed";
    internal const string ConsumedCode = "retained-action-consumed";

    private readonly IRetainedActionPolicyEvaluator _policyEvaluator;
    private readonly IRetainedActionExecutor _executor;
    private readonly IJourneyAuditLog _auditLog;
    private readonly TimeProvider _timeProvider;

    public RetainedActionResumeCoordinator(
        IRetainedActionPolicyEvaluator policyEvaluator,
        IRetainedActionExecutor executor,
        IJourneyAuditLog auditLog,
        TimeProvider? timeProvider = null)
    {
        _policyEvaluator = policyEvaluator ?? throw new ArgumentNullException(nameof(policyEvaluator));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<RetainedActionResumeResult> ResumeAsync(
        PersistedRetainedAction action,
        PositionRuntimeConfiguration runtimeConfiguration,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(runtimeConfiguration);
        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("Resume attempt id cannot be empty.", nameof(attemptId));
        }

        var grant = action.ActiveGrant;
        if (grant is null)
        {
            return Audit(action, attemptId, new(
                RetainedActionResumeOutcome.Noop, null, NotAuthorizedCode));
        }

        if (grant.Fingerprint != action.Fingerprint)
        {
            return Audit(action, attemptId, new(
                RetainedActionResumeOutcome.Returned, grant.Id, FingerprintMismatchCode));
        }

        if (grant.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            return Audit(action, attemptId, new(
                RetainedActionResumeOutcome.Expired, grant.Id, AuthorizationExpiredCode));
        }

        ActionGateOutcome policy;
        try
        {
            policy = await _policyEvaluator
                .EvaluateAsync(action, runtimeConfiguration, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Audit(action, attemptId, new(
                RetainedActionResumeOutcome.Retry, grant.Id, PolicyFailureCode));
        }

        if (policy == ActionGateOutcome.HumanApprovalRequired)
        {
            return Audit(action, attemptId, new(
                RetainedActionResumeOutcome.Returned, grant.Id, HumanApprovalRequiredCode));
        }

        if (policy is not (ActionGateOutcome.Allowed or ActionGateOutcome.EscalationRequired))
        {
            return Audit(action, attemptId, new(
                RetainedActionResumeOutcome.Retry, grant.Id, PolicyFailureCode));
        }

        RetainedActionExecutionResult execution;
        try
        {
            execution = await _executor
                .ExecuteAsync(new RetainedActionExecutionRequest(action, grant.Id), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            execution = RetainedActionExecutionResult.Failed(ExecutionFailureCode);
        }

        if (execution is null || !execution.Succeeded)
        {
            return Audit(action, attemptId, new(
                RetainedActionResumeOutcome.Retry,
                grant.Id,
                execution?.FailureCode ?? ExecutionFailureCode));
        }

        return Audit(action, attemptId, new(
            RetainedActionResumeOutcome.Consumed, grant.Id, ConsumedCode));
    }

    private RetainedActionResumeResult Audit(
        PersistedRetainedAction action,
        Guid attemptId,
        RetainedActionResumeResult result)
    {
        _auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.RetainedActionResume,
            result.Outcome == RetainedActionResumeOutcome.Consumed
                ? JourneyAuditOutcome.Succeeded
                : result.Outcome == RetainedActionResumeOutcome.Retry
                    ? JourneyAuditOutcome.Failed
                    : JourneyAuditOutcome.Rejected,
            action.OrganizationId,
            action.ThreadId,
            action.SourceMessageId,
            action.DirectiveId,
            action.PositionId,
            result.Code,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["retainedActionId"] = action.Id.ToString(),
                ["grantId"] = result.GrantId?.ToString() ?? "none",
                ["authorityKey"] = action.ActiveGrant?.Key.Value ?? "none",
                ["approvalPolicyRefs"] = string.Join(",", action.ApprovalPolicies.Select(item => item.Value)),
                ["outcome"] = result.Outcome.ToString(),
                ["redactions"] = "canonicalPayload,canonicalFacts,governanceMessages",
            },
            occurredAtUtc: _timeProvider.GetUtcNow(),
            idempotencyDiscriminator: attemptId.ToString("N")));

        return result;
    }
}

internal sealed class EscalatingRetainedActionPolicyEvaluator : IRetainedActionPolicyEvaluator
{
    public static EscalatingRetainedActionPolicyEvaluator Instance { get; } = new();

    private EscalatingRetainedActionPolicyEvaluator()
    {
    }

    public ValueTask<ActionGateOutcome> EvaluateAsync(
        PersistedRetainedAction action,
        PositionRuntimeConfiguration runtimeConfiguration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(runtimeConfiguration);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ActionGateOutcome.EscalationRequired);
    }
}

internal sealed class UnavailableRetainedActionExecutor : IRetainedActionExecutor
{
    public static UnavailableRetainedActionExecutor Instance { get; } = new();

    private UnavailableRetainedActionExecutor()
    {
    }

    public ValueTask<RetainedActionExecutionResult> ExecuteAsync(
        RetainedActionExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(RetainedActionExecutionResult.Failed(
            "retained-action-executor-unavailable"));
    }
}
