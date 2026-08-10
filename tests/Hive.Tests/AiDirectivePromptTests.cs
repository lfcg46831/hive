using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Directives;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Outcomes;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class AiDirectivePromptTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 1, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateInitialRequest_rejects_null_context()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AiDirectivePrompt.CreateInitialRequest(null!));
    }

    [Fact]
    public void CreateInitialRequest_negotiates_and_explains_the_outcome_proposal_when_required()
    {
        var context = AiDirectiveExecutionContext.From(
            Request(includeOptionalContext: false),
            requiresStructuredOutcomeProposal: true);

        var request = AiDirectivePrompt.CreateInitialRequest(context);

        Assert.Contains(
            AiDirectiveOutcomeProposalEnvelope.PropertyName,
            request.OutputConstraint!.JsonSchema.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()));
        Assert.Contains("non-authoritative proposal", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("directive.context", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("information_gaps", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("authority_request", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("NonMaterial", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("ActionDomain", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("Use []", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains(
            "does not assert that the input is complete",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never cite the same reference as grounding evidence and as a Material information gap",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Equal(
            OutcomeProposalConstraint.SchemaVersion,
            request.OutputConstraint.JsonSchema.GetProperty("properties")
                .GetProperty(AiDirectiveOutcomeProposalEnvelope.PropertyName)
                .GetProperty("properties")
                .GetProperty(OutcomeProposalConstraint.SchemaVersionProperty)
                .GetProperty("const")
                .GetInt32());
    }

    [Fact]
    public void CreateInitialRequest_defaults_to_single_shot_and_omits_progress_from_both_schemas()
    {
        var context = AiDirectiveExecutionContext.From(
            Request(includeOptionalContext: false),
            requiresStructuredOutcomeProposal: true);

        var request = AiDirectivePrompt.CreateInitialRequest(context);

        Assert.Equal(DirectiveExecutionMode.SingleShot, context.ExecutionPolicy.Mode);
        Assert.Equal("single-shot", request.Metadata["hive.directive-execution-mode"]);
        Assert.DoesNotContain("Report.Progress", request.SystemInstruction, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Progress",
            ReportKindVocabulary(request.OutputConstraint!),
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            "Report.Progress",
            OutcomeProposalIntentVocabulary(request.OutputConstraint!),
            StringComparer.Ordinal);
    }

    [Fact]
    public void CreateInitialRequest_exposes_checkpointable_relative_budget_and_progress_gates()
    {
        var context = AiDirectiveExecutionContext.From(
            Request(
                includeOptionalContext: true,
                executionTimeout: TimeSpan.FromSeconds(90),
                executionPolicy: new DirectiveExecutionPolicyRequest(
                    1,
                    DirectiveExecutionMode.Checkpointable),
                executionCapability: new DirectiveExecutionPolicyCapability(
                    1,
                    DirectiveExecutionMode.Checkpointable,
                    TimeSpan.FromSeconds(15))),
            requiresStructuredOutcomeProposal: true);

        var request = AiDirectivePrompt.CreateInitialRequest(context);

        Assert.Equal(DirectiveExecutionMode.Checkpointable, context.ExecutionPolicy.Mode);
        Assert.Equal(TimeSpan.FromSeconds(90), context.ExecutionPolicy.TotalExecutionBudget);
        Assert.Equal(TimeSpan.FromSeconds(90), context.ExecutionPolicy.RemainingExecutionTime);
        Assert.Contains("Report.Progress", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("00:01:30", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("00:00:15", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("Deadline: <managed-by-runtime>", request.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(At.AddHours(2).ToString("O"), request.Content, StringComparison.Ordinal);
        Assert.Contains(
            "Progress",
            ReportKindVocabulary(request.OutputConstraint!),
            StringComparer.Ordinal);
        Assert.Contains(
            "Report.Progress",
            OutcomeProposalIntentVocabulary(request.OutputConstraint!),
            StringComparer.Ordinal);
    }

    [Fact]
    public void CreateInitialRequest_fails_checkpointable_request_closed_on_legacy_shared_timeout()
    {
        var context = AiDirectiveExecutionContext.From(
            Request(
                includeOptionalContext: false,
                executionPolicy: new DirectiveExecutionPolicyRequest(
                    1,
                    DirectiveExecutionMode.Checkpointable),
                executionCapability: new DirectiveExecutionPolicyCapability(
                    1,
                    DirectiveExecutionMode.Checkpointable,
                    TimeSpan.FromSeconds(5))),
            requiresStructuredOutcomeProposal: true);

        var request = AiDirectivePrompt.CreateInitialRequest(context);

        Assert.Equal(DirectiveExecutionMode.SingleShot, context.ExecutionPolicy.Mode);
        Assert.Equal(
            DirectiveExecutionPolicyDecisionCode.ExecutionBudgetMissing,
            context.ExecutionPolicy.DecisionCode);
        Assert.DoesNotContain("Report.Progress", request.SystemInstruction, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Progress",
            ReportKindVocabulary(request.OutputConstraint!),
            StringComparer.Ordinal);
    }

    [Fact]
    public void CreateInitialRequest_creates_provider_neutral_request_without_assuming_bug_triage_role()
    {
        var processingRequest = Request(includeOptionalContext: true);
        var context = AiDirectiveExecutionContext.From(processingRequest);

        var request = AiDirectivePrompt.CreateInitialRequest(context);

        Assert.Equal(processingRequest.OrganizationId, request.OrganizationId);
        Assert.Equal(processingRequest.PositionId, request.PositionId);
        Assert.Equal(processingRequest.ThreadId, request.ThreadId);
        Assert.Equal(processingRequest.MessageId, request.MessageId);
        Assert.Equal("stub", request.Provider!.ProviderId);
        Assert.Equal("triage", request.Provider.ModelId);
        Assert.Equal(256, request.ModelParameters.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromSeconds(15), request.Timeout);
        Assert.Equal(AiProcessingMode.Batch, request.ProcessingMode);
        var requestTool = Assert.Single(request.Tools);
        Assert.Equal("jira", requestTool.Name);
        Assert.Equal(
            "Authorized HIVE connector 'jira' with scopes: issues/read, issues/comment.",
            requestTool.Description);
        AssertActingUnderSchema(requestTool, "bug.triage");

        Assert.NotNull(request.SystemInstruction);
        Assert.Contains(
            AiDirectiveSystemInstructionSections.BusinessIdentityHeader,
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            AiDirectiveSystemInstructionSections.HiveProtocolHeader,
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            AiDirectiveSystemInstructionSections.RuntimeAuthorityHeader,
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            AiDirectiveSystemInstructionSections.RuntimeToolsHeader,
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "## Evaluation",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains("current position", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("Escalate work outside this position's authority", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("Return JSON only", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("\"intent\"", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("Report", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("Escalation", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("Directive", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains(
            "Choose Report only for information or an assessment that asks the superior for no decision, authorization, or choice.",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Choose Escalation whenever the response asks the superior to decide, authorize, or choose; never place such a request inside Report.",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "A recommendation about a future action is informational and does not by itself request or exercise authorization.",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Normal downstream implementation, deployment, prioritization, or change control does not alone make it an Escalation",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "without exposing intermediate reasoning",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "required top-level \"acting_under\"",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Allowed \"acting_under\" values for this position: \"bug.triage\".",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains("Do not invent routing", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains(
            "Directive only when a permitted downward target is explicit",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.DoesNotContain("bug triage agent", request.SystemInstruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("You are responsible for triaging incoming bugs.", request.SystemInstruction, StringComparison.Ordinal);
        Assert.DoesNotContain("hive-evaluation-v1:", request.SystemInstruction, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime evaluation appendix", request.SystemInstruction, StringComparison.Ordinal);
        Assert.DoesNotContain("missing-information", request.SystemInstruction, StringComparison.Ordinal);

        Assert.Contains($"CorrelationId: {processingRequest.CorrelationId}", request.Content, StringComparison.Ordinal);
        Assert.Contains("IdentityPromptRef: triage-v1", request.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("IdentityPrompt:", request.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("You are responsible for triaging incoming bugs.", request.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("hive-evaluation-v1:", request.Content, StringComparison.Ordinal);
        Assert.Contains("Objective: Triage checkout regression", request.Content, StringComparison.Ordinal);
        Assert.Contains("Context: Customer reports checkout failures.", request.Content, StringComparison.Ordinal);
        Assert.Contains("DirectiveId: cccccccc-0000-0000-0000-000000000904", request.Content, StringComparison.Ordinal);
        Assert.Contains("ParentDirectiveId: cccccccc-0000-0000-0000-000000000099", request.Content, StringComparison.Ordinal);
        Assert.Contains("Deadline: 2026-07-01T18:00:00.0000000+00:00", request.Content, StringComparison.Ordinal);
        Assert.Contains("ReportsTo: delivery-lead", request.Content, StringComparison.Ordinal);
        Assert.Contains("CanDecide: bug.triage", request.Content, StringComparison.Ordinal);
        Assert.Contains("AuthorityOverrides:", request.Content, StringComparison.Ordinal);
        Assert.Contains("- comms.external-official: human-approval (approver: delivery-lead)", request.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("MustEscalate", request.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("RequiresHumanApproval", request.Content, StringComparison.Ordinal);
        Assert.Contains("AuthorizedTools:", request.Content, StringComparison.Ordinal);
        Assert.Contains("- jira: issues/read, issues/comment", request.Content, StringComparison.Ordinal);
        AssertContainsInOrder(request.Content, "- alpha: first", "- zeta: last");
        AssertContainsInOrder(request.Content, "First task", "Second task");
        Assert.DoesNotContain("```", request.SystemInstruction + request.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Bug triage directive context", request.Content, StringComparison.Ordinal);
        var outputConstraint = Assert.IsType<AiOutputConstraint>(request.OutputConstraint);
        Assert.Equal("hive_ai_directive_decision_v1", outputConstraint.SchemaName);
        Assert.Equal(1, outputConstraint.SchemaVersion);
        var schema = outputConstraint.JsonSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.TryGetProperty("oneOf", out _));
        Assert.False(schema.TryGetProperty("anyOf", out _));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["schema_version", "acting_under", "decision"],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray());
        var properties = schema.GetProperty("properties");
        var alternatives = properties.GetProperty("decision")
            .GetProperty("anyOf")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(3, alternatives.Length);
        foreach (var (intent, payloadName) in new[]
                 {
                     ("Report", "report"),
                     ("Escalation", "escalation"),
                     ("Directive", "directive"),
                 })
        {
            var alternative = Assert.Single(
                alternatives.Where(item =>
                    item.GetProperty("properties")
                        .GetProperty("intent")
                        .GetProperty("const")
                        .GetString() == intent));
            Assert.Equal(
                ["intent", payloadName],
                alternative.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray());
            Assert.False(alternative.GetProperty("additionalProperties").GetBoolean());
        }
        var reportBranch = alternatives.Single(item =>
            item.GetProperty("properties")
                .GetProperty("intent")
                .GetProperty("const")
                .GetString() == "Report");
        Assert.Contains(
            "\\u3000",
            reportBranch.GetProperty("properties")
                .GetProperty("report")
                .GetProperty("properties")
                .GetProperty("body")
                .GetProperty("pattern")
                .GetString(),
            StringComparison.Ordinal);
        Assert.Equal(
            [AiOutputConstraintMode.JsonObject, AiOutputConstraintMode.Text],
            outputConstraint.AllowedFallbackModes);
        Assert.Contains(
            "exactly one \"intent\" value and its single matching payload",
            request.SystemInstruction,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CreateInitialRequest_keeps_instruction_ownership_sections_separate()
    {
        var context = AiDirectiveExecutionContext.From(
            Request(includeOptionalContext: true));

        var sections = AiDirectivePrompt.BuildSystemInstructionSections(context);

        Assert.Equal(
            "You are responsible for triaging incoming bugs.",
            sections.BusinessIdentity);
        Assert.DoesNotContain("HIVE", sections.BusinessIdentity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hive-evaluation-v1", sections.BusinessIdentity, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Return JSON only", sections.HiveProtocol, StringComparison.Ordinal);
        Assert.Contains(
            "Choose Report only for information or an assessment that asks the superior for no decision, authorization, or choice.",
            sections.HiveProtocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "Choose Escalation whenever the response asks the superior to decide, authorize, or choose; never place such a request inside Report.",
            sections.HiveProtocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "A recommendation about a future action is informational and does not by itself request or exercise authorization.",
            sections.HiveProtocol,
            StringComparison.Ordinal);
        Assert.DoesNotContain("triaging incoming bugs", sections.HiveProtocol, StringComparison.Ordinal);
        Assert.DoesNotContain("severity", sections.HiveProtocol, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evidence", sections.HiveProtocol, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hive-evaluation-v1", sections.HiveProtocol, StringComparison.Ordinal);

        Assert.Contains("Allowed \"acting_under\" values", sections.RuntimeAuthority, StringComparison.Ordinal);
        Assert.DoesNotContain("hive-evaluation-v1", sections.RuntimeAuthority, StringComparison.Ordinal);

        Assert.Contains("Authorized connector names: jira", sections.RuntimeTools, StringComparison.Ordinal);
        Assert.DoesNotContain("triaging incoming bugs", sections.RuntimeTools, StringComparison.Ordinal);

        var composed = sections.Compose();
        AssertContainsInOrder(
            composed,
            AiDirectiveSystemInstructionSections.BusinessIdentityHeader,
            AiDirectiveSystemInstructionSections.HiveProtocolHeader);
        AssertContainsInOrder(
            composed,
            AiDirectiveSystemInstructionSections.HiveProtocolHeader,
            AiDirectiveSystemInstructionSections.RuntimeAuthorityHeader);
        AssertContainsInOrder(
            composed,
            AiDirectiveSystemInstructionSections.RuntimeAuthorityHeader,
            AiDirectiveSystemInstructionSections.RuntimeToolsHeader);
        Assert.DoesNotContain("hive-evaluation-v1", composed, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateInitialRequest_represents_missing_optional_context_explicitly()
    {
        var processingRequest = Request(includeOptionalContext: false);
        var context = AiDirectiveExecutionContext.From(processingRequest);

        var request = AiDirectivePrompt.CreateInitialRequest(context);

        Assert.Contains("IdentityPromptRef: triage-v1", request.Content, StringComparison.Ordinal);
        Assert.Contains("You are responsible for triaging incoming bugs.", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("ParentDirectiveId: <none>", request.Content, StringComparison.Ordinal);
        Assert.Contains("Deadline: <none>", request.Content, StringComparison.Ordinal);
        Assert.Contains("ReportsTo: <none>", request.Content, StringComparison.Ordinal);
        Assert.Contains("ShortMemory: <empty>", request.Content, StringComparison.Ordinal);
        Assert.Contains("OpenTasks: <empty>", request.Content, StringComparison.Ordinal);
        Assert.Contains("RecentHistory: <empty>", request.Content, StringComparison.Ordinal);
        Assert.Contains("CanDecide: <empty>", request.Content, StringComparison.Ordinal);
        Assert.Contains("AuthorityOverrides: <empty>", request.Content, StringComparison.Ordinal);
        Assert.Contains("AuthorizedTools: <empty>", request.Content, StringComparison.Ordinal);
        Assert.Empty(request.Tools);
        Assert.Contains(
            "required top-level \"acting_under\"",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Allowed \"acting_under\" values for this position: <empty>.",
            request.SystemInstruction,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CreateInitialRequest_includes_only_related_scoped_context_before_gateway_request()
    {
        var currentTaskId = PositionTaskId.From(
            Guid.Parse("00000000-0000-0000-0000-000000000020"));
        var relatedTaskId = PositionTaskId.From(
            Guid.Parse("00000000-0000-0000-0000-000000000010"));
        var unrelatedTaskId = PositionTaskId.From(
            Guid.Parse("00000000-0000-0000-0000-000000000030"));
        var unrelatedThread = ThreadId.From(
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000009999"));
        var unrelatedHistory = MessageId.From(
            Guid.Parse("dddddddd-0000-0000-0000-000000009999"));

        var processingRequest = Request(
            includeOptionalContext: true,
            stateFactory: (directive, occupant, stamp) => PositionState.Restore(new PositionSnapshot(
                At,
                occupant,
                OccupantType.AiAgent,
                openTasks:
                [
                    new PersistedTask(
                        currentTaskId,
                        directive.Thread,
                        "current-task-title",
                        Priority.High,
                        At,
                        causedBy: directive.Id),
                    new PersistedTask(
                        relatedTaskId,
                        directive.Thread,
                        "related-task-title",
                        Priority.Normal,
                        At),
                    new PersistedTask(
                        unrelatedTaskId,
                        unrelatedThread,
                        "unrelated-task-title",
                        Priority.Critical,
                        At),
                ],
                shortMemory: new Dictionary<string, string>
                {
                    ["task-current"] = "eligible-task-memory",
                    ["thread-current"] = "eligible-thread-memory",
                    ["position-fact"] = "eligible-position-fact",
                    ["legacy"] = "unscoped-memory-must-not-leak",
                    ["thread-other"] = "other-thread-memory-must-not-leak",
                    ["task-other"] = "other-task-memory-must-not-leak",
                },
                recentHistory: [unrelatedHistory, directive.Id],
                lastConfigurationStamp: stamp,
                shortMemoryContextScopes: new Dictionary<string, ShortMemoryContextScope>
                {
                    ["task-current"] = ShortMemoryContextScope.ForTask(
                        directive.Thread,
                        currentTaskId),
                    ["thread-current"] = ShortMemoryContextScope.ForThread(directive.Thread),
                    ["position-fact"] = ShortMemoryContextScope.ForPositionFact(),
                    ["thread-other"] = ShortMemoryContextScope.ForThread(unrelatedThread),
                    ["task-other"] = ShortMemoryContextScope.ForTask(
                        directive.Thread,
                        unrelatedTaskId),
                })));

        var request = AiDirectivePrompt.CreateInitialRequest(
            AiDirectiveExecutionContext.From(processingRequest));

        Assert.Contains("eligible-task-memory", request.Content, StringComparison.Ordinal);
        Assert.Contains("eligible-thread-memory", request.Content, StringComparison.Ordinal);
        Assert.Contains("eligible-position-fact", request.Content, StringComparison.Ordinal);
        Assert.Contains("current-task-title", request.Content, StringComparison.Ordinal);
        Assert.Contains("related-task-title", request.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("unscoped-memory-must-not-leak", request.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("other-thread-memory-must-not-leak", request.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("other-task-memory-must-not-leak", request.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("unrelated-task-title", request.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(unrelatedHistory.ToString(), request.Content, StringComparison.Ordinal);
        AssertContainsInOrder(request.Content, "eligible-task-memory", "eligible-thread-memory");
        AssertContainsInOrder(request.Content, "eligible-thread-memory", "eligible-position-fact");
        AssertContainsInOrder(request.Content, "current-task-title", "related-task-title");
    }

    [Fact]
    public void Context_selector_uses_utf8_budget_without_truncating_and_continues_after_oversized_entry()
    {
        var processingRequest = Request(
            includeOptionalContext: true,
            stateFactory: (directive, occupant, stamp) => PositionState.Restore(new PositionSnapshot(
                At,
                occupant,
                OccupantType.AiAgent,
                shortMemory: new Dictionary<string, string>
                {
                    ["a-large"] = new string('é', 32),
                    ["b-small"] = "ok",
                },
                lastConfigurationStamp: stamp,
                shortMemoryContextScopes: new Dictionary<string, ShortMemoryContextScope>
                {
                    ["a-large"] = ShortMemoryContextScope.ForThread(directive.Thread),
                    ["b-small"] = ShortMemoryContextScope.ForThread(directive.Thread),
                })));
        var context = AiDirectiveExecutionContext.From(processingRequest);
        var small = context.ShortMemory.Single(entry => entry.Key == "b-small");
        var budget = AiDirectiveContextLines.Utf8Cost(
            AiDirectiveContextLines.ShortMemory(small));

        var selected = AiDirectiveContextSelector.Select(context, budget);

        Assert.Equal(["b-small"], selected.ShortMemory.Select(entry => entry.Key).ToArray());
        Assert.Equal(budget, selected.UsedUtf8Bytes);
        Assert.True(
            AiDirectiveContextLines.Utf8Cost(AiDirectiveContextLines.ShortMemory(
                context.ShortMemory.Single(entry => entry.Key == "a-large"))) > budget);
    }

    [Fact]
    public void Context_selector_applies_utf8_budget_to_materialized_lineage_without_truncation()
    {
        OrgDirective? expected = null;
        var processingRequest = Request(
            includeOptionalContext: true,
            stateFactory: (current, occupant, stamp) =>
            {
                var grandparentId = DirectiveId.From(
                    Guid.Parse("cccccccc-0000-0000-0000-000000000098"));
                var grandparent = new OrgDirective(
                    MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000098")),
                    current.OrganizationId,
                    current.From,
                    current.To,
                    current.Thread,
                    Priority.Normal,
                    1,
                    At.AddMinutes(-2),
                    null,
                    grandparentId,
                    null,
                    "Oversized ancestor",
                    new string('é', 128));
                expected = new OrgDirective(
                    MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000099")),
                    current.OrganizationId,
                    current.From,
                    current.To,
                    current.Thread,
                    Priority.Normal,
                    1,
                    At.AddMinutes(-1),
                    null,
                    current.ParentDirectiveId!,
                    grandparentId,
                    "Small parent",
                    "ok");
                var history = new OrgMessage[] { grandparent, expected };
                return PositionState.Restore(new PositionSnapshot(
                    At,
                    occupant,
                    OccupantType.AiAgent,
                    recentHistory: history.Select(message => message.Id),
                    lastConfigurationStamp: stamp,
                    materializedHistory: history));
            });
        var context = AiDirectiveExecutionContext.From(processingRequest);
        var budget = AiDirectiveContextLines.Utf8Cost(
            AiDirectiveContextLines.MaterializedMessage(expected!));

        var selected = AiDirectiveContextSelector.Select(context, budget);

        Assert.Equal(new[] { expected }, selected.MaterializedHistory);
        Assert.Equal(budget, selected.UsedUtf8Bytes);
    }

    [Fact]
    public void CreateInitialRequest_projects_only_correlated_materialized_history_and_task_progress()
    {
        var parentDirectiveId = DirectiveId.From(
            Guid.Parse("cccccccc-0000-0000-0000-000000000099"));
        var siblingDirectiveId = DirectiveId.From(
            Guid.Parse("cccccccc-0000-0000-0000-000000000098"));
        var taskId = PositionTaskId.From(
            Guid.Parse("00000000-0000-0000-0000-000000000040"));
        var processingRequest = Request(
            includeOptionalContext: true,
            stateFactory: (current, occupant, stamp) =>
            {
                var parent = new OrgDirective(
                    MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000099")),
                    current.OrganizationId,
                    current.From,
                    current.To,
                    current.Thread,
                    Priority.High,
                    schemaVersion: 1,
                    sentAt: At.AddMinutes(-3),
                    deadline: null,
                    parentDirectiveId,
                    parentDirectiveId: null,
                    objective: "Inspect the original checkout incident.",
                    context: "Original grounded evidence: café checkout failed.");
                var sibling = new OrgDirective(
                    MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000098")),
                    current.OrganizationId,
                    current.From,
                    current.To,
                    current.Thread,
                    Priority.Normal,
                    schemaVersion: 1,
                    sentAt: At.AddMinutes(-2),
                    deadline: null,
                    siblingDirectiveId,
                    parentDirectiveId: null,
                    objective: "Unrelated sibling objective must not leak.",
                    context: "Sibling-only secret context.");
                var report = new Report(
                    MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000097")),
                    current.OrganizationId,
                    current.To,
                    current.From,
                    current.Thread,
                    Priority.Normal,
                    schemaVersion: 1,
                    sentAt: At.AddMinutes(-1),
                    deadline: null,
                    parentDirectiveId,
                    ReportKind.Progress,
                    "Materialized progress from the parent lineage.");
                var foreign = new OrgDirective(
                    MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000096")),
                    current.OrganizationId,
                    current.From,
                    current.To,
                    ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000096")),
                    Priority.Normal,
                    schemaVersion: 1,
                    sentAt: At.AddMinutes(-4),
                    deadline: null,
                    DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000096")),
                    parentDirectiveId: null,
                    objective: "Foreign thread objective must not leak.",
                    context: "Foreign thread secret context.");
                var history = new OrgMessage[] { foreign, parent, sibling, report };

                return PositionState.Restore(new PositionSnapshot(
                    At,
                    occupant,
                    OccupantType.AiAgent,
                    openTasks:
                    [
                        new PersistedTask(
                            taskId,
                            current.Thread,
                            "Continue the checkout investigation",
                            Priority.High,
                            At.AddMinutes(-3),
                            causedBy: parent.Id,
                            latestProgress: "Reproduced with multibyte payload: ação."),
                    ],
                    shortMemory: new Dictionary<string, string>
                    {
                        ["parent-result"] = "Accepted bounded parent result.",
                        ["sibling-result"] = "Sibling result must not leak.",
                        ["raw-provider-output"] = "Provider reasoning must not leak.",
                    },
                    recentHistory: history.Select(message => message.Id),
                    lastConfigurationStamp: stamp,
                    shortMemoryContextScopes: new Dictionary<string, ShortMemoryContextScope>
                    {
                        ["parent-result"] = ShortMemoryContextScope.ForDirective(
                            current.Thread,
                            parentDirectiveId,
                            taskId: taskId),
                        ["sibling-result"] = ShortMemoryContextScope.ForDirective(
                            current.Thread,
                            siblingDirectiveId),
                    },
                    materializedHistory: history));
            });

        var context = AiDirectiveExecutionContext.From(processingRequest);
        var first = AiDirectivePrompt.CreateInitialRequest(context);
        var second = AiDirectivePrompt.CreateInitialRequest(context);

        Assert.Equal(first.Content, second.Content);
        Assert.Contains("Original grounded evidence: café checkout failed.", first.Content, StringComparison.Ordinal);
        Assert.Contains("Materialized progress from the parent lineage.", first.Content, StringComparison.Ordinal);
        Assert.Contains("Reproduced with multibyte payload: ação.", first.Content, StringComparison.Ordinal);
        Assert.Contains("Accepted bounded parent result.", first.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Sibling-only secret context.", first.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Sibling result must not leak.", first.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreign thread secret context.", first.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Provider reasoning must not leak.", first.Content, StringComparison.Ordinal);

        var selected = AiDirectiveContextSelector.Select(context);
        Assert.True(selected.UsedUtf8Bytes <= selected.BudgetUtf8Bytes);
        Assert.Equal(
            selected.UsedUtf8Bytes,
            selected.OpenTasks.Sum(task => AiDirectiveContextLines.Utf8Cost(
                AiDirectiveContextLines.Task(task))) +
            selected.ShortMemory.Sum(entry => AiDirectiveContextLines.Utf8Cost(
                AiDirectiveContextLines.ShortMemory(entry))) +
            selected.MaterializedHistory.Sum(message => AiDirectiveContextLines.Utf8Cost(
                AiDirectiveContextLines.MaterializedMessage(message))));
    }

    [Fact]
    public void CreateInitialRequest_canonicalizes_and_isolates_acting_under_vocabularies()
    {
        var first = AiDirectivePrompt.CreateInitialRequest(
            AiDirectiveExecutionContext.From(Request(
                includeOptionalContext: true,
                canDecide: ["zeta.scope", "alpha.scope", "zeta.scope"])));
        var second = AiDirectivePrompt.CreateInitialRequest(
            AiDirectiveExecutionContext.From(Request(
                includeOptionalContext: true,
                canDecide: ["other.scope"])));

        AssertActingUnderSchema(Assert.Single(first.Tools), "alpha.scope", "zeta.scope");
        AssertActingUnderSchema(Assert.Single(second.Tools), "other.scope");
        Assert.Contains(
            "Allowed \"acting_under\" values for this position: \"alpha.scope\", \"zeta.scope\".",
            first.SystemInstruction,
            StringComparison.Ordinal);
        Assert.DoesNotContain("other.scope", first.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains(
            "Allowed \"acting_under\" values for this position: \"other.scope\".",
            second.SystemInstruction,
            StringComparison.Ordinal);
        Assert.DoesNotContain("alpha.scope", second.SystemInstruction, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateInitialRequest_omits_authorized_tools_when_can_decide_is_empty()
    {
        var request = AiDirectivePrompt.CreateInitialRequest(
            AiDirectiveExecutionContext.From(Request(
                includeOptionalContext: false,
                canDecide: [],
                tools: [new ToolConfiguration("jira", ["issues/read"])])));

        Assert.Empty(request.Tools);
        Assert.Contains("- jira: issues/read", request.Content, StringComparison.Ordinal);
        Assert.Contains(
            "Allowed \"acting_under\" values for this position: <empty>.",
            request.SystemInstruction,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_schema_composition_preserves_functional_schema_without_mutating_source()
    {
        var ticketProperty = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "string",
        };
        var sourceProperties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ticket"] = ticketProperty,
        };
        var sourceRequired = new[] { "ticket" };
        var source = new AiToolDefinition(
            "jira",
            "Looks up a ticket.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "object",
                ["properties"] = sourceProperties,
                ["required"] = sourceRequired,
                ["additionalProperties"] = false,
            });

        var composed = AiToolActingUnderSchema.Compose(
            source,
            [
                AuthorityKey.From("zeta.scope"),
                AuthorityKey.From("alpha.scope"),
                AuthorityKey.From("zeta.scope"),
            ]);

        Assert.Equal("jira", composed.Name);
        Assert.Equal("Looks up a ticket.", composed.Description);
        Assert.Equal("object", composed.ParametersSchema["type"]);
        Assert.Equal(false, composed.ParametersSchema["additionalProperties"]);
        var composedProperties = SchemaObject(composed.ParametersSchema["properties"]);
        Assert.Same(ticketProperty, composedProperties["ticket"]);
        AssertActingUnderProperty(composedProperties, "alpha.scope", "zeta.scope");
        Assert.Equal(
            ["ticket", "acting_under"],
            SchemaStrings(composed.ParametersSchema["required"]));

        Assert.False(sourceProperties.ContainsKey("acting_under"));
        Assert.Equal(["ticket"], sourceRequired);
        Assert.Same(sourceProperties, source.ParametersSchema["properties"]);
        Assert.Same(sourceRequired, source.ParametersSchema["required"]);
    }

    [Fact]
    public void Tool_schema_composition_rejects_reserved_collision_and_incompatible_shapes()
    {
        IReadOnlyDictionary<string, object?>[] incompatibleSchemas =
        [
            new Dictionary<string, object?> { ["type"] = "array" },
            new Dictionary<string, object?> { ["properties"] = new[] { "not-an-object" } },
            new Dictionary<string, object?> { ["required"] = new object?[] { "ticket", 7 } },
            new Dictionary<string, object?>
            {
                ["properties"] = new Dictionary<string, object?>
                {
                    ["acting_under"] = new Dictionary<string, object?> { ["type"] = "string" },
                },
            },
            new Dictionary<string, object?> { ["required"] = new[] { "acting_under" } },
        ];

        foreach (var schema in incompatibleSchemas)
        {
            var tool = new AiToolDefinition("jira", "Looks up a ticket.", schema);

            Assert.Throws<InvalidOperationException>(() =>
                AiToolActingUnderSchema.Compose(
                    tool,
                    [AuthorityKey.From("delivery.bug-triage")]));
        }
    }

    [Fact]
    public async Task AiAgentActor_invokes_gateway_after_initial_prompt_with_policy_and_limits()
    {
        var processingRequest = Request(
            includeOptionalContext: true,
            maxIterations: 3);
        var invoker = new RecordingInvoker();
        var system = ActorSystem.Create($"ai-agent-prompt-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(processingRequest.Occupant, invoker)),
                "agent");

            actor.Tell(processingRequest);

            var promptResult = await WaitForPromptAsync(actor, processingRequest.CorrelationId);
            var invocation = await invoker.Invoked.Task.WaitAsync(Timeout());
            var snapshotResult = await actor.Ask<AiDirectiveProcessingSnapshotQueryResult>(
                new GetAiDirectiveProcessingSnapshot(processingRequest.CorrelationId),
                Timeout());
            var gatewayResult = await actor.Ask<AiDirectiveGatewayInvocationQueryResult>(
                new GetAiDirectiveGatewayInvocation(processingRequest.CorrelationId),
                Timeout());
            var gatewayRequest = invocation.Request;

            Assert.True(promptResult.Found);
            Assert.Equal(processingRequest.CorrelationId, promptResult.CorrelationId);
            Assert.Equal(processingRequest.MessageId, promptResult.Request!.MessageId);
            Assert.Contains("Return JSON only", promptResult.Request.SystemInstruction, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "## Evaluation",
                promptResult.Request.SystemInstruction,
                StringComparison.Ordinal);
            Assert.Same(promptResult.Request, gatewayRequest);
            Assert.Equal(1, invoker.CallCount);
            Assert.Equal(processingRequest.CorrelationId, invocation.CorrelationId);
            Assert.Equal(processingRequest.OrganizationId, gatewayRequest.OrganizationId);
            Assert.Equal(processingRequest.PositionId, gatewayRequest.PositionId);
            Assert.Equal(processingRequest.ThreadId, gatewayRequest.ThreadId);
            Assert.Equal(processingRequest.MessageId, gatewayRequest.MessageId);
            Assert.Equal(TimeSpan.FromSeconds(15), gatewayRequest.Timeout);
            Assert.Equal(256, gatewayRequest.ModelParameters.MaxOutputTokens);
            Assert.Equal("3", gatewayRequest.Metadata["max_iterations"]);
            Assert.False(invoker.CancellationToken.CanBeCanceled);

            var policy = Assert.IsType<AiGatewayPolicy>(gatewayRequest.Policy);
            Assert.True(policy.HasAvailableBudget);
            Assert.Equal(TimeSpan.FromSeconds(15), policy.MaxTimeout);
            Assert.Equal(256, policy.MaxOutputTokens);
            var authorizedModel = Assert.Single(policy.AuthorizedModels);
            Assert.Equal("stub", authorizedModel.ProviderId);
            Assert.Equal("triage", authorizedModel.ModelId);
            Assert.Equal([AiProcessingMode.Batch], policy.AllowedProcessingModes.ToArray());
            Assert.Equal(["jira"], policy.AuthorizedTools.ToArray());
            var gatewayTool = Assert.Single(gatewayRequest.Tools);
            Assert.Equal("jira", gatewayTool.Name);
            Assert.Equal(
                "Authorized HIVE connector 'jira' with scopes: issues/read, issues/comment.",
                gatewayTool.Description);
            AssertActingUnderSchema(gatewayTool, "bug.triage");
            Assert.True(gatewayResult.Found);
            Assert.True(gatewayResult.Result!.IsSuccess);
            Assert.Equal(processingRequest.CorrelationId, gatewayResult.Result.CorrelationId);
            Assert.Contains("\"intent\":\"Report\"", gatewayResult.Result.Response.Text, StringComparison.Ordinal);

            Assert.Equal(AiDirectiveProcessingStatus.ResultEmitted, snapshotResult.Snapshot!.Status);
            Assert.Equal(
                [
                    AiDirectiveProcessingStatus.Received,
                    AiDirectiveProcessingStatus.ContextAssembled,
                    AiDirectiveProcessingStatus.GatewayRequested,
                    AiDirectiveProcessingStatus.ResponseInterpreted,
                    AiDirectiveProcessingStatus.ResultEmitted,
                ],
                snapshotResult.Snapshot.History.Select(transition => transition.Status).ToArray());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_fails_processing_from_structured_gateway_timeout()
    {
        var processingRequest = Request(
            includeOptionalContext: true,
            timeout: TimeSpan.FromMilliseconds(50));
        var invoker = new TimeoutResponseInvoker();
        var system = ActorSystem.Create($"ai-agent-gateway-timeout-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(processingRequest.Occupant, invoker)),
                "agent");

            actor.Tell(processingRequest);

            var snapshotResult = await WaitForSnapshotAsync(actor, processingRequest.CorrelationId);

            Assert.Equal(AiDirectiveProcessingStatus.Failed, snapshotResult.Snapshot!.Status);
            Assert.Contains("timeout", snapshotResult.Snapshot.TerminalReason, StringComparison.OrdinalIgnoreCase);
            Assert.False(invoker.CancellationToken.CanBeCanceled);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_fails_closed_when_identity_prompt_is_not_resolved()
    {
        var processingRequest = Request(includeOptionalContext: false, includeIdentityPrompt: false);
        var invoker = new ThrowingInvoker();
        var system = ActorSystem.Create($"ai-agent-prompt-fail-closed-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(processingRequest.Occupant, invoker)),
                "agent");

            actor.Tell(processingRequest);

            var snapshotResult = await WaitForSnapshotAsync(actor, processingRequest.CorrelationId);
            var promptResult = await actor.Ask<AiDirectiveInitialPromptQueryResult>(
                new GetAiDirectiveInitialPrompt(processingRequest.CorrelationId),
                Timeout());

            Assert.Equal(AiDirectiveProcessingStatus.Failed, snapshotResult.Snapshot!.Status);
            Assert.Contains(
                "identity prompt",
                snapshotResult.Snapshot.TerminalReason,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(promptResult.Found);
            Assert.Equal(0, invoker.CallCount);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_returns_missing_bug_triage_prompt_for_unknown_correlation()
    {
        var system = ActorSystem.Create($"ai-agent-prompt-missing-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    OccupantId.From("agent-7"),
                    new ThrowingInvoker())),
                "agent");

            var result = await actor.Ask<AiDirectiveInitialPromptQueryResult>(
                new GetAiDirectiveInitialPrompt("directive:unknown"),
                Timeout());

            Assert.False(result.Found);
            Assert.Equal("directive:unknown", result.CorrelationId);
            Assert.Null(result.Request);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_returns_missing_gateway_invocation_for_unknown_correlation()
    {
        var system = ActorSystem.Create($"ai-agent-gateway-missing-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    OccupantId.From("agent-7"),
                    new ThrowingInvoker())),
                "agent");

            var result = await actor.Ask<AiDirectiveGatewayInvocationQueryResult>(
                new GetAiDirectiveGatewayInvocation("directive:unknown"),
                Timeout());

            Assert.False(result.Found);
            Assert.Equal("directive:unknown", result.CorrelationId);
            Assert.Null(result.Result);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static async Task<AiDirectiveInitialPromptQueryResult> WaitForPromptAsync(
        IActorRef actor,
        string correlationId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await actor.Ask<AiDirectiveInitialPromptQueryResult>(
                new GetAiDirectiveInitialPrompt(correlationId),
                TimeSpan.FromSeconds(1));
            if (result.Found)
            {
                return result;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("AI directive initial prompt was not built.");
    }

    private static async Task<AiDirectiveProcessingSnapshotQueryResult> WaitForSnapshotAsync(
        IActorRef actor,
        string correlationId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await actor.Ask<AiDirectiveProcessingSnapshotQueryResult>(
                new GetAiDirectiveProcessingSnapshot(correlationId),
                TimeSpan.FromSeconds(1));
            if (result.Found)
            {
                return result;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("AI directive processing snapshot was not recorded.");
    }

    private static AiDirectiveProcessingRequest Request(
        bool includeOptionalContext,
        bool includeIdentityPrompt = true,
        TimeSpan? timeout = null,
        int? maxIterations = null,
        IReadOnlyList<string>? canDecide = null,
        IReadOnlyList<ToolConfiguration>? tools = null,
        Func<OrgDirective, OccupantId, PositionConfigurationStamp, PositionState>? stateFactory = null,
        TimeSpan? executionTimeout = null,
        DirectiveExecutionPolicyRequest? executionPolicy = null,
        DirectiveExecutionPolicyCapability? executionCapability = null)
    {
        var entity = PositionEntityId.From(
            OrganizationId.From("acme"),
            PositionId.From("triage-agent"));
        var occupant = OccupantId.From("agent-7");
        var stamp = new PositionConfigurationStamp(11, "sha256:t05");
        var parentDirective = includeOptionalContext
            ? DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000099"))
            : null;
        var directive = new OrgDirective(
            MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000904")),
            entity.Organization,
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(entity.Position),
            ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000904")),
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: includeOptionalContext ? At.AddHours(2) : null,
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000904")),
            parentDirective,
            objective: "Triage checkout regression",
            context: "Customer reports checkout failures.",
            executionPolicy: executionPolicy);
        IReadOnlyList<ToolConfiguration> effectiveTools = tools ??
            (includeOptionalContext
                ? [new ToolConfiguration("jira", ["issues/read", "issues/comment"])]
                : []);
        IReadOnlyList<string> effectiveCanDecide = canDecide ??
            (includeOptionalContext ? ["bug.triage"] : []);

        var configuration = new PositionRuntimeConfiguration(
            stamp,
            entity.Organization,
            entity.Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: includeOptionalContext ? PositionId.From("delivery-lead") : null,
                name: "Bug triage",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: includeIdentityPrompt ? "triage-v1" : null,
                tools: effectiveTools,
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "triage"),
                    new AiModelParameters(maxOutputTokens: 256),
                    timeout: timeout ?? TimeSpan.FromSeconds(15),
                    processingMode: AiProcessingMode.Batch,
                    maxIterations: maxIterations,
                    limitsVersion: executionTimeout is null
                        ? AiPositionRuntimeConfiguration.LegacyLimitsVersion
                        : AiPositionRuntimeConfiguration.CurrentLimitsVersion,
                    executionTimeout: executionTimeout,
                    directiveExecutionPolicy: executionCapability),
                identityPrompt: includeIdentityPrompt
                    ? new IdentityPromptRuntimeConfiguration(
                        "triage-v1",
                        "prompts/triage-v1.md",
                        "You are responsible for triaging incoming bugs.")
                    : null),
            new PositionAuthorityRuntimeConfiguration(
                effectiveCanDecide,
                overrides: includeOptionalContext
                    ? [
                        new PositionAuthorityOverrideRuntimeConfiguration(
                            "comms.external-official",
                            ActionDomainGate.HumanApproval,
                            "delivery-lead"),
                    ]
                    : null));

        var state = stateFactory?.Invoke(directive, occupant, stamp) ?? (includeOptionalContext
            ? PositionState.Restore(new PositionSnapshot(
                At,
                occupant,
                OccupantType.AiAgent,
                openTasks:
                [
                    new PersistedTask(
                        PositionTaskId.From(Guid.Parse("00000000-0000-0000-0000-000000000002")),
                        directive.Thread,
                        "Second task",
                        Priority.Normal,
                        At),
                    new PersistedTask(
                        PositionTaskId.From(Guid.Parse("00000000-0000-0000-0000-000000000001")),
                        directive.Thread,
                        "First task",
                        Priority.High,
                        At),
                ],
                shortMemory: new Dictionary<string, string>
                {
                    ["zeta"] = "last",
                    ["alpha"] = "first",
                },
                recentHistory:
                [
                    MessageId.From(Guid.Parse("dddddddd-0000-0000-0000-000000000002")),
                    MessageId.From(Guid.Parse("dddddddd-0000-0000-0000-000000000001")),
                ],
                lastConfigurationStamp: stamp,
                shortMemoryContextScopes: new Dictionary<string, ShortMemoryContextScope>
                {
                    ["zeta"] = ShortMemoryContextScope.ForThread(directive.Thread),
                    ["alpha"] = ShortMemoryContextScope.ForThread(directive.Thread),
                }))
            : PositionState.Restore(new PositionSnapshot(At, lastConfigurationStamp: stamp)));

        return AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            state,
            occupant,
            directive);
    }

    private static void AssertActingUnderSchema(
        AiToolDefinition tool,
        params string[] expectedVocabulary)
    {
        Assert.Equal("object", tool.ParametersSchema["type"]);
        AssertActingUnderProperty(
            SchemaObject(tool.ParametersSchema["properties"]),
            expectedVocabulary);
        Assert.Equal(
            ["acting_under"],
            SchemaStrings(tool.ParametersSchema["required"]));
    }

    private static void AssertActingUnderProperty(
        IReadOnlyDictionary<string, object?> properties,
        params string[] expectedVocabulary)
    {
        var actingUnder = SchemaObject(properties["acting_under"]);
        Assert.Equal("string", actingUnder["type"]);
        Assert.Equal(expectedVocabulary, SchemaStrings(actingUnder["enum"]));
    }

    private static IReadOnlyDictionary<string, object?> SchemaObject(object? value) =>
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(value);

    private static string[] SchemaStrings(object? value) =>
        Assert.IsAssignableFrom<IEnumerable<string>>(value).ToArray();

    private static string[] ReportKindVocabulary(AiOutputConstraint constraint) =>
        constraint.JsonSchema
            .GetProperty("properties")
            .GetProperty(AiDirectiveDecisionSchema.DecisionProperty)
            .GetProperty("anyOf")
            .EnumerateArray()
            .Single(branch => branch.GetProperty("properties")
                .GetProperty(AiDirectiveDecisionSchema.IntentProperty)
                .GetProperty("const")
                .GetString() == "Report")
            .GetProperty("properties")
            .GetProperty(AiDirectiveDecisionSchema.ReportPayloadProperty)
            .GetProperty("properties")
            .GetProperty(AiDirectiveDecisionSchema.ReportKindField)
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();

    private static string[] OutcomeProposalIntentVocabulary(AiOutputConstraint constraint) =>
        constraint.JsonSchema
            .GetProperty("properties")
            .GetProperty(AiDirectiveOutcomeProposalEnvelope.PropertyName)
            .GetProperty("properties")
            .GetProperty(OutcomeProposalConstraint.ProposalProperty)
            .GetProperty("anyOf")
            .EnumerateArray()
            .Select(branch => branch.GetProperty("properties")
                .GetProperty(OutcomeProposalConstraint.ProposedIntentProperty)
                .GetProperty("const")
                .GetString()!)
            .ToArray();

    private static void AssertContainsInOrder(string text, string first, string second)
    {
        var firstIndex = text.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = text.IndexOf(second, StringComparison.Ordinal);

        Assert.True(firstIndex >= 0, $"Expected to find '{first}'.");
        Assert.True(secondIndex >= 0, $"Expected to find '{second}'.");
        Assert.True(firstIndex < secondIndex, $"Expected '{first}' before '{second}'.");
    }

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class ThrowingInvoker : IAiAgentGatewayInvoker
    {
        public int CallCount { get; private set; }

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("Gateway must not be invoked by T05.");
        }
    }

    private sealed class RecordingInvoker : IAiAgentGatewayInvoker
    {
        public int CallCount { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public TaskCompletionSource<AiAgentGatewayInvocation> Invoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CancellationToken = cancellationToken;
            Invoked.TrySetResult(invocation);

            return Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                AiGatewayResponse.Succeeded(
                    invocation.Request.OrganizationId,
                    invocation.Request.PositionId,
                    invocation.Request.ThreadId,
                    invocation.Request.MessageId,
                    "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Done\",\"body\":\"Completed.\"}}",
                    AiFinishReason.Stop)));
        }
    }

    private sealed class TimeoutResponseInvoker : IAiAgentGatewayInvoker
    {
        public CancellationToken CancellationToken { get; private set; }

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            CancellationToken = cancellationToken;

            return Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                AiGatewayResponse.Failed(new AiGatewayError(
                    invocation.Request.OrganizationId,
                    invocation.Request.PositionId,
                    invocation.Request.ThreadId,
                    invocation.Request.MessageId,
                    AiGatewayErrorCode.Timeout,
                    "AI gateway provider reached its internal deadline.",
                    isRetryable: true,
                    invocation.Request.Provider))));
        }
    }
}
