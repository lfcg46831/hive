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

public sealed class AiAgentActionGateAuditTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly PositionId Superior = PositionId.From("delivery-lead");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001110"));
    private static readonly MessageId SourceMessage =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001110"));
    private static readonly DirectiveId Directive =
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001110"));

    [Fact]
    public async Task Allowed_tool_is_audited_with_authority_key_before_preserving_result_identity()
    {
        var key = AuthorityKey.From("delivery.bug-triage");
        var context = Context(canDecide: [key.Value]);
        var candidate = AiAgentActionCandidate.ForTool(
            new AiToolCall("call-allowed-tool", "files"),
            ActingUnderDeclaration.Declared(key));
        var expected = await Gate(
            Catalog(Trust(key)),
            [ActionDomainActionContract.ForTool("files")])
            .EvaluateAsync(context, candidate);
        var sequence = new List<string>();
        var log = new RecordingJourneyAuditLog(sequence);
        var sut = new StaticAuditedActionGate(
            expected,
            sequence,
            log,
            () => At);

        var actual = await sut.EvaluateAsync(context, candidate);
        sequence.Add("hand-off");

        Assert.Same(expected, actual);
        Assert.Same(candidate, actual.Candidate);
        Assert.Equal(["gate", "append", "hand-off"], sequence);
        var record = Assert.Single(log.Records);
        AssertCommonCorrelation(record);
        Assert.Equal(JourneyAuditStage.ActionGateEvaluated, record.Stage);
        Assert.Equal(JourneyAuditOutcome.Succeeded, record.Outcome);
        Assert.Equal(ActionGateResolution.DeclaredAuthorityCode, record.ReasonCode);
        Assert.Equal("tool", record.Payload["actionKind"]);
        Assert.Equal("files", record.Payload["actionSelector"]);
        Assert.Equal("allowed", record.Payload["gateOutcome"]);
        Assert.Equal("decide", record.Payload["effectiveGate"]);
        Assert.Equal(ActionGateResolution.DeclaredAuthorityCode, record.Payload["gateCode"]);
        Assert.Equal("declared", record.Payload["actingUnderState"]);
        Assert.Equal(ActingUnderDeclaration.DeclaredCode, record.Payload["actingUnderCode"]);
        Assert.Equal(key.Value, record.Payload["allowedAuthorityKey"]);
        Assert.Equal("0", record.Payload["matchCount"]);
        Assert.Equal("0", record.Payload["approvalRequirementCount"]);
        Assert.StartsWith("sha256:", record.Payload["actionInstanceDigest"], StringComparison.Ordinal);
        Assert.Equal(At, record.OccurredAtUtc);
    }

    [Fact]
    public async Task Allowed_message_is_audited_with_authority_key_and_source_correlation()
    {
        var key = AuthorityKey.From("messages.report");
        var context = Context(canDecide: [key.Value]);
        var candidate = AiAgentActionCandidate.ForMessage(
            ResultReport(context, "Safe body"),
            ActingUnderDeclaration.Declared(key));
        var log = new RecordingJourneyAuditLog();
        var sut = Gate(
            Catalog(Trust(key)),
            [ActionDomainActionContract.ForOrganizationalMessage(nameof(Report))],
            log);

        var result = await sut.EvaluateAsync(context, candidate);

        Assert.True(result.IsAllowed);
        var record = Assert.Single(log.Records);
        AssertCommonCorrelation(record);
        Assert.Equal(JourneyAuditOutcome.Succeeded, record.Outcome);
        Assert.Equal("organizational-message", record.Payload["actionKind"]);
        Assert.Equal(nameof(Report), record.Payload["actionSelector"]);
        Assert.Equal(key.Value, record.Payload["allowedAuthorityKey"]);
        Assert.Equal(SourceMessage, record.MessageId);
        Assert.NotEqual(candidate.Message!.Id, record.MessageId);
    }

    [Theory]
    [InlineData(
        ActionDomainGate.Escalate,
        2,
        "retained-for-escalation",
        "escalate",
        ActionGateResolution.ObjectiveEscalationCode)]
    [InlineData(
        ActionDomainGate.HumanApproval,
        3,
        "retained-for-human-approval",
        "human-approval",
        ActionGateResolution.ObjectiveHumanApprovalCode)]
    public async Task Objective_gate_is_audited_as_rejected_without_claiming_retention_commit(
        ActionDomainGate objectiveGate,
        int expectedOutcomeValue,
        string gateOutcome,
        string effectiveGate,
        string expectedCode)
    {
        var key = AuthorityKey.From("finance.commitments");
        var context = Context(canDecide: [key.Value]);
        var candidate = AiAgentActionCandidate.ForTool(
            new AiToolCall("call-objective", "payments"),
            ActingUnderDeclaration.Declared(key));
        var log = new RecordingJourneyAuditLog();
        var sut = Gate(
            Catalog(ObjectiveTool(key, objectiveGate, "payments")),
            [ActionDomainActionContract.ForTool("payments")],
            log);

        var result = await sut.EvaluateAsync(context, candidate);

        Assert.Equal((AiAgentActionGateOutcome)expectedOutcomeValue, result.Outcome);
        var record = Assert.Single(log.Records);
        Assert.Equal(JourneyAuditOutcome.Rejected, record.Outcome);
        Assert.Equal(expectedCode, record.ReasonCode);
        Assert.Equal(gateOutcome, record.Payload["gateOutcome"]);
        Assert.Equal(effectiveGate, record.Payload["effectiveGate"]);
        Assert.Equal(expectedCode, record.Payload["gateCode"]);
        Assert.Equal("1", record.Payload["matchCount"]);
        Assert.Equal(
            objectiveGate == ActionDomainGate.HumanApproval ? "1" : "0",
            record.Payload["approvalRequirementCount"]);
        Assert.False(record.Payload.ContainsKey("allowedAuthorityKey"));
        Assert.False(record.Payload.ContainsKey("retainedActionId"));
        Assert.False(record.Payload.ContainsKey("actionFingerprint"));
    }

    [Theory]
    [InlineData(ActingUnderDeclarationState.Missing, ActingUnderDeclaration.MissingCode)]
    [InlineData(ActingUnderDeclarationState.Invalid, ActingUnderDeclaration.InvalidCode)]
    public async Task Unmatched_missing_or_invalid_declaration_is_explicitly_audited(
        ActingUnderDeclarationState state,
        string declarationCode)
    {
        var declaration = state == ActingUnderDeclarationState.Missing
            ? ActingUnderDeclaration.Missing()
            : ActingUnderDeclaration.Invalid();
        var context = Context();
        var candidate = AiAgentActionCandidate.ForTool(
            new AiToolCall("call-unmatched", "files"),
            declaration);
        var log = new RecordingJourneyAuditLog();
        var sut = Gate(
            Catalog(),
            [ActionDomainActionContract.ForTool("files")],
            log);

        var result = await sut.EvaluateAsync(context, candidate);

        Assert.Equal(AiAgentActionGateOutcome.RetainedForEscalation, result.Outcome);
        Assert.Equal(ActionGateResolution.UnmatchedActionDefaultCode, result.Code);
        var record = Assert.Single(log.Records);
        Assert.Equal(JourneyAuditOutcome.Rejected, record.Outcome);
        Assert.Equal(ActionGateResolution.UnmatchedActionDefaultCode, record.ReasonCode);
        Assert.Equal("escalate", record.Payload["effectiveGate"]);
        Assert.Equal(
            state == ActingUnderDeclarationState.Missing ? "missing" : "invalid",
            record.Payload["actingUnderState"]);
        Assert.Equal(declarationCode, record.Payload["actingUnderCode"]);
        Assert.False(record.Payload.ContainsKey("allowedAuthorityKey"));
    }

    [Fact]
    public async Task Structural_failure_without_resolution_is_audited_as_fail_closed()
    {
        const string untrustedSelector = "missing-contract-user@example.com-secret";
        var key = AuthorityKey.From("delivery.bug-triage");
        var context = Context(canDecide: [key.Value]);
        var candidate = AiAgentActionCandidate.ForTool(
            new AiToolCall("call-no-contract", untrustedSelector),
            ActingUnderDeclaration.Declared(key));
        var log = new RecordingJourneyAuditLog();
        var sut = AiAgentActionGate.CreateFailClosed(log, auditClock: () => At);

        var result = await sut.EvaluateAsync(context, candidate);

        Assert.Equal(AiAgentActionGateOutcome.RetainedForEscalation, result.Outcome);
        Assert.Null(result.Resolution);
        Assert.Equal("action-gate-contract-unavailable", result.Code);
        var record = Assert.Single(log.Records);
        Assert.Equal(JourneyAuditOutcome.Rejected, record.Outcome);
        Assert.Equal("action-gate-contract-unavailable", record.ReasonCode);
        Assert.Equal("retained-for-escalation", record.Payload["gateOutcome"]);
        Assert.Equal("fail-closed", record.Payload["effectiveGate"]);
        Assert.Equal("redacted", record.Payload["actionSelector"]);
        Assert.Equal("0", record.Payload["matchCount"]);
        Assert.Equal("0", record.Payload["approvalRequirementCount"]);
        Assert.False(record.Payload.ContainsKey("allowedAuthorityKey"));
        Assert.DoesNotContain(untrustedSelector, record.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Untrusted_approval_failure_code_is_normalized_before_audit_and_retention()
    {
        const string untrustedCode = "action-gate-password-hunter2";
        var key = AuthorityKey.From("finance.commitments");
        var context = Context();
        var candidate = AiAgentActionCandidate.ForTool(
            new AiToolCall("call-untrusted-code", "payments"),
            ActingUnderDeclaration.Missing());
        var log = new RecordingJourneyAuditLog();
        var sut = new AiAgentActionGate(
            Catalog(ObjectiveTool(key, ActionDomainGate.HumanApproval, "payments")),
            new ActionDomainCatalogBinding(
                actionContracts: [ActionDomainActionContract.ForTool("payments")]),
            new FailedApprovalResolver(untrustedCode),
            log,
            () => At,
            () => At);

        var result = await sut.EvaluateAsync(context, candidate);

        Assert.Equal(AiAgentActionGateCodes.UnknownFailure, result.Code);
        Assert.Equal(AiAgentActionGateCodes.UnknownFailure, result.Retention!.Code);
        var record = Assert.Single(log.Records);
        Assert.Equal(AiAgentActionGateCodes.UnknownFailure, record.ReasonCode);
        Assert.Equal(AiAgentActionGateCodes.UnknownFailure, record.Payload["gateCode"]);
        Assert.Contains("gate.code:normalized", record.Payload["redactions"], StringComparison.Ordinal);
        Assert.DoesNotContain(untrustedCode, record.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            untrustedCode,
            string.Join(" ", result.Retention.GovernanceMessages),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_payload_is_whitelisted_and_redacts_functional_and_technical_content()
    {
        const string secretArgument = "secret=card-4111111111111111";
        const string factName = "customer_email";
        const string email = "customer@example.com";
        const string invalidActingUnder = "finance.invalid-secret-key";
        const string rawError = "database password=hunter2";
        var key = AuthorityKey.From("files.scoped-write");
        var context = Context(
            objective: "Investigate customer@example.com",
            directiveContext: "Use secret=directive-token");
        var candidate = AiAgentActionCandidate.ForTool(
            new AiToolCall(
                "call-sensitive-tool-id",
                "files",
                new Dictionary<string, object?>
                {
                    [factName] = email,
                    ["secret"] = secretArgument,
                }),
            ActingUnderDeclaration.Invalid());
        var log = new RecordingJourneyAuditLog();
        var sut = Gate(
            Catalog(ObjectiveTool(key, ActionDomainGate.Escalate, "files", factName, email)),
            [
                ActionDomainActionContract.ForTool(
                    "files",
                    [ActionAttributeDefinition.Direct(factName, ActionAttributeValueKind.String)]),
            ],
            log);
        _ = rawError;

        await sut.EvaluateAsync(context, candidate);

        var record = Assert.Single(log.Records);
        Assert.Equal(
            new[]
            {
                "actionInstanceDigest",
                "actionKind",
                "actionSelector",
                "actingUnderCode",
                "actingUnderState",
                "approvalRequirementCount",
                "correlationId",
                "effectiveGate",
                "gateCode",
                "gateOutcome",
                "matchCount",
                "parentDirectiveId",
                "redactions",
            }.Order(StringComparer.Ordinal),
            record.Payload.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("action.arguments", record.Payload["redactions"], StringComparison.Ordinal);
        Assert.Contains("action.facts", record.Payload["redactions"], StringComparison.Ordinal);
        Assert.Contains("acting_under.raw", record.Payload["redactions"], StringComparison.Ordinal);
        var persistedText = record.ToString();
        Assert.DoesNotContain(secretArgument, persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain(factName, persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain(email, persistedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(invalidActingUnder, persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain(rawError, persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("directive-token", persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("call-sensitive-tool-id", persistedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Message_body_is_not_retained_by_action_gate_audit()
    {
        const string body = "Report for person@example.com with secret=message-token";
        var key = AuthorityKey.From("messages.report");
        var context = Context(canDecide: [key.Value]);
        var candidate = AiAgentActionCandidate.ForMessage(
            ResultReport(context, body),
            ActingUnderDeclaration.Declared(key));
        var log = new RecordingJourneyAuditLog();
        var sut = Gate(
            Catalog(Trust(key)),
            [ActionDomainActionContract.ForOrganizationalMessage(nameof(Report))],
            log);

        await sut.EvaluateAsync(context, candidate);

        var record = Assert.Single(log.Records);
        Assert.Contains("action.message.payload", record.Payload["redactions"], StringComparison.Ordinal);
        Assert.DoesNotContain(body, record.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.com", record.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("message-token", record.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_id_is_stable_for_retry_and_distinguishes_tool_call_instances()
    {
        var key = AuthorityKey.From("delivery.bug-triage");
        var alternateKey = AuthorityKey.From("delivery.release-triage");
        var context = Context(canDecide: [key.Value, alternateKey.Value]);
        var log = new RecordingJourneyAuditLog();
        var sut = Gate(
            Catalog(Trust(key), Trust(alternateKey)),
            [ActionDomainActionContract.ForTool("files")],
            log);
        var first = AiAgentActionCandidate.ForTool(
            new AiToolCall(
                "call-id-1",
                "files",
                new Dictionary<string, object?> { ["secret"] = "first-secret" }),
            ActingUnderDeclaration.Declared(key));
        var sameLogicalActionWithDifferentArguments = AiAgentActionCandidate.ForTool(
            new AiToolCall(
                "call-id-1",
                "files",
                new Dictionary<string, object?> { ["secret"] = "second-secret" }),
            ActingUnderDeclaration.Declared(key));
        var secondInstance = AiAgentActionCandidate.ForTool(
            new AiToolCall("call-id-2", "files"),
            ActingUnderDeclaration.Declared(key));
        var sameInstanceUnderAlternateAuthority = AiAgentActionCandidate.ForTool(
            new AiToolCall("call-id-1", "files"),
            ActingUnderDeclaration.Declared(alternateKey));

        await sut.EvaluateAsync(context, first);
        await sut.EvaluateAsync(context, sameLogicalActionWithDifferentArguments);
        await sut.EvaluateAsync(context, sameInstanceUnderAlternateAuthority);
        await sut.EvaluateAsync(context, secondInstance);

        Assert.Equal(4, log.Records.Count);
        Assert.Equal(log.Records[0].AuditEventId, log.Records[1].AuditEventId);
        Assert.NotEqual(log.Records[0].AuditEventId, log.Records[2].AuditEventId);
        Assert.Equal(
            log.Records[0].Payload["actionInstanceDigest"],
            log.Records[1].Payload["actionInstanceDigest"]);
        Assert.Equal(
            log.Records[0].Payload["actionInstanceDigest"],
            log.Records[2].Payload["actionInstanceDigest"]);
        Assert.Equal(key.Value, log.Records[0].Payload["allowedAuthorityKey"]);
        Assert.Equal(alternateKey.Value, log.Records[2].Payload["allowedAuthorityKey"]);
        Assert.NotEqual(log.Records[0].AuditEventId, log.Records[3].AuditEventId);
        Assert.NotEqual(
            log.Records[0].Payload["actionInstanceDigest"],
            log.Records[3].Payload["actionInstanceDigest"]);
        Assert.DoesNotContain("first-secret", log.Records[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("second-secret", log.Records[1].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_append_exception_is_propagated_and_prevents_allowed_hand_off()
    {
        var key = AuthorityKey.From("delivery.bug-triage");
        var context = Context(canDecide: [key.Value]);
        var candidate = AiAgentActionCandidate.ForTool(
            new AiToolCall("call-append-failure", "files"),
            ActingUnderDeclaration.Declared(key));
        var expected = new InvalidOperationException("audit store unavailable: password=hunter2");
        var sut = Gate(
            Catalog(Trust(key)),
            [ActionDomainActionContract.ForTool("files")],
            new ThrowingJourneyAuditLog(expected));
        var handOffCount = 0;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await sut.EvaluateAsync(context, candidate);
            handOffCount++;
        });

        Assert.Same(expected, actual);
        Assert.Equal(0, handOffCount);
    }

    private static AiAgentActionGate Gate(
        ActionDomainCatalog catalog,
        IReadOnlyList<ActionDomainActionContract> contracts,
        IJourneyAuditLog? auditLog = null) =>
        new(
            catalog,
            new ActionDomainCatalogBinding(actionContracts: contracts),
            new ResolvedApprovalResolver(),
            auditLog ?? NoopJourneyAuditLog.Instance,
            () => At.AddMinutes(5),
            () => At);

    private static ActionDomainCatalog Catalog(params ActionDomain[] domains) =>
        new(
            1,
            new ActionDomainCatalogDefaults(ActionDomainGate.Escalate),
            domains);

    private static ActionDomain Trust(AuthorityKey key) =>
        new(key, "Trusted action.", ActionDomainGate.Decide, []);

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
        IEnumerable<string>? canDecide = null,
        string objective = "Triage checkout regression",
        string directiveContext = "Customer reports checkout failures.")
    {
        var entity = PositionEntityId.From(Organization, Position);
        var directive = new OrgDirective(
            SourceMessage,
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
            objective,
            directiveContext);
        var configuration = new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(18, "sha256:t10-action-gate-audit"),
            Organization,
            Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                Superior,
                name: "Bug triage",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "triage-v1",
                tools: [new ToolConfiguration("files", ["bugs/read"])],
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "triage"),
                    new AiModelParameters(maxOutputTokens: 256),
                    timeout: TimeSpan.FromSeconds(15),
                    maxIterations: 3),
                identityPrompt: new IdentityPromptRuntimeConfiguration(
                    "triage-v1",
                    "prompts/triage-v1.md",
                    "You are responsible for triaging incoming bugs.")),
            new PositionAuthorityRuntimeConfiguration(canDecide));
        var request = AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            PositionState.Restore(new PositionSnapshot(At)),
            OccupantId.From("agent-10"),
            directive);

        return AiDirectiveExecutionContext.From(request);
    }

    private static Report ResultReport(AiDirectiveExecutionContext context, string body) =>
        new(
            MessageId.From(Guid.Parse("dddddddd-0000-0000-0000-000000001110")),
            context.OrganizationId,
            new PositionEndpointRef(context.PositionId),
            new PositionEndpointRef(Superior),
            context.Directive.ThreadId,
            context.Directive.Priority,
            schemaVersion: 1,
            At.AddMinutes(5),
            context.Directive.Deadline,
            context.Directive.DirectiveId,
            ReportKind.Done,
            body);

    private static void AssertCommonCorrelation(JourneyAuditRecord record)
    {
        Assert.Equal(Organization, record.OrganizationId);
        Assert.Equal(Position, record.PositionId);
        Assert.Equal(Thread, record.ThreadId);
        Assert.Equal(SourceMessage, record.MessageId);
        Assert.Equal(Directive, record.DirectiveId);
    }

    private sealed class StaticAuditedActionGate(
        AiAgentActionGateResult result,
        IList<string> sequence,
        IJourneyAuditLog auditLog,
        Func<DateTimeOffset> auditClock) : AiAgentActionGateBase(auditLog, auditClock)
    {
        protected override ValueTask<AiAgentActionGateResult> EvaluateCoreAsync(
            AiDirectiveExecutionContext context,
            AiAgentActionCandidate candidate,
            CancellationToken cancellationToken)
        {
            sequence.Add("gate");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ResolvedApprovalResolver : IAiActionApprovalResolver
    {
        public ValueTask<AiActionApprovalResolution> ResolveAsync(
            AiActionApprovalResolutionQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                AiActionApprovalResolution.Resolved(
                    new PositionEndpointRef(Superior),
                    ApprovalPolicyRef.From("action-domain-default")));
    }

    private sealed class FailedApprovalResolver(string code) : IAiActionApprovalResolver
    {
        public ValueTask<AiActionApprovalResolution> ResolveAsync(
            AiActionApprovalResolutionQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AiActionApprovalResolution.Failed(code));
    }

    private sealed class RecordingJourneyAuditLog(IList<string>? sequence = null)
        : IJourneyAuditLog
    {
        private readonly List<JourneyAuditRecord> _records = [];

        public IReadOnlyList<JourneyAuditRecord> Records => _records;

        public void Append(JourneyAuditRecord record)
        {
            sequence?.Add("append");
            _records.Add(record);
        }

        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null) =>
            _records
                .Where(record => record.ThreadId == threadId
                                 && (directiveId is null || record.DirectiveId == directiveId))
                .ToArray();
    }

    private sealed class ThrowingJourneyAuditLog(Exception exception) : IJourneyAuditLog
    {
        public void Append(JourneyAuditRecord record) => throw exception;

        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null) => [];
    }
}
