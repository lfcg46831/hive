using Hive.Domain.Auditing;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.Auditing;

namespace Hive.Tests;

public sealed class RetainedActionLifecycleAuditTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 12, 17, 0, 0, TimeSpan.Zero);
    private static readonly PositionEntityId Entity = PositionEntityId.From(
        OrganizationId.From("acme"), PositionId.From("engineer"));

    [Fact]
    public void Persisted_lifecycle_and_re_escalation_are_correlated_redacted_and_idempotent()
    {
        var audit = new RecordingAuditLog();
        var inner = new RecordingProjectionPublisher();
        var publisher = new JourneyAuditPositionProjectionPublisher(audit, inner);
        var retained = Action();
        var grant = Grant(retained, "free-form grant reason");
        var authorizedEvent = new RetainedActionAuthorized(grant, At.AddMinutes(1));
        var authorized = retained.Authorize(grant, authorizedEvent.OccurredAt);
        var consumedEvent = new RetainedActionConsumed(retained.Id, grant.Id, At.AddMinutes(2));
        var consumed = authorized.Consume(consumedEvent.OccurredAt);
        var expiredEvent = new RetainedActionExpired(
            retained.Id, grant.Id, "authorization-expired", At.AddMinutes(3));
        var expired = authorized.Expire(expiredEvent.OccurredAt, expiredEvent.ReEscalationCode);

        publisher.Publish(new PositionRetainedActionLifecycleChanged(Entity, authorized, authorizedEvent));
        publisher.Publish(new PositionRetainedActionLifecycleChanged(Entity, consumed, consumedEvent));
        publisher.Publish(new PositionRetainedActionLifecycleChanged(Entity, expired, expiredEvent));
        var reEscalation = new PositionRetainedActionReEscalationReady(Entity, expired, expiredEvent);
        publisher.Publish(reEscalation);
        publisher.Publish(reEscalation);

        Assert.Equal(5, audit.Records.Count);
        Assert.Equal(
            [
                JourneyAuditStage.RetainedActionLifecycle,
                JourneyAuditStage.RetainedActionLifecycle,
                JourneyAuditStage.RetainedActionLifecycle,
                JourneyAuditStage.RetainedActionReEscalation,
                JourneyAuditStage.RetainedActionReEscalation,
            ],
            audit.Records.Select(record => record.Stage));
        Assert.Equal(audit.Records[3].AuditEventId, audit.Records[4].AuditEventId);

        var consumption = audit.Records[1];
        Assert.Equal(grant.Id, consumption.MessageId);
        Assert.Equal(grant.Key.Value, consumption.Payload["authorityKey"]);
        Assert.Equal("policy/security", consumption.Payload["approvalPolicyRefs"]);
        Assert.Equal(retained.Id.ToString(), consumption.Payload["retainedActionId"]);

        Assert.All(audit.Records, record =>
        {
            var values = string.Join("|", record.Payload.Values);
            Assert.DoesNotContain("free-form grant reason", values);
            Assert.DoesNotContain("secret-title", values);
            Assert.DoesNotContain(retained.Fingerprint.ToString(), values);
            Assert.Equal(
                "reason,fingerprint,canonicalPayload,canonicalFacts,governanceMessages",
                record.Payload["redactions"]);
        });
        Assert.Equal(5, inner.Events.Count);
    }

    [Fact]
    public void Accepted_denial_is_audited_without_free_form_reason()
    {
        var audit = new RecordingAuditLog();
        var publisher = new JourneyAuditPositionProjectionPublisher(audit);
        var retained = Action();
        var denial = Denial(retained, "customer secret");
        var transition = new RetainedActionDenied(denial, At.AddMinutes(1));

        publisher.Publish(new PositionRetainedActionLifecycleChanged(
            Entity,
            retained.Deny(denial, transition.OccurredAt),
            transition));

        var record = Assert.Single(audit.Records);
        Assert.Equal(JourneyAuditOutcome.Accepted, record.Outcome);
        Assert.Equal("authorization-denial-accepted", record.ReasonCode);
        Assert.Equal(nameof(AuthorizationDenial), record.Payload["resolutionType"]);
        Assert.DoesNotContain("customer secret", string.Join("|", record.Payload.Values));
        Assert.False(record.Payload.ContainsKey("authorityKey"));
    }

    private static PersistedRetainedAction Action() =>
        new(
            RetainedActionId.New(),
            ActionFingerprint.From($"sha256:{new string('b', 64)}"),
            RetainedActionKind.Tool,
            "github.create-issue",
            "{\"title\":\"secret-title\"}",
            "{\"repository\":\"secret-repository\"}",
            "directive:audit",
            Entity.Organization,
            Entity.Position,
            ThreadId.New(),
            MessageId.New(),
            DirectiveId.New(),
            null,
            "action-gate-escalation-required",
            At,
            [ApprovalPolicyRef.From("policy/security")]);

    private static AuthorizationGrant Grant(PersistedRetainedAction action, string reason) =>
        new(
            MessageId.New(), action.OrganizationId, new OrganizationOwnerEndpointRef(),
            new PositionEndpointRef(action.PositionId), action.ThreadId, Priority.High, 1, At,
            null, MessageId.New(), action.Id, action.Fingerprint,
            AuthorityKey.From("delivery.release"), At.AddHours(1), reason);

    private static AuthorizationDenial Denial(PersistedRetainedAction action, string reason) =>
        new(
            MessageId.New(), action.OrganizationId, new OrganizationOwnerEndpointRef(),
            new PositionEndpointRef(action.PositionId), action.ThreadId, Priority.High, 1, At,
            null, MessageId.New(), action.Id, reason);

    private sealed class RecordingAuditLog : IJourneyAuditLog
    {
        public List<JourneyAuditRecord> Records { get; } = [];
        public void Append(JourneyAuditRecord record) => Records.Add(record);
        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null) => Records.Where(record => record.ThreadId == threadId).ToArray();
    }

    private sealed class RecordingProjectionPublisher : IPositionProjectionPublisher
    {
        public List<PositionProjectionEvent> Events { get; } = [];
        public void Publish(PositionProjectionEvent @event) => Events.Add(@event);
    }
}
