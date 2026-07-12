using Hive.Domain.Auditing;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class AuditedAuthorizationRoutingValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 16, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionEndpointRef Requester = new(PositionId.From("engineer"));
    private static readonly PositionEndpointRef Recipient = new(PositionId.From("lead"));
    private static readonly ThreadId Thread = ThreadId.New();
    private static readonly MessageId EscalationId = MessageId.New();
    private static readonly RetainedActionId ActionId = RetainedActionId.New();
    private static readonly ActionFingerprint Fingerprint =
        ActionFingerprint.From($"sha256:{new string('a', 64)}");

    [Fact]
    public async Task Accepted_grant_and_denial_are_audited_with_only_whitelisted_fields()
    {
        var audit = new RecordingAuditLog();
        var sut = Validator(Record(), audit);

        var grant = Grant("free-form grant reason");
        var grantResult = await sut.ValidateAsync(grant);
        var denial = Denial("sensitive denial reason");
        var denialResult = await Validator(Record(), audit).ValidateAsync(denial);

        Assert.True(grantResult.IsValid);
        Assert.True(denialResult.IsValid);
        Assert.Collection(
            audit.Records,
            record => AssertResolution(record, grant, nameof(AuthorizationGrant), hasAuthorityKey: true),
            record => AssertResolution(record, denial, nameof(AuthorizationDenial), hasAuthorityKey: false));
        Assert.DoesNotContain(
            "free-form grant reason",
            string.Join("|", audit.Records.SelectMany(record => record.Payload.Values)));
        Assert.DoesNotContain(
            "sensitive denial reason",
            string.Join("|", audit.Records.SelectMany(record => record.Payload.Values)));
    }

    [Fact]
    public async Task Aggregated_rejection_audits_only_sorted_stable_codes()
    {
        var audit = new RecordingAuditLog();
        var record = new AuthorizationEscalationRecord(
            EscalationId,
            Organization,
            ThreadId.New(),
            Requester,
            Recipient,
            RetainedActionId.New(),
            MessageId.New());

        var result = await Validator(record, audit).ValidateAsync(
            Grant("must not leak", expiresAt: Now));

        Assert.False(result.IsValid);
        var entry = Assert.Single(audit.Records);
        Assert.Equal(JourneyAuditOutcome.Rejected, entry.Outcome);
        Assert.Equal(AuditedAuthorizationRoutingValidator.RejectedCode, entry.ReasonCode);
        Assert.Equal(
            string.Join(",", result.Errors.Select(error => error.Code).Distinct().Order()),
            entry.Payload["validationCodes"]);
        Assert.DoesNotContain("must not leak", string.Join("|", entry.Payload.Values));
    }

    [Fact]
    public async Task Technical_failure_propagates_without_semantic_audit()
    {
        var audit = new RecordingAuditLog();
        var failure = new InvalidOperationException("registry unavailable");
        var sut = new AuditedAuthorizationRoutingValidator(
            new AuthorizationRoutingValidator(new FailingLog(failure)),
            audit,
            new FixedTimeProvider(Now));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.ValidateAsync(Grant(null)));

        Assert.Same(failure, thrown);
        Assert.Empty(audit.Records);
    }

    private static void AssertResolution(
        JourneyAuditRecord record,
        OrgMessage resolution,
        string resolutionType,
        bool hasAuthorityKey)
    {
        Assert.Equal(JourneyAuditStage.AuthorizationResolution, record.Stage);
        Assert.Equal(JourneyAuditOutcome.Accepted, record.Outcome);
        Assert.Equal(AuditedAuthorizationRoutingValidator.AcceptedCode, record.ReasonCode);
        Assert.Equal(resolution.Id, record.MessageId);
        Assert.Equal(resolutionType, record.Payload["resolutionType"]);
        Assert.Equal("", record.Payload["validationCodes"]);
        Assert.Equal(hasAuthorityKey, record.Payload.ContainsKey("authorityKey"));
        Assert.Equal("reason,fingerprint,message.payload", record.Payload["redactions"]);
        Assert.DoesNotContain("fingerprint", record.Payload.Keys, StringComparer.OrdinalIgnoreCase);
    }

    private static AuditedAuthorizationRoutingValidator Validator(
        AuthorizationEscalationRecord record,
        IJourneyAuditLog audit) =>
        new(
            new AuthorizationRoutingValidator(new StubLog(record), new FixedTimeProvider(Now)),
            audit,
            new FixedTimeProvider(Now));

    private static AuthorizationEscalationRecord Record() =>
        new(EscalationId, Organization, Thread, Requester, Recipient, ActionId);

    private static AuthorizationGrant Grant(string? reason, DateTimeOffset? expiresAt = null) =>
        new(
            MessageId.New(), Organization, Recipient, Requester, Thread, Priority.High, 1,
            Now.AddMinutes(-1), null, EscalationId, ActionId, Fingerprint,
            AuthorityKey.From("delivery.release"), expiresAt ?? Now.AddHours(1), reason);

    private static AuthorizationDenial Denial(string reason) =>
        new(
            MessageId.New(), Organization, Recipient, Requester, Thread, Priority.High, 1,
            Now.AddMinutes(-1), null, EscalationId, ActionId, reason);

    private sealed class StubLog(AuthorizationEscalationRecord record) : IAuthorizationEscalationLog
    {
        public ValueTask<AuthorizationEscalationRecord?> FindEscalationAsync(
            OrganizationId organizationId,
            MessageId escalationId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(record)!;
    }

    private sealed class FailingLog(Exception failure) : IAuthorizationEscalationLog
    {
        public ValueTask<AuthorizationEscalationRecord?> FindEscalationAsync(
            OrganizationId organizationId,
            MessageId escalationId,
            CancellationToken cancellationToken = default) => ValueTask.FromException<AuthorizationEscalationRecord?>(failure);
    }

    private sealed class RecordingAuditLog : IJourneyAuditLog
    {
        public List<JourneyAuditRecord> Records { get; } = [];
        public void Append(JourneyAuditRecord record) => Records.Add(record);
        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null) => Records.Where(record => record.ThreadId == threadId).ToArray();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
