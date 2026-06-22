using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;

namespace Hive.Tests;

public sealed class RoutingAdmissionValidatorTests
{
    private static readonly OrganizationId Org = OrganizationId.From("engineering-delivery");
    private static readonly ApprovalPolicyRef Policy = ApprovalPolicyRef.From("requires-human-approval");
    private static readonly ApprovalPolicyVersion Version =
        ApprovalPolicyVersion.Create("v3", "sha256:abc123");
    private static readonly DateTimeOffset Now = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly ThreadId Thread = ThreadId.New();

    private static readonly PositionEndpointRef Ceo = Position("ceo");
    private static readonly PositionEndpointRef DeliveryLead = Position("delivery-lead");
    private static readonly PositionEndpointRef Engineer = Position("engineer");

    // ---- admission of valid routing ----

    [Fact]
    public async Task Valid_directive_is_admitted()
    {
        var admission = await Admission().AdmitAsync(Directive(DeliveryLead, Engineer));

        Assert.True(admission.IsAdmitted);
        Assert.Null(admission.Rejection);
    }

    [Fact]
    public async Task Valid_report_is_admitted()
    {
        var admission = await Admission().AdmitAsync(Report(Engineer, DeliveryLead));

        Assert.True(admission.IsAdmitted);
        Assert.Null(admission.Rejection);
    }

    [Fact]
    public async Task Valid_escalation_is_admitted()
    {
        var admission = await Admission().AdmitAsync(Escalation(Engineer, DeliveryLead));

        Assert.True(admission.IsAdmitted);
        Assert.Null(admission.Rejection);
    }

    [Fact]
    public async Task Valid_approval_request_is_admitted()
    {
        var admission = await Admission(
                authority: ResolvingAuthority(ApproverResolution.Resolved(Policy, DeliveryLead, Version)))
            .AdmitAsync(ApprovalRequest(Engineer, DeliveryLead));

        Assert.True(admission.IsAdmitted);
        Assert.Null(admission.Rejection);
    }

    [Fact]
    public async Task Valid_approval_decision_is_admitted()
    {
        var requestId = MessageId.New();
        var admission = await Admission(requestLog: RecordingLog(Record(requestId)))
            .AdmitAsync(ApprovalDecision(requestId, DeliveryLead, Engineer));

        Assert.True(admission.IsAdmitted);
        Assert.Null(admission.Rejection);
    }

    // ---- rejection before acceptance ----

    [Fact]
    public async Task Directive_with_level_jump_is_rejected_with_sanitized_public_result()
    {
        var directive = Directive(Ceo, Engineer);

        var admission = await Admission().AdmitAsync(directive);

        Assert.False(admission.IsAdmitted);
        var rejection = Assert.IsType<RoutingRejection>(admission.Rejection);

        // The audit trail keeps the fine-grained reason; the public surface is sanitized to §9.8.
        Assert.Equal(
            [new ValidationError("direct-subordinate-required", "to.positionId", RejectionReason.InvalidRoute)],
            rejection.AuditResult.Errors);
        Assert.Equal(
            [new ValidationError("invalid-route", "$", RejectionReason.InvalidRoute)],
            rejection.PublicResult.Errors);
    }

    [Fact]
    public async Task Rejection_context_carries_the_original_message_identity()
    {
        var directive = Directive(Ceo, Engineer);

        var admission = await Admission().AdmitAsync(directive);

        var context = admission.Rejection!.Context;
        Assert.Equal(directive.Id, context.MessageId);
        Assert.Equal(Org, context.OrganizationId);
        Assert.Equal(directive.From, context.Sender);
        Assert.Equal(directive.To, context.Recipient);
        Assert.Equal(directive.Thread, context.Thread);
    }

    [Fact]
    public async Task Decision_from_unauthorized_approver_is_rejected()
    {
        var requestId = MessageId.New();
        var admission = await Admission(requestLog: RecordingLog(Record(requestId)))
            .AdmitAsync(ApprovalDecision(requestId, Position("security-officer"), Engineer));

        Assert.False(admission.IsAdmitted);
        Assert.Equal(
            [new ValidationError("unauthorized", "$", RejectionReason.Unauthorized)],
            admission.Rejection!.PublicResult.Errors);
    }

