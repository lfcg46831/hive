using System.Reflection;
using System.Text.Json;
using Hive.Contracts.Inbox;

namespace Hive.Tests;

public sealed class InboxPublicContractTests
{
    public static TheoryData<InboxMessageContent, string, string[]> CanonicalContent
    {
        get
        {
            var data = new TheoryData<InboxMessageContent, string, string[]>();
            data.Add(
                new InboxDirectiveMessageContent("Fix the regression", "Production is affected"),
                "Directive",
                ["type", "objective", "context"]);
            data.Add(
                new InboxReportMessageContent("Fix is in progress", InboxReportKind.Progress),
                "Report",
                ["type", "body", "kind"]);
            data.Add(
                new InboxEscalationMessageContent("Deployment blocked", "Credential expired"),
                "Escalation",
                ["type", "issue", "context"]);
            data.Add(
                new InboxMemoMessageContent("Release window changed"),
                "Memo",
                ["type", "body"]);
            data.Add(
                new InboxPeerRequestMessageContent("Can you reproduce this?"),
                "PeerRequest",
                ["type", "ask"]);
            data.Add(
                new InboxPeerResponseMessageContent("Reproduced on iOS 17"),
                "PeerResponse",
                ["type", "body"]);
            data.Add(
                new InboxApprovalRequestMessageContent(
                    "Deploy the hotfix",
                    "Customer-facing outage"),
                "ApprovalRequest",
                ["type", "action", "justification"]);
            data.Add(
                new InboxApprovalDecisionMessageContent("Approved for the outage"),
                "ApprovalDecision",
                ["type", "reason"]);
            data.Add(
                new InboxApprovalDecisionMessageContent(reason: null),
                "ApprovalDecision",
                ["type"]);
            return data;
        }
    }

    private static readonly Guid RequestId =
        Guid.Parse("cf2b086f-dd04-445f-a68e-8e40a75530b9");

    private static readonly Guid DecisionId =
        Guid.Parse("c15095cd-7f12-4099-a15b-995275dfb3d0");

    private static readonly Guid ThreadId =
        Guid.Parse("68bba79b-d881-40a8-82fd-09b08e2adfd7");

