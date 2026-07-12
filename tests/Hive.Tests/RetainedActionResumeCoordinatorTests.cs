using Hive.Actors.Positions;
using Hive.Domain.Auditing;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class RetainedActionResumeCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Fingerprint_mismatch_returns_before_policy_or_execution()
    {
        var policy = new RecordingPolicy(ActionGateOutcome.Allowed);
        var executor = new RecordingExecutor(RetainedActionExecutionResult.Success());
        var audit = new RecordingAuditLog();
        var action = AuthorizedAction(
            ActionFingerprint.From("sha256:00000000000000000000000000000000000000000000000000000000000000aa"));
        var coordinator = Coordinator(policy, executor, audit);

        var result = await coordinator.ResumeAsync(action, Runtime(action), Guid.NewGuid());

        Assert.Equal(RetainedActionResumeOutcome.Returned, result.Outcome);
        Assert.Equal(RetainedActionResumeCoordinator.FingerprintMismatchCode, result.Code);
        Assert.Equal(0, policy.Calls);
        Assert.Empty(executor.Requests);
        AssertAuditIsRedacted(audit.Single());
    }

    [Theory]
    [InlineData(-1, (int)RetainedActionResumeOutcome.Expired, 0)]
    [InlineData(0, (int)RetainedActionResumeOutcome.Expired, 0)]
    [InlineData(1, (int)RetainedActionResumeOutcome.Consumed, 1)]
    public async Task Expiration_boundary_is_inclusive(
        int secondsFromNow,
        int expectedValue,
        int expectedExecutions)
    {
        var action = AuthorizedAction(expiresAt: Now.AddSeconds(secondsFromNow));
        var policy = new RecordingPolicy(ActionGateOutcome.EscalationRequired);
        var executor = new RecordingExecutor(RetainedActionExecutionResult.Success());

        var result = await Coordinator(policy, executor, new RecordingAuditLog())
            .ResumeAsync(action, Runtime(action), Guid.NewGuid());

        Assert.Equal((RetainedActionResumeOutcome)expectedValue, result.Outcome);
        Assert.Equal(expectedExecutions, executor.Requests.Count);
        Assert.Equal(expectedExecutions, policy.Calls);
    }

    [Fact]
    public async Task Human_approval_never_executes_under_a_grant()
    {
        var action = AuthorizedAction();
        var executor = new RecordingExecutor(RetainedActionExecutionResult.Success());

        var result = await Coordinator(
                new RecordingPolicy(ActionGateOutcome.HumanApprovalRequired),
                executor,
                new RecordingAuditLog())
            .ResumeAsync(action, Runtime(action), Guid.NewGuid());

        Assert.Equal(RetainedActionResumeOutcome.Returned, result.Outcome);
        Assert.Equal(RetainedActionResumeCoordinator.HumanApprovalRequiredCode, result.Code);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Technical_failure_keeps_authorization_retryable_with_the_same_effect_key()
    {
        var action = AuthorizedAction();
        var executor = new RecordingExecutor(
            RetainedActionExecutionResult.Failed("connector-unavailable"),
            RetainedActionExecutionResult.Success());
        var coordinator = Coordinator(
            new RecordingPolicy(ActionGateOutcome.Allowed),
            executor,
            new RecordingAuditLog());

        var first = await coordinator.ResumeAsync(action, Runtime(action), Guid.NewGuid());
        var second = await coordinator.ResumeAsync(action, Runtime(action), Guid.NewGuid());

        Assert.Equal(RetainedActionResumeOutcome.Retry, first.Outcome);
        Assert.Equal("connector-unavailable", first.Code);
        Assert.Equal(RetainedActionResumeOutcome.Consumed, second.Outcome);
        Assert.Equal(2, executor.Requests.Count);
        Assert.Equal(executor.Requests[0].IdempotencyKey, executor.Requests[1].IdempotencyKey);
        Assert.Contains(action.Id.ToString(), executor.Requests[0].IdempotencyKey, StringComparison.Ordinal);
        Assert.Contains(action.ActiveGrant!.Id.ToString(), executor.Requests[0].IdempotencyKey, StringComparison.Ordinal);
    }

    private static RetainedActionResumeCoordinator Coordinator(
        IRetainedActionPolicyEvaluator policy,
        IRetainedActionExecutor executor,
        IJourneyAuditLog audit) =>
        new(policy, executor, audit, new FixedTimeProvider(Now));

    private static PersistedRetainedAction AuthorizedAction(
        ActionFingerprint? grantFingerprint = null,
        DateTimeOffset? expiresAt = null)
    {
        var action = new PersistedRetainedAction(
            RetainedActionId.New(),
            ActionFingerprint.From("sha256:0000000000000000000000000000000000000000000000000000000000000001"),
            RetainedActionKind.Tool,
            "github.create-issue",
            "{\"arguments\":{\"title\":\"Incident\"}}",
            "{\"repository\":\"acme/hive\"}",
            "directive:resume",
            OrganizationId.From("acme"),
            PositionId.From("delivery-lead"),
            ThreadId.New(),
            MessageId.New(),
            DirectiveId.New(),
            null,
            "action-gate-objective-escalation",
            Now.AddHours(-1),
            [ApprovalPolicyRef.From("policy/security")]);
        var grant = new AuthorizationGrant(
            MessageId.New(),
            action.OrganizationId,
            new OrganizationOwnerEndpointRef(),
            new PositionEndpointRef(action.PositionId),
            action.ThreadId,
            Priority.High,
            1,
            Now.AddHours(-1),
            null,
            MessageId.New(),
            action.Id,
            grantFingerprint ?? action.Fingerprint,
            AuthorityKey.From("governance.authorize-retained-action"),
            expiresAt ?? Now.AddHours(1),
            null);

        return action.Authorize(grant, Now.AddMinutes(-30));
    }

    private static PositionRuntimeConfiguration Runtime(PersistedRetainedAction action) =>
        new(
            new PositionConfigurationStamp(1, "sha256:resume"),
            action.OrganizationId,
            action.PositionId,
            new PositionRuntimeDescriptor(UnitId.From("delivery")),
            new OccupantRuntimeConfiguration(OccupantType.AiAgent),
            new PositionAuthorityRuntimeConfiguration());

    private static void AssertAuditIsRedacted(JourneyAuditRecord record)
    {
        Assert.Equal(JourneyAuditStage.RetainedActionResume, record.Stage);
        Assert.Equal("canonicalPayload,canonicalFacts,governanceMessages", record.Payload["redactions"]);
        Assert.DoesNotContain("arguments", string.Join("|", record.Payload.Values), StringComparison.Ordinal);
        Assert.DoesNotContain("repository", string.Join("|", record.Payload.Values), StringComparison.Ordinal);
    }

    private sealed class RecordingPolicy(ActionGateOutcome outcome) : IRetainedActionPolicyEvaluator
    {
        public int Calls { get; private set; }

        public ValueTask<ActionGateOutcome> EvaluateAsync(
            PersistedRetainedAction action,
            PositionRuntimeConfiguration runtimeConfiguration,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(outcome);
        }
    }

    private sealed class RecordingExecutor(params RetainedActionExecutionResult[] results)
        : IRetainedActionExecutor
    {
        private int _index;
        public List<RetainedActionExecutionRequest> Requests { get; } = [];

        public ValueTask<RetainedActionExecutionResult> ExecuteAsync(
            RetainedActionExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var result = results[Math.Min(_index, results.Length - 1)];
            _index++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingAuditLog : IJourneyAuditLog
    {
        private readonly List<JourneyAuditRecord> _records = [];

        public void Append(JourneyAuditRecord record) => _records.Add(record);

        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null) =>
            _records.Where(record => record.ThreadId == threadId
                                     && (directiveId is null || record.DirectiveId == directiveId)).ToArray();

        public JourneyAuditRecord Single() => Assert.Single(_records);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
