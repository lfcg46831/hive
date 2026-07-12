using Hive.Domain.Identity;
using Hive.Domain.Governance;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class GovernanceMessageTests
{
    private static readonly DateTimeOffset SentAt =
        new(2026, 6, 21, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ApprovalRequest_preserves_payload_and_derives_governance_channel()
    {
        var policy = ApprovalPolicyRef.From("production-release");

        var message = new ApprovalRequest(
            MessageId.New(), OrganizationId.From("acme"), Position("delivery-lead"),
            new OrganizationOwnerEndpointRef(), ThreadId.New(), Priority.High, 1,
            SentAt, SentAt.AddHours(2), "Deploy version 2.4 to production",
            "The release candidate passed verification", policy);

        Assert.Equal("Deploy version 2.4 to production", message.Action);
        Assert.Equal("The release candidate passed verification", message.Justification);
        Assert.Equal(policy, message.Policy);
        Assert.Equal(MessageChannel.Governance, message.Channel);
    }

    [Fact]
    public void ApprovalDecision_preserves_payload_and_derives_governance_channel()
    {
        var requestId = MessageId.New();

        var message = new ApprovalDecision(
            MessageId.New(), OrganizationId.From("acme"),
            new OrganizationOwnerEndpointRef(), Position("delivery-lead"),
            ThreadId.New(), Priority.High, 1, SentAt, null,
            requestId, approved: false, "Deployment window has closed");

        Assert.Equal(requestId, message.RequestId);
        Assert.False(message.Approved);
        Assert.Equal("Deployment window has closed", message.Reason);
        Assert.Equal(MessageChannel.Governance, message.Channel);
    }

    [Fact]
    public void ApprovalDecision_allows_approved_decision_without_reason()
    {
        var message = new ApprovalDecision(
            MessageId.New(), OrganizationId.From("acme"),
            new OrganizationOwnerEndpointRef(), Position("delivery-lead"),
            ThreadId.New(), Priority.Normal, 1, SentAt, null,
            MessageId.New(), approved: true, reason: null);

        Assert.True(message.Approved);
        Assert.Null(message.Reason);
    }

    [Fact]
    public void ApprovalRequest_rejects_missing_policy_reference()
    {
        Assert.Throws<ArgumentNullException>(() => new ApprovalRequest(
            MessageId.New(), OrganizationId.From("acme"), Position("delivery-lead"),
            new OrganizationOwnerEndpointRef(), ThreadId.New(), Priority.High, 1,
            SentAt, null, "Deploy", "Release is verified", null!));
    }

    [Fact]
    public void ApprovalDecision_rejects_missing_request_reference()
    {
        Assert.Throws<ArgumentNullException>(() => new ApprovalDecision(
            MessageId.New(), OrganizationId.From("acme"),
            new OrganizationOwnerEndpointRef(), Position("delivery-lead"),
            ThreadId.New(), Priority.High, 1, SentAt, null,
            null!, approved: true, reason: null));
    }

    [Fact]
    public void AuthorizationGrant_preserves_bound_payload_and_allows_no_reason()
    {
        var inReplyTo = MessageId.New();
        var retainedActionId = RetainedActionId.New();
        var fingerprint = ActionFingerprint.From($"sha256:{new string('a', 64)}");
        var key = AuthorityKey.From("delivery.bug-triage");
        var expiresAt = SentAt.AddHours(24);

        var message = new AuthorizationGrant(
            MessageId.New(), OrganizationId.From("acme"),
            new OrganizationOwnerEndpointRef(), Position("delivery-lead"),
            ThreadId.New(), Priority.High, 1, SentAt, null,
            inReplyTo, retainedActionId, fingerprint, key, expiresAt, reason: null);

        Assert.Equal(inReplyTo, message.InReplyTo);
        Assert.Equal(retainedActionId, message.RetainedActionId);
        Assert.Equal(fingerprint, message.Fingerprint);
        Assert.Equal(key, message.Key);
        Assert.Equal(expiresAt, message.ExpiresAt);
        Assert.Null(message.Reason);
        Assert.Equal(MessageChannel.Governance, message.Channel);
    }

    [Fact]
    public void AuthorizationDenial_preserves_bound_payload()
    {
        var inReplyTo = MessageId.New();
        var retainedActionId = RetainedActionId.New();

        var message = new AuthorizationDenial(
            MessageId.New(), OrganizationId.From("acme"), Position("delivery-lead"),
            Position("developer"), ThreadId.New(), Priority.High, 1, SentAt, null,
            inReplyTo, retainedActionId, "Use the approved deployment window");

        Assert.Equal(inReplyTo, message.InReplyTo);
        Assert.Equal(retainedActionId, message.RetainedActionId);
        Assert.Equal("Use the approved deployment window", message.Reason);
        Assert.Equal(MessageChannel.Governance, message.Channel);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void AuthorizationGrant_rejects_missing_typed_references(
        bool missingReply, bool missingAction, bool missingFingerprint, bool missingKey)
    {
        Assert.Throws<ArgumentNullException>(() => new AuthorizationGrant(
            MessageId.New(), OrganizationId.From("acme"), Position("lead"), Position("developer"),
            ThreadId.New(), Priority.High, 1, SentAt, null,
            missingReply ? null! : MessageId.New(),
            missingAction ? null! : RetainedActionId.New(),
            missingFingerprint ? null! : ActionFingerprint.From($"sha256:{new string('b', 64)}"),
            missingKey ? null! : AuthorityKey.From("delivery.bug-triage"),
            SentAt.AddHours(1), null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AuthorizationDenial_rejects_missing_typed_references(bool missingReply)
    {
        Assert.Throws<ArgumentNullException>(() => new AuthorizationDenial(
            MessageId.New(), OrganizationId.From("acme"), Position("lead"), Position("developer"),
            ThreadId.New(), Priority.High, 1, SentAt, null,
            missingReply ? null! : MessageId.New(),
            missingReply ? RetainedActionId.New() : null!,
            "Denied"));
    }

    [Fact]
    public void Governance_payload_properties_are_get_only()
    {
        AssertGetOnly<ApprovalRequest>(
            nameof(ApprovalRequest.Action),
            nameof(ApprovalRequest.Justification),
            nameof(ApprovalRequest.Policy),
            nameof(ApprovalRequest.Channel));
        AssertGetOnly<ApprovalDecision>(
            nameof(ApprovalDecision.RequestId),
            nameof(ApprovalDecision.Approved),
            nameof(ApprovalDecision.Reason),
            nameof(ApprovalDecision.Channel));
        AssertGetOnly<AuthorizationGrant>(
            nameof(AuthorizationGrant.InReplyTo),
            nameof(AuthorizationGrant.RetainedActionId),
            nameof(AuthorizationGrant.Fingerprint),
            nameof(AuthorizationGrant.Key),
            nameof(AuthorizationGrant.ExpiresAt),
            nameof(AuthorizationGrant.Reason),
            nameof(AuthorizationGrant.Channel));
        AssertGetOnly<AuthorizationDenial>(
            nameof(AuthorizationDenial.InReplyTo),
            nameof(AuthorizationDenial.RetainedActionId),
            nameof(AuthorizationDenial.Reason),
            nameof(AuthorizationDenial.Channel));
    }

    private static PositionEndpointRef Position(string value) =>
        new(PositionId.From(value));

    private static void AssertGetOnly<T>(params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = typeof(T).GetProperty(propertyName);

            Assert.NotNull(property);
            Assert.Null(property.SetMethod);
        }
    }
}
