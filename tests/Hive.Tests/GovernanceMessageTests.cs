using Hive.Domain.Identity;
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
