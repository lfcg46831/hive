using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;

namespace Hive.Tests;

/// <summary>
/// Acceptance suite for US-F0-04-T11. It exercises the full vertical/governance routing matrix
/// end-to-end through the inbox entry point (<see cref="RoutingAdmissionValidator"/> from
/// US-F0-04-T09), proving each acceptance criterion of US-F0-04: valid directive, directive with a
/// level jump, valid report/escalation, report to the wrong level, top escalation to the
/// <c>OrganizationOwner</c>, valid approval, decision by an unauthorized approver, nonexistent
/// organization, nonexistent source/destination position, and registry unavailability.
/// </summary>
/// <remarks>
/// The decisive contract under test is §9.8: only <em>confirmed</em> absences (organization or
/// position that the registry/authority reports as missing) become <see cref="RejectionReason.InvalidRoute"/>
/// rejections, while transient technical failures (registry/authority/log unavailability,
/// cancellation) keep surfacing as exceptions subject to retry rather than as routing rejections.
/// </remarks>
public sealed class RoutingValidationAcceptanceTests
{
    private static readonly OrganizationId Org = OrganizationId.From("engineering-delivery");
    private static readonly OrganizationId UnknownOrg = OrganizationId.From("ghost-organization");
    private static readonly ApprovalPolicyRef Policy = ApprovalPolicyRef.From("requires-human-approval");
    private static readonly ApprovalPolicyVersion Version =
        ApprovalPolicyVersion.Create("v3", "sha256:abc123");
    private static readonly DateTimeOffset Now = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly ThreadId Thread = ThreadId.New();

    private static readonly PositionEndpointRef Ceo = Position("ceo");
    private static readonly PositionEndpointRef DeliveryLead = Position("delivery-lead");
    private static readonly PositionEndpointRef Engineer = Position("engineer");

    // ---- Directives: only superior -> direct subordinate is accepted ----

    [Fact]
    public async Task Valid_directive_to_direct_subordinate_is_admitted()
    {
        var admission = await Admission().AdmitAsync(Directive(DeliveryLead, Engineer));

        AssertAdmitted(admission);
    }

    [Fact]
    public async Task Directive_with_level_jump_is_rejected_as_invalid_route()
    {
        // The CEO is two levels above the engineer: a directive that skips the delivery lead must
        // not be accepted into the inbox.
        var admission = await Admission().AdmitAsync(Directive(Ceo, Engineer));

        AssertInvalidRoute(
            admission,
            new ValidationError("direct-subordinate-required", "to.positionId", RejectionReason.InvalidRoute));
    }

    // ---- Reports and escalations: only subordinate -> direct superior is accepted ----

    [Fact]
    public async Task Valid_report_to_direct_superior_is_admitted()
    {
        var admission = await Admission().AdmitAsync(Report(Engineer, DeliveryLead));

        AssertAdmitted(admission);
    }

    [Fact]
    public async Task Valid_escalation_to_direct_superior_is_admitted()
    {
        var admission = await Admission().AdmitAsync(Escalation(Engineer, DeliveryLead));

        AssertAdmitted(admission);
    }

    [Fact]
    public async Task Report_to_the_wrong_level_is_rejected_as_invalid_route()
    {
        // The engineer's direct superior is the delivery lead, not the CEO: a report that climbs
        // past the direct superior is an invalid upward route.
        var admission = await Admission().AdmitAsync(Report(Engineer, Ceo));

        AssertInvalidRoute(
            admission,
            new ValidationError("direct-superior-required", "to.positionId", RejectionReason.InvalidRoute));
    }

    [Fact]
    public async Task Top_escalation_from_root_leadership_to_organization_owner_is_admitted()
    {
        // Root-unit leadership has no direct superior; its escalations are routed to the
        // OrganizationOwner / kill switch instead of being treated as a level jump.
        var admission = await Admission().AdmitAsync(
            Escalation(Ceo, new OrganizationOwnerEndpointRef()));

        AssertAdmitted(admission);
    }

    // ---- Governance: approvals only flow between requester and authorized approver ----

    [Fact]
    public async Task Valid_approval_request_to_authorized_approver_is_admitted()
    {
        var admission = await Admission(
                authority: ResolvingAuthority(ApproverResolution.Resolved(Policy, DeliveryLead, Version)))
            .AdmitAsync(ApprovalRequest(Engineer, DeliveryLead));

        AssertAdmitted(admission);
    }

    [Fact]
    public async Task Valid_approval_decision_from_authorized_approver_is_admitted()
    {
        var requestId = MessageId.New();
        var admission = await Admission(requestLog: RecordingLog(Record(requestId)))
            .AdmitAsync(ApprovalDecision(requestId, DeliveryLead, Engineer));

        AssertAdmitted(admission);
    }