    private static readonly DateTimeOffset SentAt =
        new(2026, 8, 4, 9, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset DeadlineAt = SentAt.AddHours(4);

    [Fact]
    public void Approval_request_serializes_the_stable_public_inbox_shape()
    {
        var item = CreateApprovalRequest();

        var json = JsonSerializer.SerializeToElement(item);

        Assert.Equal("delivery-lead/cf2b086f-dd04-445f-a68e-8e40a75530b9", json.GetProperty("item_id").GetString());
        Assert.Equal(RequestId, json.GetProperty("message_id").GetGuid());
        Assert.Equal("delivery-lead", json.GetProperty("assigned_position_id").GetString());
        Assert.Equal("ApprovalRequest", json.GetProperty("type").GetString());
        Assert.Equal(
            "Position",
            json.GetProperty("origin").GetProperty("type").GetString());
        Assert.Equal(
            "engineer",
            json.GetProperty("origin").GetProperty("position_id").GetString());
        Assert.Equal(
            "OrganizationOwner",
            json.GetProperty("destination").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("destination").GetProperty("position_id").ValueKind);
        Assert.Equal(ThreadId, json.GetProperty("thread_id").GetGuid());
        Assert.Equal("Critical", json.GetProperty("priority").GetString());
        Assert.Equal(SentAt, json.GetProperty("sent_at_utc").GetDateTimeOffset());
        Assert.Equal(DeadlineAt, json.GetProperty("deadline_at_utc").GetDateTimeOffset());
        Assert.False(json.GetProperty("is_expired").GetBoolean());
        Assert.Equal("None", json.GetProperty("reminder_state").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("last_reminder_at_utc").ValueKind);
        Assert.False(json.GetProperty("is_delegated").GetBoolean());
        Assert.Equal("Unread", json.GetProperty("read_state").GetString());
        Assert.Equal("NotApplicable", json.GetProperty("response_state").GetString());

        var approval = json.GetProperty("approval");
        Assert.Equal(RequestId, approval.GetProperty("request_id").GetGuid());
        Assert.Equal("deployment.production", approval.GetProperty("action").GetString());
        Assert.Equal("production-change", approval.GetProperty("policy_ref").GetString());
        Assert.Equal("Pending", approval.GetProperty("state").GetString());
        Assert.True(approval.GetProperty("can_decide").GetBoolean());
        Assert.Equal(JsonValueKind.Null, approval.GetProperty("decision_message_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, approval.GetProperty("decided_at_utc").ValueKind);
    }

    [Fact]
    public void Non_approval_message_has_no_approval_metadata()
    {
        var item = new InboxItem(
            "engineer/c15095cd-7f12-4099-a15b-995275dfb3d0",
            DecisionId,
            "engineer",
            InboxMessageType.Directive,
            new InboxMessageEndpoint(InboxMessageEndpointType.Position, "delivery-lead"),
            new InboxMessageEndpoint(InboxMessageEndpointType.Position, "engineer"),
            ThreadId,
            InboxPriority.High,
            SentAt,
            null,
            InboxReadState.Read,
            InboxResponseState.InProgress);

        var json = JsonSerializer.SerializeToElement(item);

        Assert.Equal(JsonValueKind.Null, json.GetProperty("deadline_at_utc").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("approval").ValueKind);
        Assert.Equal("InProgress", json.GetProperty("response_state").GetString());
    }

    [Fact]
    public void Realtime_invalidation_serializes_sequence_scope_and_change_type()
    {
        var notification = new InboxChangedNotification(
            sequence: 42,
            organizationId: "acme",
            itemId: "delivery-lead/cf2b086f-dd04-445f-a68e-8e40a75530b9",
            assignedPositionId: "delivery-lead",
            InboxChangeType.ApprovalPending,
            SentAt);

        var json = JsonSerializer.SerializeToElement(notification);

        Assert.Equal(42, json.GetProperty("sequence").GetInt64());
        Assert.Equal("acme", json.GetProperty("organization_id").GetString());
        Assert.Equal(
            "delivery-lead/cf2b086f-dd04-445f-a68e-8e40a75530b9",
            json.GetProperty("item_id").GetString());
        Assert.Equal(
            "delivery-lead",
            json.GetProperty("assigned_position_id").GetString());
        Assert.Equal("ApprovalPending", json.GetProperty("change_type").GetString());
        Assert.Equal(SentAt, json.GetProperty("changed_at_utc").GetDateTimeOffset());
    }

    [Fact]
    public void Deadline_reminder_expiry_and_delegation_serialize_as_derived_item_state()
    {
        var reminderAt = DeadlineAt.AddMinutes(-30);
        var item = new InboxItem(
            "delivery-lead/cf2b086f-dd04-445f-a68e-8e40a75530b9",
            RequestId,
            "delivery-lead",
            InboxMessageType.ApprovalRequest,
            new InboxMessageEndpoint(InboxMessageEndpointType.Position, "engineer"),
            new InboxMessageEndpoint(InboxMessageEndpointType.Position, "delivery-lead"),
            ThreadId,
            InboxPriority.Critical,
            SentAt,
            DeadlineAt,
            InboxReadState.Unread,
            InboxResponseState.NotApplicable,
            new InboxApprovalMetadata(
                RequestId,
                "deployment.production",
                "production-change",
                InboxApprovalState.Expired,
                canDecide: false),
            isExpired: true,
            reminderState: InboxReminderState.Sent,
            lastReminderAtUtc: reminderAt,
            isDelegated: true);

        var json = JsonSerializer.SerializeToElement(item);

        Assert.True(json.GetProperty("is_expired").GetBoolean());
        Assert.Equal("Sent", json.GetProperty("reminder_state").GetString());
        Assert.Equal(
            reminderAt,
            json.GetProperty("last_reminder_at_utc").GetDateTimeOffset());
        Assert.True(json.GetProperty("is_delegated").GetBoolean());
    }

    [Fact]
    public void Approval_decision_requires_correlated_decision_metadata()
    {
        var decidedAt = SentAt.AddMinutes(10);
        var metadata = new InboxApprovalMetadata(
            RequestId,
            "deployment.production",
            "production-change",
            InboxApprovalState.Approved,
            canDecide: false,
            DecisionId,
            decidedAt);

        var item = new InboxItem(
            "engineer/c15095cd-7f12-4099-a15b-995275dfb3d0",
            DecisionId,
            "engineer",
            InboxMessageType.ApprovalDecision,
            new InboxMessageEndpoint(InboxMessageEndpointType.OrganizationOwner),
            new InboxMessageEndpoint(InboxMessageEndpointType.Position, "engineer"),
            ThreadId,
            InboxPriority.Critical,
            decidedAt,
            null,
            InboxReadState.Unread,
            InboxResponseState.NotApplicable,
            metadata);

        Assert.Equal(RequestId, item.Approval!.RequestId);
        Assert.Equal(DecisionId, item.Approval.DecisionMessageId);
        Assert.Equal(InboxApprovalState.Approved, item.Approval.State);
    }

    [Fact]
    public void Endpoint_variants_reject_incoherent_identifiers()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new InboxMessageEndpoint(InboxMessageEndpointType.Position));
        Assert.Throws<ArgumentException>(() =>
            new InboxMessageEndpoint(
                InboxMessageEndpointType.OrganizationOwner,
                "delivery-lead"));
    }

    [Fact]
    public void Approval_metadata_rejects_incoherent_state()
    {
        Assert.Throws<ArgumentException>(() => new InboxApprovalMetadata(
            RequestId,
            "deployment.production",
            "production-change",
            InboxApprovalState.Pending,
            canDecide: true,
            DecisionId,
            SentAt));

        Assert.Throws<ArgumentException>(() => new InboxApprovalMetadata(
            RequestId,
            "deployment.production",
            "production-change",
            InboxApprovalState.Approved,
            canDecide: false));

        Assert.Throws<ArgumentException>(() => new InboxApprovalMetadata(
            RequestId,
            "deployment.production",
            "production-change",
            InboxApprovalState.Expired,
            canDecide: true));
    }

    [Fact]
    public void Inbox_item_rejects_missing_or_unexpected_approval_metadata()
    {
        var approval = CreateApprovalRequest().Approval;

        Assert.Throws<ArgumentException>(() => CreateItem(
            InboxMessageType.ApprovalRequest,
            approval: null));
        Assert.Throws<ArgumentException>(() => CreateItem(
            InboxMessageType.Directive,
            approval));
    }

    [Fact]
    public void Inbox_item_rejects_invalid_identity_time_and_enum_values()
    {
        Assert.ThrowsAny<ArgumentException>(() => CreateItem(
            InboxMessageType.Directive,
            messageId: Guid.Empty));
        Assert.ThrowsAny<ArgumentException>(() => CreateItem(
            InboxMessageType.Directive,
            threadId: Guid.Empty));
        Assert.ThrowsAny<ArgumentException>(() => CreateItem(
            InboxMessageType.Directive,
            sentAtUtc: new DateTimeOffset(2026, 8, 4, 9, 30, 0, TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentException>(() => CreateItem(
            InboxMessageType.Directive,
            deadlineAtUtc: SentAt.AddSeconds(-1)));
        Assert.Throws<ArgumentException>(() => CreateItem(
            InboxMessageType.Directive,
            isExpired: true));
        Assert.Throws<ArgumentException>(() => CreateItem(
            InboxMessageType.Directive,
            deadlineAtUtc: DeadlineAt,
            reminderState: InboxReminderState.Sent));
        Assert.Throws<ArgumentException>(() => CreateItem(
            InboxMessageType.Directive,
            deadlineAtUtc: DeadlineAt,
            lastReminderAtUtc: DeadlineAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateItem(
            (InboxMessageType)999));
    }

    [Fact]
    public void Public_inbox_contract_surface_does_not_expose_runtime_types()
    {
        var contractTypes = typeof(InboxItem).Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace == typeof(InboxItem).Namespace)
            .ToArray();
        var exposedTypes = contractTypes
            .SelectMany(PublicSurfaceTypes)
            .Where(type => type.Namespace is not null)
            .Select(type => type.Namespace!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.DoesNotContain(exposedTypes, value => value.StartsWith("Hive.Domain", StringComparison.Ordinal));
        Assert.DoesNotContain(exposedTypes, value => value.StartsWith("Hive.Infrastructure", StringComparison.Ordinal));
        Assert.DoesNotContain(exposedTypes, value => value.StartsWith("Hive.Api", StringComparison.Ordinal));
    }

    [Fact]
    public void Inbox_page_serializes_generation_staleness_and_cursor_pagination_metadata()
    {
        var lastEventAppliedAt = SentAt.AddMinutes(-1);
        var page = new InboxPage(
            SentAt.AddMinutes(1),
            lastEventAppliedAt,
            pageSize: 25,
            nextCursor: "deadline-priority-message-item:v1",
            [CreateApprovalRequest()]);

        var json = JsonSerializer.SerializeToElement(page);

        Assert.Equal(
            SentAt.AddMinutes(1),
            json.GetProperty("generated_at_utc").GetDateTimeOffset());
        Assert.Equal(
            lastEventAppliedAt,
            json.GetProperty("last_event_applied_at_utc").GetDateTimeOffset());
        Assert.Equal(25, json.GetProperty("page_size").GetInt32());
        Assert.Equal(
            "deadline-priority-message-item:v1",
            json.GetProperty("next_cursor").GetString());
        Assert.Equal(
            RequestId,
            Assert.Single(json.GetProperty("items").EnumerateArray())
                .GetProperty("message_id")
                .GetGuid());
    }

    [Fact]
    public void Inbox_detail_serializes_the_item_with_projection_metadata()
    {
        var content = new InboxApprovalRequestMessageContent(
            "deployment.production",
            "Production incident");
        var response = new InboxItemResponse(
            SentAt.AddMinutes(1),
            lastEventAppliedAtUtc: null,
            CreateApprovalRequest(),
            draftText: "Pending rationale",
            content);

        var json = JsonSerializer.SerializeToElement(response);

        Assert.Equal(
            SentAt.AddMinutes(1),
            json.GetProperty("generated_at_utc").GetDateTimeOffset());
        Assert.Equal(
            JsonValueKind.Null,
            json.GetProperty("last_event_applied_at_utc").ValueKind);
        Assert.Equal(
            RequestId,
            json.GetProperty("item").GetProperty("message_id").GetGuid());
        Assert.Equal("Pending rationale", json.GetProperty("draft_text").GetString());
        Assert.Equal(
            "deployment.production",
            json.GetProperty("content").GetProperty("action").GetString());
    }

    [Theory]
    [MemberData(nameof(CanonicalContent))]
    public void Canonical_content_serializes_as_a_closed_discriminated_shape(
        InboxMessageContent content,
        string expectedType,
        string[] expectedProperties)
    {
        var json = JsonSerializer.SerializeToElement(content);

        Assert.Equal(expectedType, json.GetProperty("type").GetString());
        Assert.Equal(
            expectedProperties.Order(StringComparer.Ordinal),
            json.EnumerateObject().Select(static property => property.Name)
                .Order(StringComparer.Ordinal));
        if (content is InboxReportMessageContent)
        {
            Assert.Equal("progress", json.GetProperty("kind").GetString());
        }
    }

    [Fact]
    public void Inbox_detail_rejects_content_for_a_different_message_type()
    {
        Assert.Throws<ArgumentException>(() => new InboxItemResponse(
            SentAt,
            SentAt,
            CreateApprovalRequest(),
            content: new InboxMemoMessageContent("Not an approval request")));
    }

    [Fact]
    public void Interaction_response_serializes_public_state_without_runtime_types()
    {
        var response = new InboxInteractionResponse(
            SentAt.AddMinutes(2),
            SentAt.AddMinutes(1),
            "delivery-lead/cf2b086f-dd04-445f-a68e-8e40a75530b9",
            InboxReadState.Read,
            InboxResponseState.InProgress,
            "Pending rationale",
            SentAt.AddMinutes(2));

        var json = JsonSerializer.SerializeToElement(response);

        Assert.Equal("Read", json.GetProperty("read_state").GetString());
        Assert.Equal("InProgress", json.GetProperty("response_state").GetString());
        Assert.Equal("Pending rationale", json.GetProperty("draft_text").GetString());
        Assert.Equal(
            SentAt.AddMinutes(2),
            json.GetProperty("interaction_updated_at_utc").GetDateTimeOffset());
    }

    [Fact]
    public void Inbox_page_rejects_invalid_metadata_and_unbounded_or_null_items()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InboxPage(
            SentAt,
            SentAt,
            pageSize: 0,
            nextCursor: null,
            []));
        Assert.Throws<ArgumentException>(() => new InboxPage(
            SentAt,
            SentAt,
            pageSize: 1,
            nextCursor: null,
            [CreateApprovalRequest(), CreateApprovalRequest()]));
        Assert.Throws<ArgumentException>(() => new InboxPage(
            SentAt,
            SentAt,
            pageSize: 1,
            nextCursor: " ",
            [CreateApprovalRequest()]));
        Assert.Throws<ArgumentNullException>(() => new InboxItemResponse(
            SentAt,
            SentAt,
            item: null!));
    }

    private static InboxItem CreateApprovalRequest() => new(
        "delivery-lead/cf2b086f-dd04-445f-a68e-8e40a75530b9",
        RequestId,
        "delivery-lead",
        InboxMessageType.ApprovalRequest,
        new InboxMessageEndpoint(InboxMessageEndpointType.Position, "engineer"),
        new InboxMessageEndpoint(InboxMessageEndpointType.OrganizationOwner),
        ThreadId,
        InboxPriority.Critical,
        SentAt,
        DeadlineAt,
        InboxReadState.Unread,
        InboxResponseState.NotApplicable,
        new InboxApprovalMetadata(
            RequestId,
            "deployment.production",
            "production-change",
            InboxApprovalState.Pending,
            canDecide: true));

    private static InboxItem CreateItem(
        InboxMessageType type,
        InboxApprovalMetadata? approval = null,
        Guid? messageId = null,
        Guid? threadId = null,
        DateTimeOffset? sentAtUtc = null,
        DateTimeOffset? deadlineAtUtc = null,
        bool isExpired = false,
        InboxReminderState reminderState = InboxReminderState.None,
        DateTimeOffset? lastReminderAtUtc = null) =>
        new(
            "engineer/c15095cd-7f12-4099-a15b-995275dfb3d0",
            messageId ?? DecisionId,
            "engineer",
            type,
            new InboxMessageEndpoint(InboxMessageEndpointType.Position, "delivery-lead"),
            new InboxMessageEndpoint(InboxMessageEndpointType.Position, "engineer"),
            threadId ?? ThreadId,
            InboxPriority.High,
            sentAtUtc ?? SentAt,
            deadlineAtUtc,
            InboxReadState.Unread,
            InboxResponseState.AwaitingResponse,
            approval,
            isExpired,
            reminderState,
            lastReminderAtUtc);

    private static IEnumerable<Type> PublicSurfaceTypes(Type type)
    {
        yield return type;
        foreach (var property in type.GetProperties(
                     BindingFlags.Instance |
                     BindingFlags.Public |
                     BindingFlags.DeclaredOnly))
        {
            yield return property.PropertyType;
            foreach (var argument in property.PropertyType.GetGenericArguments())
            {
                yield return argument;
            }
        }

        foreach (var constructor in type.GetConstructors())
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
                foreach (var argument in parameter.ParameterType.GetGenericArguments())
                {
                    yield return argument;
                }
            }
        }
    }
}
