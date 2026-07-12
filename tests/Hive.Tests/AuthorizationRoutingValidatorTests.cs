using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class AuthorizationRoutingValidatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Org = OrganizationId.From("engineering-delivery");
    private static readonly ThreadId Thread = ThreadId.New();
    private static readonly MessageId EscalationId = MessageId.New();
    private static readonly RetainedActionId ActionId = RetainedActionId.New();
    private static readonly PositionEndpointRef Requester = Position("engineer");
    private static readonly PositionEndpointRef Recipient = Position("delivery-lead");
    private static readonly ActionFingerprint Fingerprint =
        ActionFingerprint.From($"sha256:{new string('a', 64)}");
    private static readonly AuthorityKey Key = AuthorityKey.From("delivery.release");

    [Fact]
    public async Task Grant_from_original_position_recipient_to_requester_is_valid()
    {
        var result = await Validator(Record()).ValidateAsync(Grant(Recipient, Requester));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Grant_from_original_owner_recipient_is_valid()
    {
        var owner = new OrganizationOwnerEndpointRef();
        var result = await Validator(Record(recipient: owner)).ValidateAsync(Grant(owner, Requester));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Denial_uses_same_correlated_reverse_route_without_expiry()
    {
        var result = await Validator(Record()).ValidateAsync(Denial(Recipient, Requester));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Denial_from_original_owner_recipient_is_valid()
    {
        var owner = new OrganizationOwnerEndpointRef();
        var result = await Validator(Record(recipient: owner)).ValidateAsync(Denial(owner, Requester));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Missing_correlation_is_rejected()
    {
        var result = await Validator(null).ValidateAsync(Grant(Recipient, Requester));

        Assert.Equal([AuthorizationValidationCatalog.EscalationNotFound()], result.Errors);
    }

    [Fact]
    public async Task Cross_organization_record_is_rejected()
    {
        var other = OrganizationId.From("other-organization");
        var result = await Validator(Record(organizationId: other))
            .ValidateAsync(Grant(Recipient, Requester));

        Assert.Equal([AuthorizationValidationCatalog.OrganizationMismatch()], result.Errors);
    }

    [Fact]
    public async Task Mismatched_thread_is_rejected()
    {
        var result = await Validator(Record(thread: ThreadId.New()))
            .ValidateAsync(Grant(Recipient, Requester));

        Assert.Equal([AuthorizationValidationCatalog.ThreadMismatch()], result.Errors);
    }

    [Fact]
    public async Task Mismatched_retained_action_is_rejected()
    {
        var result = await Validator(Record(retainedActionId: RetainedActionId.New()))
            .ValidateAsync(Grant(Recipient, Requester));

        Assert.Equal([AuthorizationValidationCatalog.RetainedActionMismatch()], result.Errors);
    }

    [Fact]
    public async Task Issuer_must_be_original_escalation_recipient()
    {
        var result = await Validator(Record())
            .ValidateAsync(Grant(Position("security-lead"), Requester));

        Assert.Equal([AuthorizationValidationCatalog.UnauthorizedIssuer()], result.Errors);
    }

    [Fact]
    public async Task Destination_must_be_original_requester()
    {
        var result = await Validator(Record())
            .ValidateAsync(Grant(Recipient, Position("other-engineer")));

        Assert.Equal([AuthorizationValidationCatalog.OriginalRequesterRequired()], result.Errors);
    }

    [Fact]
    public async Task Resolution_after_one_was_accepted_is_duplicate()
    {
        var result = await Validator(Record(resolutionMessageId: MessageId.New()))
            .ValidateAsync(Denial(Recipient, Requester));

        Assert.Equal([AuthorizationValidationCatalog.ResponseDuplicate()], result.Errors);
    }

    [Fact]
    public async Task Grant_expiring_after_now_is_valid()
    {
        var result = await Validator(Record()).ValidateAsync(
            Grant(Recipient, Requester, expiresAt: Now.AddTicks(1)));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Grant_at_or_before_now_is_expired(int offsetMinutes)
    {
        var result = await Validator(Record()).ValidateAsync(
            Grant(Recipient, Requester, expiresAt: Now.AddMinutes(offsetMinutes)));

        Assert.Equal([AuthorizationValidationCatalog.GrantExpired()], result.Errors);
    }

    [Fact]
    public async Task Semantic_failures_are_aggregated_deterministically()
    {
        var result = await Validator(Record(
                thread: ThreadId.New(),
                retainedActionId: RetainedActionId.New(),
                resolutionMessageId: MessageId.New()))
            .ValidateAsync(Grant(
                Position("wrong-issuer"),
                Position("wrong-requester"),
                expiresAt: Now));

        Assert.Equal(6, result.Errors.Count);
        Assert.Contains(AuthorizationValidationCatalog.ThreadMismatch(), result.Errors);
        Assert.Contains(AuthorizationValidationCatalog.RetainedActionMismatch(), result.Errors);
        Assert.Contains(AuthorizationValidationCatalog.UnauthorizedIssuer(), result.Errors);
        Assert.Contains(AuthorizationValidationCatalog.OriginalRequesterRequired(), result.Errors);
        Assert.Contains(AuthorizationValidationCatalog.ResponseDuplicate(), result.Errors);
        Assert.Contains(AuthorizationValidationCatalog.GrantExpired(), result.Errors);
    }

    [Fact]
    public async Task Invalid_endpoints_are_aggregated_without_log_query()
    {
        var validator = new AuthorizationRoutingValidator(new FailingLog(
            new InvalidOperationException("Log must not be queried.")));
        var result = await validator.ValidateAsync(Grant(
            new SystemEndpointRef(SystemEndpointKind.Scheduler),
            new OrganizationOwnerEndpointRef()));

        Assert.Equal(
            [
                AuthorizationValidationCatalog.EndpointNotAllowed("from"),
                AuthorizationValidationCatalog.EndpointNotAllowed("to"),
            ],
            result.Errors);
    }

    [Fact]
    public async Task Cancellation_propagates_before_log_query()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var validator = new AuthorizationRoutingValidator(new FailingLog(
            new InvalidOperationException("Log must not be queried.")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await validator.ValidateAsync(
                Grant(Recipient, Requester),
                cancellation.Token));
    }

    [Fact]
    public async Task Technical_log_failure_propagates()
    {
        var failure = new InvalidOperationException("Authorization escalation log unavailable.");
        var validator = new AuthorizationRoutingValidator(new FailingLog(failure));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await validator.ValidateAsync(Grant(Recipient, Requester)));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task Null_dependencies_and_messages_are_api_misuse()
    {
        Assert.Throws<ArgumentNullException>(() => new AuthorizationRoutingValidator(null!));
        var validator = Validator(null);
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await validator.ValidateAsync((AuthorizationGrant)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await validator.ValidateAsync((AuthorizationDenial)null!));
    }

    [Fact]
    public void Escalation_record_rejects_non_governance_recipient()
    {
        Assert.Throws<ArgumentException>(() => new AuthorizationEscalationRecord(
            EscalationId,
            Org,
            Thread,
            Requester,
            new SystemEndpointRef(SystemEndpointKind.Scheduler),
            ActionId));
    }

    private static AuthorizationRoutingValidator Validator(AuthorizationEscalationRecord? record) =>
        new(new StubLog(record), new FixedTimeProvider(Now));

    private static AuthorizationEscalationRecord Record(
        OrganizationId? organizationId = null,
        ThreadId? thread = null,
        EndpointRef? recipient = null,
        RetainedActionId? retainedActionId = null,
        MessageId? resolutionMessageId = null) =>
        new(
            EscalationId,
            organizationId ?? Org,
            thread ?? Thread,
            Requester,
            recipient ?? Recipient,
            retainedActionId ?? ActionId,
            resolutionMessageId);

    private static AuthorizationGrant Grant(
        EndpointRef from,
        EndpointRef to,
        DateTimeOffset? expiresAt = null) =>
        new(
            MessageId.New(),
            Org,
            from,
            to,
            Thread,
            Priority.High,
            1,
            Now.AddHours(-2),
            null,
            EscalationId,
            ActionId,
            Fingerprint,
            Key,
            expiresAt ?? Now.AddHours(1),
            null);

    private static AuthorizationDenial Denial(EndpointRef from, EndpointRef to) =>
        new(
            MessageId.New(),
            Org,
            from,
            to,
            Thread,
            Priority.High,
            1,
            Now.AddHours(-2),
            null,
            EscalationId,
            ActionId,
            "The retained action is not authorized.");

    private static PositionEndpointRef Position(string value) =>
        new(PositionId.From(value));

    private sealed class StubLog(AuthorizationEscalationRecord? record) : IAuthorizationEscalationLog
    {
        public ValueTask<AuthorizationEscalationRecord?> FindEscalationAsync(
            OrganizationId organizationId,
            MessageId escalationId,
            CancellationToken cancellationToken = default) =>
            new(record);
    }

    private sealed class FailingLog(Exception failure) : IAuthorizationEscalationLog
    {
        public ValueTask<AuthorizationEscalationRecord?> FindEscalationAsync(
            OrganizationId organizationId,
            MessageId escalationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<AuthorizationEscalationRecord?>(failure);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
