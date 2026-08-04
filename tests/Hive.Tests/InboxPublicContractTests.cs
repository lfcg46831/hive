using System.Reflection;
using System.Text.Json;
using Hive.Contracts.Inbox;

namespace Hive.Tests;

public sealed class InboxPublicContractTests
{
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
        DateTimeOffset? deadlineAtUtc = null) =>
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
            approval);

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
