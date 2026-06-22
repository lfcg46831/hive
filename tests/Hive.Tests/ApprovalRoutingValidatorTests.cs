using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class ApprovalRoutingValidatorTests
{
    private static readonly OrganizationId Org = OrganizationId.From("engineering-delivery");
    private static readonly ApprovalPolicyRef Policy = ApprovalPolicyRef.From("requires-human-approval");
    private static readonly ApprovalPolicyVersion Version =
        ApprovalPolicyVersion.Create("v3", "sha256:abc123");
    private static readonly DateTimeOffset Now =
        new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly ThreadId Thread = ThreadId.New();

    private static readonly PositionEndpointRef Requester = Position("engineer");
    private static readonly PositionEndpointRef Approver = Position("delivery-lead");

    // ---- ApprovalRequest ----

    [Fact]
    public async Task Request_to_resolved_approver_is_valid()
    {
        var validator = Validator(
            authority: ResolvingAuthority(ApproverResolution.Resolved(Policy, Approver, Version)));

        var result = await validator.ValidateAsync(Request(Requester, Approver));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Request_to_resolved_owner_approver_is_valid()
    {
        var owner = new OrganizationOwnerEndpointRef();
        var validator = Validator(
            authority: ResolvingAuthority(ApproverResolution.Resolved(Policy, owner, Version)));

        var result = await validator.ValidateAsync(Request(Requester, owner));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Request_to_wrong_approver_is_rejected()
    {
        var validator = Validator(
            authority: ResolvingAuthority(
                ApproverResolution.Resolved(Policy, Position("security-officer"), Version)));

        var result = await validator.ValidateAsync(Request(Requester, Approver));

        Assert.Equal(
            [new ValidationError("authorized-approver-required", "to", RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Fact]
    public async Task Request_with_undeclared_policy_is_rejected()
    {
        var validator = Validator(
            authority: ResolvingAuthority(ApproverResolution.PolicyNotFound(Policy)));

        var result = await validator.ValidateAsync(Request(Requester, Approver));

        Assert.Equal(
            [new ValidationError("approval-policy-not-found", "policy", RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Fact]
    public async Task Request_for_unauthorized_action_is_rejected()
    {
        var validator = Validator(
            authority: ResolvingAuthority(ApproverResolution.ActionNotAuthorized(Policy, Version)));

        var result = await validator.ValidateAsync(Request(Requester, Approver));

        Assert.Equal(
            [new ValidationError("action-not-authorized", "action", RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Fact]
    public async Task Expired_request_window_is_rejected()
    {
        var validator = Validator(
            authority: ResolvingAuthority(ApproverResolution.Resolved(Policy, Approver, Version)));

        var result = await validator.ValidateAsync(
            Request(Requester, Approver, deadline: Now.AddMinutes(-1)));

        Assert.Equal(
            [new ValidationError("approval-request-expired", "deadline", RejectionReason.Expired)],
            result.Errors);
    }

    [Fact]
    public async Task Request_window_exactly_at_now_is_expired()
    {
        var validator = Validator(
            authority: ResolvingAuthority(ApproverResolution.Resolved(Policy, Approver, Version)));

        var result = await validator.ValidateAsync(Request(Requester, Approver, deadline: Now));

        Assert.Contains(
            new ValidationError("approval-request-expired", "deadline", RejectionReason.Expired),
            result.Errors);
    }

    [Fact]
    public async Task Future_request_window_is_accepted()
    {
        var validator = Validator(
            authority: ResolvingAuthority(ApproverResolution.Resolved(Policy, Approver, Version)));

        var result = await validator.ValidateAsync(
            Request(Requester, Approver, deadline: Now.AddHours(1)));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Wrong_approver_and_expired_window_are_aggregated()
    {
        var validator = Validator(
            authority: ResolvingAuthority(
                ApproverResolution.Resolved(Policy, Position("security-officer"), Version)));

        var result = await validator.ValidateAsync(
            Request(Requester, Approver, deadline: Now.AddMinutes(-5)));

        Assert.Equal(
            [
                new ValidationError("approval-request-expired", "deadline", RejectionReason.Expired),
                new ValidationError("authorized-approver-required", "to", RejectionReason.InvalidRoute),
            ],
            result.Errors);
    }

    [Fact]
    public async Task Request_invalid_endpoints_are_aggregated_without_authority_query()
    {
        var validator = Validator(authority: new FailingAuthority(
            new InvalidOperationException("Authority must not be queried.")));
        var request = Request(
            new OrganizationOwnerEndpointRef(),
            new SystemEndpointRef(SystemEndpointKind.Scheduler));

        var result = await validator.ValidateAsync(request);

        Assert.Equal(
            [
                new ValidationError("endpoint-not-allowed", "from", RejectionReason.InvalidRoute),
                new ValidationError("endpoint-not-allowed", "to", RejectionReason.InvalidRoute),
            ],
            result.Errors);
    }

    [Fact]
    public async Task Unknown_organization_returns_canonical_error()
    {
        var validator = Validator(authority: new FailingAuthority(
            ApprovalAuthorityNotFoundException.ForOrganization(Org)));

        var result = await validator.ValidateAsync(Request(Requester, Approver));

        Assert.Equal(
            [new ValidationError("organization-not-found", "organizationId", RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Fact]
    public async Task Request_cancellation_propagates_before_authority()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var validator = Validator(authority: new FailingAuthority(
            new InvalidOperationException("Authority must not be queried.")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await validator.ValidateAsync(Request(Requester, Approver), cancellation.Token));
    }

    [Fact]
    public async Task Request_unexpected_authority_failure_propagates()
    {
        var failure = new InvalidOperationException("Authority unavailable.");
        var validator = Validator(authority: new FailingAuthority(failure));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await validator.ValidateAsync(Request(Requester, Approver)));

        Assert.Same(failure, thrown);
    }

    // ---- ApprovalDecision ----

    [Fact]
    public async Task Decision_from_authorized_approver_to_requester_is_valid()
    {
        var requestId = MessageId.New();
        var validator = Validator(requestLog: RecordingLog(Record(requestId)));

        var result = await validator.ValidateAsync(Decision(requestId, Approver, Requester));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Decision_from_owner_approver_is_valid()
    {
        var owner = new OrganizationOwnerEndpointRef();
        var requestId = MessageId.New();
        var validator = Validator(requestLog: RecordingLog(Record(requestId, approver: owner)));

        var result = await validator.ValidateAsync(Decision(requestId, owner, Requester));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Decision_without_correlated_request_is_rejected()
    {
        var validator = Validator(requestLog: RecordingLog(null));

        var result = await validator.ValidateAsync(
            Decision(MessageId.New(), Approver, Requester));

        Assert.Equal(
            [new ValidationError("approval-request-not-found", "requestId", RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Fact]
    public async Task Decision_from_unauthorized_approver_is_rejected()
    {
        var requestId = MessageId.New();
        var validator = Validator(requestLog: RecordingLog(Record(requestId)));

        var result = await validator.ValidateAsync(
            Decision(requestId, Position("security-officer"), Requester));

        Assert.Equal(
            [new ValidationError("unauthorized-approver", "from", RejectionReason.Unauthorized)],
            result.Errors);
    }

    [Fact]
    public async Task Decision_to_wrong_requester_is_rejected()
    {
        var requestId = MessageId.New();
        var validator = Validator(requestLog: RecordingLog(Record(requestId)));

        var result = await validator.ValidateAsync(
            Decision(requestId, Approver, Position("other-engineer")));

        Assert.Equal(
            [new ValidationError("original-requester-required", "to", RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Fact]
    public async Task Decision_on_closed_request_is_rejected()
    {
        var requestId = MessageId.New();
        var validator = Validator(
            requestLog: RecordingLog(Record(requestId, state: MessageState.Completed)));

        var result = await validator.ValidateAsync(Decision(requestId, Approver, Requester));

        Assert.Equal(
            [new ValidationError("approval-request-not-open", "requestId", RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Fact]
    public async Task Decision_after_window_closed_is_rejected()
    {
        var requestId = MessageId.New();
        var validator = Validator(
            requestLog: RecordingLog(Record(requestId, deadline: Now.AddMinutes(-1))));

        var result = await validator.ValidateAsync(Decision(requestId, Approver, Requester));

        Assert.Equal(
            [new ValidationError("approval-decision-expired", "requestId", RejectionReason.Expired)],
            result.Errors);
    }

    [Fact]
    public async Task Decision_on_mismatched_thread_is_rejected()
    {
        var requestId = MessageId.New();
        var validator = Validator(requestLog: RecordingLog(Record(requestId)));

        var result = await validator.ValidateAsync(
            Decision(requestId, Approver, Requester, thread: ThreadId.New()));

        Assert.Equal(
            [new ValidationError("approval-thread-mismatch", "thread", RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Fact]
    public async Task Decision_unauthorized_and_expired_are_aggregated()
    {
        var requestId = MessageId.New();
        var validator = Validator(
            requestLog: RecordingLog(Record(requestId, deadline: Now.AddMinutes(-1))));

        var result = await validator.ValidateAsync(
            Decision(requestId, Position("security-officer"), Requester));

        Assert.Equal(
            [
                new ValidationError("unauthorized-approver", "from", RejectionReason.Unauthorized),
                new ValidationError("approval-decision-expired", "requestId", RejectionReason.Expired),
            ],
            result.Errors);
    }

    [Fact]
    public async Task Decision_invalid_endpoints_are_aggregated_without_log_query()
    {
        var validator = Validator(requestLog: new FailingLog(
            new InvalidOperationException("Request log must not be queried.")));
        var decision = Decision(
            MessageId.New(),
            new SystemEndpointRef(SystemEndpointKind.Scheduler),
            new OrganizationOwnerEndpointRef());

        var result = await validator.ValidateAsync(decision);

        Assert.Equal(
            [
                new ValidationError("endpoint-not-allowed", "from", RejectionReason.InvalidRoute),
                new ValidationError("endpoint-not-allowed", "to", RejectionReason.InvalidRoute),
            ],
            result.Errors);
    }

    [Fact]
    public async Task Decision_cancellation_propagates_before_log()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var validator = Validator(requestLog: new FailingLog(
            new InvalidOperationException("Request log must not be queried.")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await validator.ValidateAsync(
                Decision(MessageId.New(), Approver, Requester),
                cancellation.Token));
    }

    [Fact]
    public async Task Decision_unexpected_log_failure_propagates()
    {
        var failure = new InvalidOperationException("Request log unavailable.");
        var validator = Validator(requestLog: new FailingLog(failure));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await validator.ValidateAsync(Decision(MessageId.New(), Approver, Requester)));

        Assert.Same(failure, thrown);
    }

    // ---- API misuse ----

    [Fact]
    public void Missing_dependencies_are_api_misuse()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ApprovalRoutingValidator(null!, RecordingLog(null)));
        Assert.Throws<ArgumentNullException>(
            () => new ApprovalRoutingValidator(ResolvingAuthority(ApproverResolution.PolicyNotFound(Policy)), null!));
    }

    [Fact]
    public async Task Missing_request_is_api_misuse()
    {
        var validator = Validator();

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await validator.ValidateAsync((ApprovalRequest)null!));
    }

    [Fact]
    public async Task Missing_decision_is_api_misuse()
    {
        var validator = Validator();

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await validator.ValidateAsync((ApprovalDecision)null!));
    }

    // ---- helpers ----

    private static ApprovalRoutingValidator Validator(
        IApprovalAuthority? authority = null,
        IApprovalRequestLog? requestLog = null) =>
        new(
            authority ?? ResolvingAuthority(ApproverResolution.Resolved(Policy, Approver, Version)),
            requestLog ?? RecordingLog(null),
            new FixedTimeProvider(Now));

    private static ApprovalRequest Request(
        EndpointRef from,
        EndpointRef to,
        DateTimeOffset? deadline = null) =>
        new(
            MessageId.New(),
            Org,
            from,
            to,
            Thread,
            Priority.High,
            1,
            Now.AddHours(-1),
            deadline,
            "publish-final",
            "The release candidate passed verification.",
            Policy);

    private static ApprovalDecision Decision(
        MessageId requestId,
        EndpointRef from,
        EndpointRef to,
        ThreadId? thread = null) =>
        new(
            MessageId.New(),
            Org,
            from,
            to,
            thread ?? Thread,
            Priority.High,
            1,
            Now.AddMinutes(-30),
            null,
            requestId,
            approved: true,
            reason: null);

    private static ApprovalRequestRecord Record(
        MessageId requestId,
        EndpointRef? approver = null,
        DateTimeOffset? deadline = null,
        MessageState state = MessageState.Accepted) =>
        new(
            requestId,
            Org,
            Requester.PositionId,
            approver ?? Approver,
            Version,
            Thread,
            deadline,
            state);

    private static PositionEndpointRef Position(string value) =>
        new(PositionId.From(value));

    private static IApprovalAuthority ResolvingAuthority(ApproverResolution resolution) =>
        new StubAuthority(resolution);

    private static IApprovalRequestLog RecordingLog(ApprovalRequestRecord? record) =>
        new StubRequestLog(record);

    private sealed class StubAuthority(ApproverResolution resolution) : IApprovalAuthority
    {
        public ValueTask<ApproverResolution> ResolveApproverAsync(
            ApprovalAuthorityQuery query,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            return new ValueTask<ApproverResolution>(resolution);
        }
    }

    private sealed class FailingAuthority(Exception failure) : IApprovalAuthority
    {
        public ValueTask<ApproverResolution> ResolveApproverAsync(
            ApprovalAuthorityQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ApproverResolution>(failure);
    }

    private sealed class StubRequestLog(ApprovalRequestRecord? record) : IApprovalRequestLog
    {
        public ValueTask<ApprovalRequestRecord?> FindRequestAsync(
            OrganizationId organizationId,
            MessageId requestId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(organizationId);
            ArgumentNullException.ThrowIfNull(requestId);
            return new ValueTask<ApprovalRequestRecord?>(record);
        }
    }

    private sealed class FailingLog(Exception failure) : IApprovalRequestLog
    {
        public ValueTask<ApprovalRequestRecord?> FindRequestAsync(
            OrganizationId organizationId,
            MessageId requestId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ApprovalRequestRecord?>(failure);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