    [Fact]
    public async Task Decision_from_unauthorized_approver_is_rejected_as_unauthorized()
    {
        var requestId = MessageId.New();

        // The authorized approver recorded at acceptance is the delivery lead; a decision arriving
        // from a different position is rejected as Unauthorized, not InvalidRoute.
        var admission = await Admission(requestLog: RecordingLog(Record(requestId)))
            .AdmitAsync(ApprovalDecision(requestId, Position("security-officer"), Engineer));

        Assert.False(admission.IsAdmitted);
        Assert.Equal(
            [new ValidationError("unauthorized", "$", RejectionReason.Unauthorized)],
            admission.Rejection!.PublicResult.Errors);
    }

    // ---- Confirmed absences map to InvalidRoute (never an exception to the caller) ----

    [Fact]
    public async Task Unknown_organization_is_invalid_route_and_never_throws()
    {
        var admission = await Admission().AdmitAsync(
            Directive(UnknownOrg, DeliveryLead, Engineer));

        AssertInvalidRoute(
            admission,
            new ValidationError("organization-not-found", "organizationId", RejectionReason.InvalidRoute));
    }

    [Fact]
    public async Task Unknown_source_position_is_invalid_route()
    {
        var admission = await Admission().AdmitAsync(
            Directive(Position("ghost-source"), Engineer));

        AssertInvalidRoute(
            admission,
            new ValidationError("position-not-found", "from.positionId", RejectionReason.InvalidRoute));
    }

    [Fact]
    public async Task Unknown_destination_position_is_invalid_route()
    {
        var admission = await Admission().AdmitAsync(
            Directive(DeliveryLead, Position("ghost-destination")));

        AssertInvalidRoute(
            admission,
            new ValidationError("position-not-found", "to.positionId", RejectionReason.InvalidRoute));
    }

    [Fact]
    public async Task Unknown_source_and_destination_positions_are_aggregated_as_invalid_route()
    {
        var admission = await Admission().AdmitAsync(
            Directive(Position("ghost-source"), Position("ghost-destination")));

        Assert.False(admission.IsAdmitted);
        var rejection = admission.Rejection!;

        // The audit trail keeps both fine-grained absences; the public surface collapses to §9.8.
        Assert.Equal(
            [
                new ValidationError("position-not-found", "from.positionId", RejectionReason.InvalidRoute),
                new ValidationError("position-not-found", "to.positionId", RejectionReason.InvalidRoute),
            ],
            rejection.AuditResult.Errors);
        Assert.Equal(
            [new ValidationError("invalid-route", "$", RejectionReason.InvalidRoute)],
            rejection.PublicResult.Errors);
    }

    // ---- Technical failures stay exceptional / retryable, not rejections ----

    [Fact]
    public async Task Registry_unavailability_propagates_as_a_retryable_exception()
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

    [Fact]
    public async Task Approval_authority_unavailability_propagates_as_a_retryable_exception()
    {
        var failure = new InvalidOperationException("Approval authority unavailable.");
        var admission = new RoutingAdmissionValidator(
            new DirectiveRoutingValidator(SampleRelations()),
            new ReportRoutingValidator(SampleRelations()),
            new EscalationRoutingValidator(SampleRelations()),
            new ApprovalRoutingValidator(
                new FailingAuthority(failure),
                RecordingLog(null),
                new FixedTimeProvider(Now)));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await admission.AdmitAsync(ApprovalRequest(Engineer, DeliveryLead)));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task Cancellation_propagates_before_validation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Admission().AdmitAsync(
                Directive(DeliveryLead, Engineer),
                cancellation.Token));
    }

    // ---- helpers ----

    private static void AssertAdmitted(RoutingAdmission admission)
    {
        Assert.True(admission.IsAdmitted);
        Assert.Null(admission.Rejection);
    }

    private static void AssertInvalidRoute(RoutingAdmission admission, ValidationError expectedAuditError)
    {
        Assert.False(admission.IsAdmitted);
        var rejection = admission.Rejection!;

        // Detailed reason is preserved for the audit trail...
        Assert.Equal([expectedAuditError], rejection.AuditResult.Errors);
        // ...while the result returned to the sender is sanitized to a single coarse reason (§9.8).
        Assert.Equal(
            [new ValidationError("invalid-route", "$", RejectionReason.InvalidRoute)],
            rejection.PublicResult.Errors);
    }

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
        Directive(Org, from, to);

    private static Directive Directive(OrganizationId organizationId, EndpointRef from, EndpointRef to) =>
        new(
            MessageId.New(),
            organizationId,
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

    private static ApprovalDecision ApprovalDecision(MessageId requestId, EndpointRef from, EndpointRef to) =>
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
