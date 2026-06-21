using System.Reflection;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class MessageContractValidatorTests
{
    private static readonly DateTimeOffset SentAt =
        new(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly MessageContractValidator _validator = new();
    private readonly TrackingValidationContext _context = new();

    [Fact]
    public async Task Valid_materialized_message_passes_structural_validation()
    {
        var result = await _validator.ValidateAsync(CreateMemo(), _context);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Missing_materialized_message_is_a_controlled_validation_error()
    {
        var result = await _validator.ValidateAsync(null, _context);

        Assert.Equal(
            [new ValidationError("materialization-failed", "$", RejectionReason.InvalidContract)],
            result.Errors);
        Assert.Equal(0, _context.LookupCount);
    }

    [Fact]
    public async Task Unsupported_schema_version_gates_later_phases()
    {
        var message = CreateMemo(schemaVersion: 2);

        var result = await _validator.ValidateAsync(message, _context);

        Assert.Equal(
            [new ValidationError(
                "unsupported-schema-version",
                "schemaVersion",
                RejectionReason.UnsupportedSchemaVersion)],
            result.Errors);
        Assert.Equal(0, _context.LookupCount);
    }

    [Fact]
    public async Task Independent_missing_fields_are_aggregated_and_sorted()
    {
        var message = new Directive(
            MessageId.New(), OrganizationId.From("acme"), Position("lead"),
            Position("developer"), ThreadId.New(), Priority.Normal, 1, SentAt,
            null, DirectiveId.New(), null, " ", "");

        var result = await _validator.ValidateAsync(message, _context);

        Assert.Equal(
            [
                new("required-field", "context", RejectionReason.InvalidContract),
                new("required-field", "objective", RejectionReason.InvalidContract),
            ],
            result.Errors);
        Assert.Equal(0, _context.LookupCount);
    }

    [Fact]
    public async Task Empty_required_collection_is_rejected()
    {
        var message = new Escalation(
            MessageId.New(), OrganizationId.From("acme"), Position("developer"),
            Position("lead"), ThreadId.New(), Priority.High, 1, SentAt, null,
            "Blocked", "Context", []);

        var result = await _validator.ValidateAsync(message, _context);

        Assert.Equal(
            [new ValidationError(
                "required-field",
                "optionsConsidered",
                RejectionReason.InvalidContract)],
            result.Errors);
    }

    [Fact]
    public async Task Missing_organization_is_reported_without_throwing()
    {
        var message = SetProperty(CreateMemo(), nameof(OrgMessage.OrganizationId), null);

        var result = await _validator.ValidateAsync(message, _context);

        Assert.Contains(
            new ValidationError("required-field", "organizationId", RejectionReason.InvalidContract),
            result.Errors);
    }

    [Fact]
    public async Task Endpoint_variant_outside_catalog_matrix_is_invalid_route()
    {
        var message = new Memo(
            MessageId.New(), OrganizationId.From("acme"),
            new OrganizationOwnerEndpointRef(), Position("developer"),
            ThreadId.New(), Priority.Normal, 1, SentAt, null, "Update");

        var result = await _validator.ValidateAsync(message, _context);

        Assert.Equal(
            [new ValidationError("endpoint-not-allowed", "from", RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Fact]
    public async Task System_endpoint_must_use_the_catalog_producer()
    {
        var message = new Pulse(
            MessageId.New(), OrganizationId.From("acme"),
            new SystemEndpointRef(SystemEndpointKind.DomainEvents), Position("developer"),
            ThreadId.New(), Priority.Normal, 1, SentAt, null, "daily", "{}");

        var result = await _validator.ValidateAsync(message, _context);

        Assert.Equal(
            [new ValidationError("endpoint-not-allowed", "from", RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Fact]
    public async Task Position_endpoint_requires_its_nested_identity()
    {
        var endpoint = SetProperty(
            Position("lead"),
            nameof(PositionEndpointRef.PositionId),
            null);
        var message = new Memo(
            MessageId.New(), OrganizationId.From("acme"), endpoint,
            Position("developer"), ThreadId.New(), Priority.Normal, 1,
            SentAt, null, "Update");

        var result = await _validator.ValidateAsync(message, _context);

        Assert.Equal(
            [new ValidationError(
                "required-field",
                "from.positionId",
                RejectionReason.InvalidContract)],
            result.Errors);
    }

    [Fact]
    public async Task Undefined_system_endpoint_kind_is_a_controlled_error()
    {
        var endpoint = SetProperty(
            new SystemEndpointRef(SystemEndpointKind.Scheduler),
            nameof(SystemEndpointRef.Kind),
            (SystemEndpointKind)7);
        var message = new Pulse(
            MessageId.New(), OrganizationId.From("acme"), endpoint,
            Position("developer"), ThreadId.New(), Priority.Normal, 1,
            SentAt, null, "daily", "{}");

        var result = await _validator.ValidateAsync(message, _context);

        Assert.Equal(
            [new ValidationError(
                "invalid-system-endpoint",
                "from",
                RejectionReason.InvalidContract)],
            result.Errors);
    }

    [Fact]
    public async Task Undefined_priority_is_a_validation_error_not_an_exception()
    {
        var message = SetProperty(CreateMemo(), nameof(OrgMessage.Priority), (Priority)0);

        var result = await _validator.ValidateAsync(message, _context);

        Assert.Contains(
            new ValidationError("invalid-priority", "priority", RejectionReason.InvalidContract),
            result.Errors);
    }

    [Fact]
    public async Task Undefined_report_kind_is_a_validation_error_not_an_exception()
    {
        var report = new Report(
            MessageId.New(), OrganizationId.From("acme"), Position("developer"),
            Position("lead"), ThreadId.New(), Priority.Normal, 1, SentAt, null,
            DirectiveId.New(), ReportKind.Progress, "Progress");
        var message = SetProperty(report, nameof(Report.Kind), (ReportKind)0);

        var result = await _validator.ValidateAsync(message, _context);

        Assert.Contains(
            new ValidationError("invalid-report-kind", "kind", RejectionReason.InvalidContract),
            result.Errors);
        Assert.Equal(0, _context.LookupCount);
    }

    [Fact]
    public async Task Missing_policy_is_reported_without_throwing()
    {
        var request = CreateApprovalRequest();
        var message = SetProperty(request, nameof(ApprovalRequest.Policy), null);

        var result = await _validator.ValidateAsync(message, _context);

        Assert.Contains(
            new ValidationError("required-field", "policy", RejectionReason.InvalidContract),
            result.Errors);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Required_directive_identity_is_reported_without_throwing(
        bool reportReference)
    {
        OrgMessage message = reportReference
            ? SetProperty(
                new Report(
                    MessageId.New(), OrganizationId.From("acme"), Position("developer"),
                    Position("lead"), ThreadId.New(), Priority.Normal, 1, SentAt, null,
                    DirectiveId.New(), ReportKind.Progress, "Progress"),
                nameof(Report.AboutDirectiveId),
                null)
            : SetProperty(
                CreateDirective(
                    OrganizationId.From("acme"), ThreadId.New(),
                    DirectiveId.New(), null),
                nameof(Directive.DirectiveId),
                null);

        var result = await _validator.ValidateAsync(message, _context);

        Assert.Contains(
            new ValidationError(
                "required-field",
                reportReference ? "aboutDirectiveId" : "directiveId",
                RejectionReason.InvalidContract),
            result.Errors);
        Assert.Equal(0, _context.LookupCount);
    }

    [Fact]
    public async Task Lexically_invalid_policy_is_reported_without_throwing()
    {
        var policy = SetProperty(
            ApprovalPolicyRef.From("release"),
            nameof(ApprovalPolicyRef.Value),
            " release ");
        var request = SetProperty(CreateApprovalRequest(), nameof(ApprovalRequest.Policy), policy);

        var result = await _validator.ValidateAsync(request, _context);

        Assert.Contains(
            new ValidationError("invalid-policy", "policy.value", RejectionReason.InvalidContract),
            result.Errors);
    }

    [Fact]
    public async Task Valid_parent_directive_in_the_same_scope_is_accepted()
    {
        var organization = OrganizationId.From("acme");
        var thread = ThreadId.New();
        var parent = CreateDirective(organization, thread, DirectiveId.New(), null);
        var child = CreateDirective(organization, thread, DirectiveId.New(), parent.DirectiveId);
        _context.Directives.Add(parent.DirectiveId, parent);

        var result = await _validator.ValidateAsync(child, _context);

        Assert.True(result.IsValid);
        Assert.Equal(1, _context.LookupCount);
    }

    [Fact]
    public async Task Valid_report_target_in_the_same_scope_is_accepted()
    {
        var organization = OrganizationId.From("acme");
        var thread = ThreadId.New();
        var directive = CreateDirective(organization, thread, DirectiveId.New(), null);
        var report = new Report(
            MessageId.New(), organization, Position("developer"), Position("lead"),
            thread, Priority.Normal, 1, SentAt, null, directive.DirectiveId,
            ReportKind.Progress, "Progress");
        _context.Directives.Add(directive.DirectiveId, directive);

        var result = await _validator.ValidateAsync(report, _context);

        Assert.True(result.IsValid);
        Assert.Equal(1, _context.LookupCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_directive_reference_is_rejected(bool reportReference)
    {
        var reference = DirectiveId.New();
        OrgMessage message = reportReference
            ? new Report(
                MessageId.New(), OrganizationId.From("acme"), Position("developer"),
                Position("lead"), ThreadId.New(), Priority.Normal, 1, SentAt, null,
                reference, ReportKind.Progress, "Progress")
            : CreateDirective(
                OrganizationId.From("acme"), ThreadId.New(), DirectiveId.New(), reference);

        var result = await _validator.ValidateAsync(message, _context);

        Assert.Equal(
            [new ValidationError(
                "reference-not-found",
                reportReference ? "aboutDirectiveId" : "parentDirectiveId",
                RejectionReason.InvalidContract)],
            result.Errors);
    }

    [Fact]
    public async Task Directive_reference_must_share_organization()
    {
        var thread = ThreadId.New();
        var parent = CreateDirective(
            OrganizationId.From("other"), thread, DirectiveId.New(), null);
        var child = CreateDirective(
            OrganizationId.From("acme"), thread, DirectiveId.New(), parent.DirectiveId);
        _context.Directives.Add(parent.DirectiveId, parent);

        var result = await _validator.ValidateAsync(child, _context);

        Assert.Equal(
            [new ValidationError(
                "reference-organization-mismatch",
                "parentDirectiveId",
                RejectionReason.InvalidContract)],
            result.Errors);
    }

    [Fact]
    public async Task Directive_reference_must_share_thread()
    {
        var organization = OrganizationId.From("acme");
        var parent = CreateDirective(
            organization, ThreadId.New(), DirectiveId.New(), null);
        var child = CreateDirective(
            organization, ThreadId.New(), DirectiveId.New(), parent.DirectiveId);
        _context.Directives.Add(parent.DirectiveId, parent);

        var result = await _validator.ValidateAsync(child, _context);

        Assert.Equal(
            [new ValidationError(
                "reference-thread-mismatch",
                "parentDirectiveId",
                RejectionReason.InvalidContract)],
            result.Errors);
    }

    [Fact]
    public async Task Self_parenting_is_rejected_before_context_lookup()
    {
        var directive = CreateDirective(
            OrganizationId.From("acme"), ThreadId.New(), DirectiveId.New(), null);
        SetProperty(directive, nameof(Directive.ParentDirectiveId), directive.DirectiveId);

        var result = await _validator.ValidateAsync(directive, _context);

        Assert.Equal(
            [new ValidationError(
                "self-reference",
                "parentDirectiveId",
                RejectionReason.InvalidContract)],
            result.Errors);
        Assert.Equal(0, _context.LookupCount);
    }

    [Fact]
    public async Task Incoming_directive_cannot_introduce_a_lineage_cycle()
    {
        var organization = OrganizationId.From("acme");
        var thread = ThreadId.New();
        var incomingId = DirectiveId.New();
        var parentId = DirectiveId.New();
        var incoming = CreateDirective(organization, thread, incomingId, parentId);
        var parent = CreateDirective(organization, thread, parentId, incomingId);
        _context.Directives.Add(parentId, parent);

        var result = await _validator.ValidateAsync(incoming, _context);

        Assert.Equal(
            [new ValidationError(
                "reference-cycle",
                "parentDirectiveId",
                RejectionReason.InvalidContract)],
            result.Errors);
    }

    [Fact]
    public async Task Repeated_existing_ancestor_is_a_lineage_cycle()
    {
        var organization = OrganizationId.From("acme");
        var thread = ThreadId.New();
        var parentId = DirectiveId.New();
        var ancestorId = DirectiveId.New();
        var incoming = CreateDirective(organization, thread, DirectiveId.New(), parentId);
        var parent = CreateDirective(organization, thread, parentId, ancestorId);
        var ancestor = CreateDirective(organization, thread, ancestorId, parentId);
        _context.Directives.Add(parentId, parent);
        _context.Directives.Add(ancestorId, ancestor);

        var result = await _validator.ValidateAsync(incoming, _context);

        Assert.Equal(
            [new ValidationError(
                "reference-cycle",
                "parentDirectiveId",
                RejectionReason.InvalidContract)],
            result.Errors);
    }

    [Fact]
    public async Task Dependency_failure_is_not_converted_to_functional_rejection()
    {
        var context = new TrackingValidationContext
        {
            Failure = new InvalidOperationException("read model unavailable"),
        };
        var directive = CreateDirective(
            OrganizationId.From("acme"), ThreadId.New(),
            DirectiveId.New(), DirectiveId.New());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _validator.ValidateAsync(directive, context));

        Assert.Equal("read model unavailable", exception.Message);
    }

    [Fact]
    public async Task Context_cancellation_is_not_converted_to_functional_rejection()
    {
        var context = new TrackingValidationContext
        {
            Failure = new OperationCanceledException("cancelled"),
        };
        var directive = CreateDirective(
            OrganizationId.From("acme"), ThreadId.New(),
            DirectiveId.New(), DirectiveId.New());

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await _validator.ValidateAsync(directive, context));
    }

    [Fact]
    public async Task Every_canonical_message_type_satisfies_its_catalog_contract()
    {
        var organization = OrganizationId.From("acme");
        var thread = ThreadId.New();
        var directive = CreateDirective(organization, thread, DirectiveId.New(), null);
        _context.Directives.Add(directive.DirectiveId, directive);
        var position = Position("developer");
        var lead = Position("lead");
        var owner = new OrganizationOwnerEndpointRef();
        var messages = new OrgMessage[]
        {
            directive,
            new Report(
                MessageId.New(), organization, position, lead, thread, Priority.Normal,
                1, SentAt, null, directive.DirectiveId, ReportKind.Done, "Done"),
            new Escalation(
                MessageId.New(), organization, position, lead, thread, Priority.High,
                1, SentAt, null, "Blocked", "Context", ["Escalate"]),
            new Memo(
                MessageId.New(), organization, position, lead, thread, Priority.Normal,
                1, SentAt, null, "Update"),
            new PeerRequest(
                MessageId.New(), organization, position, lead, thread, Priority.Normal,
                1, SentAt, null, "Review"),
            new PeerResponse(
                MessageId.New(), organization, lead, position, thread, Priority.Normal,
                1, SentAt, null, MessageId.New(), "Reviewed"),
            new ApprovalRequest(
                MessageId.New(), organization, lead, owner, thread, Priority.High,
                1, SentAt, null, "Deploy", "Verified", ApprovalPolicyRef.From("release")),
            new ApprovalDecision(
                MessageId.New(), organization, owner, lead, thread, Priority.High,
                1, SentAt, null, MessageId.New(), approved: true, reason: null),
            new Pulse(
                MessageId.New(), organization,
                new SystemEndpointRef(SystemEndpointKind.Scheduler), position,
                thread, Priority.Normal, 1, SentAt, null, "daily", "{}"),
            new EventTrigger(
                MessageId.New(), organization,
                new SystemEndpointRef(SystemEndpointKind.DomainEvents), lead,
                thread, Priority.Normal, 1, SentAt, null, "deadline", "{}"),
        };

        foreach (var message in messages)
        {
            var result = await _validator.ValidateAsync(message, _context);

            Assert.True(
                result.IsValid,
                $"{message.GetType().Name}: {string.Join(", ", result.Errors)}");
            Assert.Equal(
                MessageContractRules.For(message.GetType()).Channel,
                message.Channel);
        }
    }

    private static Memo CreateMemo(int schemaVersion = 1) =>
        new(
            MessageId.New(), OrganizationId.From("acme"), Position("lead"),
            Position("developer"), ThreadId.New(), Priority.Normal, schemaVersion,
            SentAt, null, "Status update");

    private static ApprovalRequest CreateApprovalRequest() =>
        new(
            MessageId.New(), OrganizationId.From("acme"), Position("lead"),
            new OrganizationOwnerEndpointRef(), ThreadId.New(), Priority.High, 1,
            SentAt, null, "Deploy", "Verified", ApprovalPolicyRef.From("release"));

    private static Directive CreateDirective(
        OrganizationId organization,
        ThreadId thread,
        DirectiveId directiveId,
        DirectiveId? parentDirectiveId) =>
        new(
            MessageId.New(), organization, Position("lead"), Position("developer"),
            thread, Priority.Normal, 1, SentAt, null, directiveId,
            parentDirectiveId, "Implement", "Context");

    private static PositionEndpointRef Position(string value) =>
        new(PositionId.From(value));

    private static T SetProperty<T>(T instance, string propertyName, object? value)
        where T : class
    {
        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(
                $"<{propertyName}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (field is not null)
            {
                field.SetValue(instance, value);
                return instance;
            }
        }

        throw new InvalidOperationException($"Backing field for {propertyName} was not found.");
    }

    private sealed class TrackingValidationContext : IMessageValidationContext
    {
        public Dictionary<DirectiveId, Directive> Directives { get; } = [];

        public Exception? Failure { get; init; }

        public int LookupCount { get; private set; }

        public ValueTask<Directive?> FindDirectiveAsync(
            DirectiveId directiveId,
            CancellationToken cancellationToken = default)
        {
            LookupCount++;

            if (Failure is not null)
            {
                return ValueTask.FromException<Directive?>(Failure);
            }

            return ValueTask.FromResult(
                Directives.GetValueOrDefault(directiveId));
        }
    }
}