    // ---- out of vertical/governance scope ----

    [Fact]
    public async Task Message_without_routing_rule_is_admitted_without_querying_validators()
    {
        var blowUp = new InvalidOperationException("Validators must not be queried.");
        var admission = new RoutingAdmissionValidator(
            new DirectiveRoutingValidator(new FailingRelations(blowUp)),
            new ReportRoutingValidator(new FailingRelations(blowUp)),
            new EscalationRoutingValidator(new FailingRelations(blowUp)),
            new ApprovalRoutingValidator(new FailingAuthority(blowUp), new FailingLog(blowUp)));

        var result = await admission.AdmitAsync(Memo(Engineer, DeliveryLead));

        Assert.True(result.IsAdmitted);
        Assert.Null(result.Rejection);
    }

    // ---- technical failures stay exceptional ----

    [Fact]
    public async Task Cancellation_propagates_before_dispatch()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var admission = new RoutingAdmissionValidator(
            new DirectiveRoutingValidator(new FailingRelations(
                new InvalidOperationException("Validators must not be queried."))),
            new ReportRoutingValidator(SampleRelations()),
            new EscalationRoutingValidator(SampleRelations()),
            DefaultApproval());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await admission.AdmitAsync(Directive(DeliveryLead, Engineer), cancellation.Token));
    }

    [Fact]
    public async Task Unexpected_validator_failure_propagates()
    {
        var failure = new InvalidOperationException("Registry unavailable.");
        var admission = new RoutingAdmissionValidator(
            new DirectiveRoutingValidator(new FailingRelations(failure)),
            new ReportRoutingValidator(SampleRelations()),
            new EscalationRoutingValidator(SampleRelations()),
            DefaultApproval());

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await admission.AdmitAsync(Directive(DeliveryLead, Engineer)));

        Assert.Same(failure, thrown);
    }

    // ---- API misuse ----

    [Fact]
    public void Missing_dependencies_are_api_misuse()
    {
        Assert.Throws<ArgumentNullException>(() => new RoutingAdmissionValidator(
            null!,
            new ReportRoutingValidator(SampleRelations()),
            new EscalationRoutingValidator(SampleRelations()),
            DefaultApproval()));
        Assert.Throws<ArgumentNullException>(() => new RoutingAdmissionValidator(
            new DirectiveRoutingValidator(SampleRelations()),
            null!,
            new EscalationRoutingValidator(SampleRelations()),
            DefaultApproval()));
        Assert.Throws<ArgumentNullException>(() => new RoutingAdmissionValidator(
            new DirectiveRoutingValidator(SampleRelations()),
            new ReportRoutingValidator(SampleRelations()),
            null!,
            DefaultApproval()));
        Assert.Throws<ArgumentNullException>(() => new RoutingAdmissionValidator(
            new DirectiveRoutingValidator(SampleRelations()),
            new ReportRoutingValidator(SampleRelations()),
            new EscalationRoutingValidator(SampleRelations()),
            null!));
    }

    [Fact]
    public async Task Missing_message_is_api_misuse()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await Admission().AdmitAsync(null!));
    }

    // ---- helpers ----

    private static RoutingAdmissionValidator Admission(
        IApprovalAuthority? authority = null,
        IApprovalRequestLog? requestLog = null) =>
        new(
            new DirectiveRoutingValidator(SampleRelations()),
            new ReportRoutingValidator(SampleRelations()),
            new EscalationRoutingValidator(SampleRelations()),
            new ApprovalRoutingValidator(
                authority ?? ResolvingAuthority(ApproverResolution.Resolved(Policy, DeliveryLead, Version)),
                requestLog ?? RecordingLog(null),
                new FixedTimeProvider(Now)));

    private static ApprovalRoutingValidator DefaultApproval() =>
        new(
            ResolvingAuthority(ApproverResolution.Resolved(Policy, DeliveryLead, Version)),
            RecordingLog(null),
            new FixedTimeProvider(Now));

    private static Directive Directive(EndpointRef from, EndpointRef to) =>
        new(
            MessageId.New(),
            Org,
            from,
            to,
            Thread,
            Priority.High,
            1,
            Now.AddHours(-1),
            null,
            DirectiveId.New(),
            null,
            "Ship the release",
            "The candidate passed verification.");

    private static Report Report(EndpointRef from, EndpointRef to) =>
        new(
            MessageId.New(),
            Org,
            from,
            to,
            Thread,
            Priority.Normal,
            1,
            Now.AddHours(-1),
            null,
            DirectiveId.New(),
            ReportKind.Progress,
            "Half of the tasks are done.");

    private static Escalation Escalation(EndpointRef from, EndpointRef to) =>
        new(
            MessageId.New(),
            Org,
            from,
            to,
            Thread,
            Priority.High,
            1,
            Now.AddHours(-1),
            null,
            "Delivery is blocked",
            "A dependency requires a decision outside the source position's authority.",
            ["Wait for the dependency", "Use the fallback"]);

    private static ApprovalRequest ApprovalRequest(EndpointRef from, EndpointRef to) =>
        new(
            MessageId.New(),
            Org,
            from,
            to,
            Thread,
            Priority.High,
            1,
            Now.AddHours(-1),
            null,
            "publish-final",
            "The release candidate passed verification.",
            Policy);

    private static ApprovalDecision ApprovalDecision(
        MessageId requestId,
        EndpointRef from,
        EndpointRef to) =>
        new(
            MessageId.New(),
            Org,
            from,
            to,
            Thread,
            Priority.High,
            1,
            Now.AddMinutes(-30),
            null,
            requestId,
            approved: true,
            reason: null);

    private static Memo Memo(EndpointRef from, EndpointRef to) =>
        new(
            MessageId.New(),
            Org,
            from,
            to,
            Thread,
            Priority.Normal,
            1,
            Now.AddHours(-1),
            null,
            "FYI: the dependency landed.");

    private static ApprovalRequestRecord Record(MessageId requestId) =>
        new(
            requestId,
            Org,
            Engineer.PositionId,
            DeliveryLead,
            Version,
            Thread,
            null,
            MessageState.Accepted);

    private static PositionEndpointRef Position(string value) => new(PositionId.From(value));

    private static IOrganizationRelations SampleRelations() =>
        new MaterializedOrganizationRelations(
            OrganizationRelationsSnapshot
                .CreateBuilder(Org, new OrganizationOwnerEndpointRef())
                .AddPosition(PositionId.From("ceo"), UnitId.From("root"))
                .AddPosition(
                    PositionId.From("delivery-lead"),
                    UnitId.From("delivery"),
                    PositionId.From("ceo"))
                .AddPosition(
                    PositionId.From("engineer"),
                    UnitId.From("delivery"),
                    PositionId.From("delivery-lead"))
                .Build());

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

    private sealed class FailingRelations(Exception failure) : IOrganizationRelations
    {
        public ValueTask<PositionId?> GetDirectSuperiorAsync(
            OrganizationId organizationId,
            PositionId positionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<PositionId?>(failure);

        public ValueTask<IReadOnlyCollection<PositionId>> GetDirectSubordinatesAsync(
            OrganizationId organizationId,
            PositionId positionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IReadOnlyCollection<PositionId>>(failure);

        public ValueTask<PositionId> GetRootUnitLeadershipAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<PositionId>(failure);

        public ValueTask<OrganizationOwnerEndpointRef> GetOrganizationOwnerAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<OrganizationOwnerEndpointRef>(failure);

        public ValueTask<UnitId?> GetUnitOfPositionAsync(
            OrganizationId organizationId,
            PositionId positionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<UnitId?>(failure);
    }

    private sealed class FailingAuthority(Exception failure) : IApprovalAuthority
    {
        public ValueTask<ApproverResolution> ResolveApproverAsync(
            ApprovalAuthorityQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ApproverResolution>(failure);
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
