using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class AiAgentActionGateTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly PositionId Superior = PositionId.From("delivery-lead");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001108"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001108"));
    private static readonly DirectiveId Directive =
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001108"));

    [Fact]
    public async Task Declared_trust_key_allows_tool_and_preserves_functional_candidate()
    {
        var key = AuthorityKey.From("delivery.bug-triage");
        var context = Context(canDecide: [key.Value]);
        var toolCall = new AiToolCall(
            "call-1",
            "files",
            new Dictionary<string, object?> { ["ticket"] = "HIVE-123" });
        var candidate = AiAgentActionCandidate.ForTool(
            toolCall,
            ActingUnderDeclaration.Declared(key));
        var gate = Gate(
            Catalog(Trust(key)),
            [ActionDomainActionContract.ForTool("files")]);

        var result = await gate.EvaluateAsync(context, candidate);

        Assert.True(result.IsAllowed);
        Assert.Same(candidate, result.Candidate);
        Assert.Same(toolCall, result.Candidate.ToolCall);
        Assert.Equal(key, result.Resolution!.AllowedAuthorityKey);
        Assert.Null(result.Retention);
    }

    [Fact]
    public async Task Missing_runtime_catalog_is_fail_closed_even_for_declared_authority()
    {
        var key = AuthorityKey.From("delivery.bug-triage");
        var candidate = AiAgentActionCandidate.ForTool(
            new AiToolCall("call-1", "files"),
            ActingUnderDeclaration.Declared(key));

        var result = await AiAgentActionGate.CreateFailClosed(
            NoopJourneyAuditLog.Instance).EvaluateAsync(
            Context(canDecide: [key.Value]),
            candidate);

        Assert.Equal(AiAgentActionGateOutcome.RetainedForEscalation, result.Outcome);
        Assert.Equal("action-gate-contract-unavailable", result.Code);
        Assert.IsType<Escalation>(Assert.Single(result.Retention!.GovernanceMessages));
    }

    [Fact]
    public async Task Objective_escalation_retains_message_and_uses_owner_for_root_position()
    {
        var key = AuthorityKey.From("messages.report-review");
        var context = Context(hasSuperior: false);
        var report = ResultReport(context);
        var gate = Gate(
            Catalog(ObjectiveMessage(key, ActionDomainGate.Escalate, nameof(Report))),
            [ActionDomainActionContract.ForOrganizationalMessage(nameof(Report))]);

        var result = await gate.EvaluateAsync(
            context,
            AiAgentActionCandidate.ForMessage(
                report,
                ActingUnderDeclaration.Missing()));

        Assert.Equal(AiAgentActionGateOutcome.RetainedForEscalation, result.Outcome);
        Assert.Equal(ActionGateResolution.ObjectiveEscalationCode, result.Code);
        Assert.Same(report, result.Retention!.Candidate.Message);
        var escalation = Assert.IsType<Escalation>(
            Assert.Single(result.Retention.GovernanceMessages));
        Assert.IsType<OrganizationOwnerEndpointRef>(escalation.To);
        Assert.DoesNotContain("Bug triage is complete", escalation.Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Human_approval_materializes_one_request_per_canonical_requirement()
    {
        var first = AuthorityKey.From("finance.commitments");
        var second = AuthorityKey.From("finance.large-commitments");
        var third = AuthorityKey.From("security.exception");
        var resolver = new RecordingApprovalResolver(query =>
            AiActionApprovalResolution.Resolved(
                new PositionEndpointRef(PositionId.From(query.RequiredApprover!)),
                ApprovalPolicyRef.From("action-domain-" + query.RequiredApprover)));
        var context = Context(
            overrides:
            [
                new PositionAuthorityOverrideRuntimeConfiguration(
                    first.Value,
                    ActionDomainGate.HumanApproval,
                    "delivery-lead"),
                new PositionAuthorityOverrideRuntimeConfiguration(
                    second.Value,
                    ActionDomainGate.HumanApproval,
                    "delivery-lead"),
                new PositionAuthorityOverrideRuntimeConfiguration(
                    third.Value,
                    ActionDomainGate.HumanApproval,
                    "ceo"),
            ]);
        var gate = Gate(
            Catalog(
                ObjectiveMessage(first, ActionDomainGate.HumanApproval, nameof(Report)),
                ObjectiveMessage(second, ActionDomainGate.HumanApproval, nameof(Report)),
                ObjectiveMessage(third, ActionDomainGate.HumanApproval, nameof(Report))),
            [ActionDomainActionContract.ForOrganizationalMessage(nameof(Report))],
            resolver);

        var result = await gate.EvaluateAsync(
            context,
            AiAgentActionCandidate.ForMessage(
                ResultReport(context),
                ActingUnderDeclaration.Missing()));

        Assert.Equal(AiAgentActionGateOutcome.RetainedForHumanApproval, result.Outcome);
        Assert.Equal(2, resolver.Queries.Count);
        Assert.Equal("ceo", resolver.Queries[0].RequiredApprover);
        Assert.Equal([third], resolver.Queries[0].AuthorityKeys);
        Assert.Equal("delivery-lead", resolver.Queries[1].RequiredApprover);
        Assert.Equal([first, second], resolver.Queries[1].AuthorityKeys);
        var requests = result.Retention!.GovernanceMessages
            .Select(message => Assert.IsType<ApprovalRequest>(message))
            .ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(
            [new PositionEndpointRef(PositionId.From("ceo")), new PositionEndpointRef(Superior)],
            requests.Select(request => request.To).ToArray());
        var retained = AiAgentRetainedActionFactory.Create(result, At).Action;
        Assert.Equal(RetainedActionKind.OrganizationalMessage, retained.Kind);
        Assert.Equal(
            ["action-domain-ceo", "action-domain-delivery-lead"],
            retained.ApprovalPolicies.Select(policy => policy.Value));
        Assert.Equal(Directive, retained.DirectiveId);
        Assert.Null(retained.ParentDirectiveId);
    }

    [Fact]
    public async Task Failed_approval_resolution_emits_no_partial_requests_and_keeps_human_gate()
    {
        var first = AuthorityKey.From("finance.commitments");
        var second = AuthorityKey.From("security.exception");
        var resolver = new RecordingApprovalResolver(query =>
            query.RequiredApprover == "ceo"
                ? AiActionApprovalResolution.Resolved(
                    new PositionEndpointRef(PositionId.From("ceo")),
                    ApprovalPolicyRef.From("action-domain-ceo"))
                : AiActionApprovalResolution.Failed("action-gate-policy-not-found"));
        var context = Context(
            overrides:
            [
                new PositionAuthorityOverrideRuntimeConfiguration(
                    first.Value,
                    ActionDomainGate.HumanApproval,
                    "ceo"),
                new PositionAuthorityOverrideRuntimeConfiguration(
                    second.Value,
                    ActionDomainGate.HumanApproval,
                    "delivery-lead"),
            ]);
        var gate = Gate(
            Catalog(
                ObjectiveMessage(first, ActionDomainGate.HumanApproval, nameof(Report)),
                ObjectiveMessage(second, ActionDomainGate.HumanApproval, nameof(Report))),
            [ActionDomainActionContract.ForOrganizationalMessage(nameof(Report))],
            resolver);

        var result = await gate.EvaluateAsync(
            context,
            AiAgentActionCandidate.ForMessage(
                ResultReport(context),
                ActingUnderDeclaration.Missing()));

        Assert.Equal(AiAgentActionGateOutcome.RetainedForHumanApproval, result.Outcome);
        Assert.Equal("action-gate-policy-not-found", result.Code);
        Assert.Equal(ActionGateOutcome.HumanApprovalRequired, result.Resolution!.Outcome);
        Assert.IsType<Escalation>(Assert.Single(result.Retention!.GovernanceMessages));
        Assert.DoesNotContain(result.Retention.GovernanceMessages, message => message is ApprovalRequest);
    }

    [Fact]
    public async Task Invalid_direct_facts_retain_tool_without_running_resolver_or_approval()
    {
        var key = AuthorityKey.From("files.scoped-write");
        var resolver = new RecordingApprovalResolver(_ =>
            throw new InvalidOperationException("Approval resolution must not run."));
        var gate = Gate(
            Catalog(ObjectiveTool(key, ActionDomainGate.Escalate, "files", "scope", "write")),
            [
                ActionDomainActionContract.ForTool(
                    "files",
                    [ActionAttributeDefinition.Direct("scope", ActionAttributeValueKind.String)]),
            ],
            resolver);

        var result = await gate.EvaluateAsync(
            Context(),
            AiAgentActionCandidate.ForTool(
                new AiToolCall("call-1", "files"),
                ActingUnderDeclaration.Missing()));

        Assert.Equal(AiAgentActionGateOutcome.RetainedForEscalation, result.Outcome);
        Assert.Equal("action-gate-direct-facts-invalid", result.Code);
        Assert.Null(result.Facts);
        Assert.Null(result.Resolution);
        Assert.Empty(resolver.Queries);
        var retained = AiAgentRetainedActionFactory.Create(result, At).Action;
        var repeated = AiAgentRetainedActionFactory.Create(result, At).Action;
        Assert.Equal(RetainedActionKind.Tool, retained.Kind);
        Assert.Contains("\"Name\":\"files\"", retained.CanonicalPayload, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", retained.Fingerprint.Value, StringComparison.Ordinal);
        Assert.Equal(
            RetainedActionFingerprintFactory.Create(
                result.Retention!.Candidate,
                result.Facts).Fingerprint,
            retained.Fingerprint);
        Assert.Equal(retained.Fingerprint, repeated.Fingerprint);
        Assert.Equal(retained.Id, repeated.Id);
    }

    [Fact]
    public async Task Iteration_executor_never_calls_connector_when_action_gate_retains_tool()
    {
        var key = AuthorityKey.From("files.review");
        var context = Context(tools: [new ToolConfiguration("files", ["bugs/read"])]);
        var state = AiDirectiveIterationState.Start(context, At);
        var decision = state.Evaluate(
            AiGatewayResponse.Succeeded(
                Organization,
                Position,
                Thread,
                Message,
                text: null,
                AiFinishReason.ToolCalls,
                toolCalls: [new AiToolCall("call-1", "files")]),
            At.AddSeconds(1),
            hasAvailableBudget: true);
        var toolExecutor = new RecordingToolExecutor();
        var gate = Gate(
            Catalog(ObjectiveTool(key, ActionDomainGate.Escalate, "files")),
            [ActionDomainActionContract.ForTool("files")]);
        var executor = new AiDirectiveIterationExecutor(
            new UnusedInvoker(),
            toolExecutor,
            gate);

        var result = await executor.ExecuteAsync(
            context,
            state,
            decision,
            hasAvailableBudget: true);

        Assert.True(result.IsFailure);
        Assert.Equal(ActionGateResolution.ObjectiveEscalationCode, result.Failure!.Code);
        Assert.Equal(
            AiAgentActionGateOutcome.RetainedForEscalation,
            result.Failure.ActionGateResult!.Outcome);
        Assert.Equal(0, toolExecutor.CallCount);
    }

    private static AiAgentActionGate Gate(
        ActionDomainCatalog catalog,
        IReadOnlyList<ActionDomainActionContract> contracts,
        IAiActionApprovalResolver? resolver = null) =>
        new(
            catalog,
            new ActionDomainCatalogBinding(actionContracts: contracts),
            resolver ?? new RecordingApprovalResolver(_ =>
                AiActionApprovalResolution.Failed("approval-not-expected")),
            () => At.AddMinutes(10));

    private static ActionDomainCatalog Catalog(params ActionDomain[] domains) =>
        new(
            1,
            new ActionDomainCatalogDefaults(ActionDomainGate.Escalate),
            domains);

    private static ActionDomain Trust(AuthorityKey key) =>
        new(key, "Trusted action.", ActionDomainGate.Decide, []);

    private static ActionDomain ObjectiveMessage(
        AuthorityKey key,
        ActionDomainGate gate,
        string messageType) =>
        new(
            key,
            "Objective message policy.",
            gate,
            [
                new ActionDomainMatchPredicate(
                    ActionDomainActionKind.OrganizationalMessage,
                    new Dictionary<string, object> { ["message_type"] = messageType }),
            ]);

    private static ActionDomain ObjectiveTool(
        AuthorityKey key,
        ActionDomainGate gate,
        string tool,
        string? attribute = null,
        object? value = null)
    {
        var attributes = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["tool"] = tool,
        };
        if (attribute is not null)
        {
            attributes[attribute] = value!;
        }

        return new ActionDomain(
            key,
            "Objective tool policy.",
            gate,
            [new ActionDomainMatchPredicate(ActionDomainActionKind.Tool, attributes)]);
    }

    private static AiDirectiveExecutionContext Context(
        bool hasSuperior = true,
        IEnumerable<string>? canDecide = null,
        IEnumerable<PositionAuthorityOverrideRuntimeConfiguration>? overrides = null,
        IEnumerable<ToolConfiguration>? tools = null)
    {
        var effectiveReportsTo = hasSuperior ? Superior : null;
        var entity = PositionEntityId.From(Organization, Position);
        var directive = new OrgDirective(
            Message,
            Organization,
            new PositionEndpointRef(Superior),
            new PositionEndpointRef(Position),
            Thread,
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: At.AddHours(2),
            Directive,
            parentDirectiveId: null,
            objective: "Triage checkout regression",
            context: "Customer reports checkout failures.");
        var configuration = new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(18, "sha256:t08-action-gate"),
            Organization,
            Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                effectiveReportsTo,
                name: "Bug triage",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "triage-v1",
                tools: tools,
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "triage"),
                    new AiModelParameters(maxOutputTokens: 256),
                    timeout: TimeSpan.FromSeconds(15),
                    maxIterations: 3),
                identityPrompt: new IdentityPromptRuntimeConfiguration(
                    "triage-v1",
                    "prompts/triage-v1.md",
                    "You are responsible for triaging incoming bugs.")),
            new PositionAuthorityRuntimeConfiguration(canDecide, overrides));
        var request = AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            PositionState.Restore(new PositionSnapshot(At)),
            OccupantId.From("agent-8"),
            directive);

        return AiDirectiveExecutionContext.From(request);
    }

    private static Report ResultReport(AiDirectiveExecutionContext context) =>
        new(
            MessageId.From(Guid.Parse("dddddddd-0000-0000-0000-000000001108")),
            context.OrganizationId,
            new PositionEndpointRef(context.PositionId),
            context.Relation.ReportsTo is { } superior
                ? new PositionEndpointRef(superior)
                : new OrganizationOwnerEndpointRef(),
            context.Directive.ThreadId,
            context.Directive.Priority,
            schemaVersion: 1,
            At.AddMinutes(5),
            context.Directive.Deadline,
            context.Directive.DirectiveId,
            ReportKind.Done,
            "Bug triage is complete.");

    private sealed class RecordingApprovalResolver(
        Func<AiActionApprovalResolutionQuery, AiActionApprovalResolution> resolve)
        : IAiActionApprovalResolver
    {
        public List<AiActionApprovalResolutionQuery> Queries { get; } = [];

        public ValueTask<AiActionApprovalResolution> ResolveAsync(
            AiActionApprovalResolutionQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add(query);
            return ValueTask.FromResult(resolve(query));
        }
    }

    private sealed class RecordingToolExecutor : IAiDirectiveConnectorToolExecutor
    {
        public int CallCount { get; private set; }

        public ValueTask<AiDirectiveConnectorToolExecutionResult> ExecuteAsync(
            AiDirectiveConnectorToolExecution execution,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(
                AiDirectiveConnectorToolExecutionResult.Succeeded(execution));
        }
    }

    private sealed class UnusedInvoker : IAiAgentGatewayInvoker
    {
        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Gateway invocation was not expected.");
    }
}
