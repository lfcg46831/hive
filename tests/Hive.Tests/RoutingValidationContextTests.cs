using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class RoutingValidationContextTests
{
    private static readonly OrganizationId Org = OrganizationId.From("engineering-delivery");
    private static readonly DateTimeOffset SentAt =
        new(2026, 6, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ForMessage_captures_identity_endpoints_and_thread_without_governance()
    {
        var directive = SampleDirective(out var from, out var to, out var thread, out var id);

        var context = RoutingValidationContext.ForMessage(directive);

        Assert.Equal(id, context.MessageId);
        Assert.Equal(Org, context.OrganizationId);
        Assert.Equal(from, context.Sender);
        Assert.Equal(to, context.Recipient);
        Assert.Equal(thread, context.Thread);
        Assert.Null(context.Policy);
        Assert.Null(context.AppliedVersion);
        Assert.Null(context.ResolvedApprover);
    }

    [Fact]
    public void WithGovernance_enriches_policy_version_and_resolved_approver()
    {
        var directive = SampleDirective(out _, out _, out _, out _);
        var policy = ApprovalPolicyRef.From("budget-approval");
        var version = ApprovalPolicyVersion.Create("v3", "deadbeef");
        var approver = new OrganizationOwnerEndpointRef();

        var context = RoutingValidationContext
            .ForMessage(directive)
            .WithGovernance(policy, version, approver);

        Assert.Equal(policy, context.Policy);
        Assert.Equal(version, context.AppliedVersion);
        Assert.Equal(approver, context.ResolvedApprover);
        // Base routing facts are preserved.
        Assert.Equal(directive.Id, context.MessageId);
        Assert.Equal(directive.From, context.Sender);
    }

    [Fact]
    public void Required_fields_reject_null()
    {
        Assert.Throws<ArgumentNullException>(() => RoutingValidationContext.ForMessage(null!));
        Assert.Throws<ArgumentNullException>(
            () => new RoutingValidationContext(
                null!,
                Org,
                new OrganizationOwnerEndpointRef(),
                new OrganizationOwnerEndpointRef(),
                ThreadId.New()));
    }

    private static Directive SampleDirective(
        out EndpointRef from,
        out EndpointRef to,
        out ThreadId thread,
        out MessageId id)
    {
        from = new PositionEndpointRef(PositionId.From("delivery-lead"));
        to = new PositionEndpointRef(PositionId.From("engineer"));
        thread = ThreadId.New();
        id = MessageId.New();

        return new Directive(
            id,
            Org,
            from,
            to,
            thread,
            Priority.Normal,
            1,
            SentAt,
            null,
            DirectiveId.New(),
            null,
            "Deliver the assigned work",
            "Use the current organizational context");
    }
}
