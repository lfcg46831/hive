using System.Text.Json;
using Hive.Domain.Identity;
using Hive.Domain.Outcomes;

namespace Hive.Tests;

public sealed class OutcomeVerifierContractTests
{
    [Fact]
    public void Classification_constraint_and_parser_share_one_closed_vocabulary()
    {
        Assert.Equal(
            OutcomeKindContract.WireValues,
            OutcomeVerifierClassificationContract.WireValues);

        using var schema = JsonDocument.Parse(
            OutcomeVerifierConstraint.OutputConstraint.JsonSchema.GetRawText());
        var classificationSchema = schema.RootElement
            .GetProperty("properties")
            .GetProperty(OutcomeVerifierConstraint.ClassificationProperty);
        Assert.Equal(
            OutcomeVerifierClassificationContract.WireValues,
            classificationSchema
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()));
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());

        foreach (var classification in Enum.GetValues<OutcomeVerifierClassification>())
        {
            var output = $$"""
                {
                  "schema_version": 1,
                  "classification": "{{OutcomeVerifierClassificationContract.ToWireValue(classification)}}"
                }
                """;

            var parsed = OutcomeVerifierParser.Parse(output);

            Assert.True(parsed.IsSuccess);
            Assert.Equal(classification, parsed.Classification);
            Assert.Empty(parsed.Errors);
        }
    }

    [Fact]
    public void System_instruction_states_the_provider_neutral_outcome_preconditions()
    {
        var instruction = OutcomeVerifierConstraint.SystemInstruction;

        Assert.Contains(
            "bounded proposed artifact",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Execution facts and objective policy gates are authoritative",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Report.Progress requires verifiable_progress=true",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "pending_actions=true, autonomous_action_available=true",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Report.Done requires pending_actions=false",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "proposal.semantic_completion_candidate is a deterministic, non-authoritative summary",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Report.Done is structurally compatible without completion_state=Satisfied",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApprovalRequired requires human_approval_required=true or approval_pending=true",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "bounded context that semantically requires the superior to decide, authorize, or choose now",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "observed_policy_triggers is a closed authoritative fact",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Security, privacy, compliance, financial, or safety subject matter does not alone require Escalation",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not alone require Escalation",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains("return Undetermined", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("triage", instruction, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, "empty-response", "$")]
    [InlineData("not-json", "invalid-json", "$")]
    [InlineData("[]", "top-level-object-required", "$")]
    [InlineData("{\"schema_version\":1}", "required-field", "classification")]
    [InlineData("{\"schema_version\":2,\"classification\":\"Escalation\"}", "invalid-schema-version", "schema_version")]
    [InlineData("{\"schema_version\":1,\"classification\":\"Report\"}", "invalid-vocabulary", "classification")]
    [InlineData("{\"schema_version\":1,\"classification\":\"Escalation\",\"reasoning\":\"hidden\"}", "unknown-field", "$")]
    [InlineData("{\"schema_version\":1,\"classification\":\"Escalation\",\"classification\":\"Report.Done\"}", "duplicate-field", "classification")]
    public void Parser_rejects_non_contract_output_without_interpreting_text(
        string? output,
        string code,
        string path)
    {
        var parsed = OutcomeVerifierParser.Parse(output);

        Assert.False(parsed.IsSuccess);
        Assert.Contains(parsed.Errors, error => error.Code == code && error.Path == path);
    }

    [Fact]
    public void Verification_context_is_bounded_canonical_and_has_no_tool_or_memory_surface()
    {
        var source = new List<OutcomeVerificationContextEntry>
        {
            new("proposal.message", "Candidate response."),
            new("directive.objective", "Assess the request."),
        };
        var context = Context(source);
        source.Clear();

        Assert.Equal(
            ["directive.objective", "proposal.message"],
            context.Entries.Select(entry => entry.Reference));
        Assert.Equal(TimeSpan.FromSeconds(5), context.Timeout);

        var publicProperties = new[]
        {
            typeof(OutcomeVerificationContext),
            typeof(OutcomeVerificationArtifact),
            typeof(OutcomeVerificationRequest),
            typeof(OutcomeVerifierResult),
        }.SelectMany(type => type.GetProperties());
        Assert.DoesNotContain(publicProperties, property =>
            new[] { "tool", "memory", "provider", "model", "reasoning", "raw" }
                .Any(term => property.Name.Contains(term, StringComparison.OrdinalIgnoreCase)));

        Assert.Throws<ArgumentException>(() => Context(
            Enumerable.Range(0, OutcomeVerificationContext.MaximumEntries + 1)
                .Select(index => new OutcomeVerificationContextEntry(
                    $"context.{index}",
                    "value"))));
        Assert.Throws<ArgumentException>(() => Context(
        [
            new OutcomeVerificationContextEntry("context.same", "one"),
            new OutcomeVerificationContextEntry("context.same", "two"),
        ]));
        Assert.Throws<ArgumentException>(() => Context(
        [
            new OutcomeVerificationContextEntry(
                "context.large",
                new string('x', OutcomeVerificationContext.MaximumUtf8Bytes + 1)),
        ]));
    }

    [Fact]
    public void Verification_artifact_is_bounded_canonical_and_contains_only_semantic_fields()
    {
        var source = new List<OutcomeVerificationArtifactEntry>
        {
            new("report.summary", "Assessment complete."),
            new("report.body", "Severity, impact, missing information, and next action."),
        };

        var artifact = new OutcomeVerificationArtifact(OutcomeKind.ReportDone, source);
        source.Clear();

        Assert.Equal(OutcomeKind.ReportDone, artifact.Kind);
        Assert.Equal(
            ["report.body", "report.summary"],
            artifact.Entries.Select(entry => entry.Reference));
        Assert.InRange(artifact.Utf8Bytes, 1, OutcomeVerificationArtifact.MaximumUtf8Bytes);
        Assert.Throws<ArgumentException>(() => new OutcomeVerificationArtifact(
            OutcomeKind.ContinueWork,
            [new("message.body", "Still working.")]));
        Assert.Throws<ArgumentException>(() => new OutcomeVerificationArtifact(
            OutcomeKind.ReportDone,
            Enumerable.Range(0, OutcomeVerificationArtifact.MaximumEntries + 1)
                .Select(index => new OutcomeVerificationArtifactEntry(
                    $"report.field.{index}",
                    "value"))));
        Assert.Throws<ArgumentException>(() => new OutcomeVerificationArtifact(
            OutcomeKind.ReportDone,
            [
                new("report.same", "one"),
                new("report.same", "two"),
            ]));
        Assert.Throws<ArgumentException>(() => new OutcomeVerificationArtifact(
            OutcomeKind.ReportDone,
            [new(
                "report.body",
                new string('x', OutcomeVerificationArtifact.MaximumUtf8Bytes + 1))]));
    }

    [Fact]
    public void Verifier_result_carries_only_a_classification_or_a_closed_failure_status()
    {
        var classified = OutcomeVerifierResult.Classified(
            OutcomeVerifierClassification.Escalation);

        Assert.Equal(OutcomeVerifierResultStatus.Classified, classified.Status);
        Assert.Equal(OutcomeVerifierClassification.Escalation, classified.Classification);
        Assert.Null(OutcomeVerifierResult.Unavailable().Classification);
        Assert.Null(OutcomeVerifierResult.TimedOut().Classification);
        Assert.Null(OutcomeVerifierResult.InvalidOutput().Classification);
    }

    [Fact]
    public void Verification_request_accepts_only_the_matching_approval_artifact_kind()
    {
        var proposal = new OutcomeProposal(
            OutcomeProposedIntent.ApprovalRequired,
            OutcomeWorkState.Blocked,
            OutcomeRequiredIntervention.HumanApproval,
            [OutcomeBlocker.HumanApproval],
            nextAction: null,
            evidenceReferences: []);
        var approvalArtifact = new OutcomeVerificationArtifact(
            OutcomeKind.ApprovalRequired,
            [new("approval.action", "Approve the requested change.")]);

        var request = new OutcomeVerificationRequest(
            Context([]),
            Facts(),
            new DirectiveExecutionContract(),
            proposal,
            Policy(),
            approvalArtifact);

        Assert.Same(approvalArtifact, request.Artifact);
        Assert.Throws<ArgumentException>(() => new OutcomeVerificationRequest(
            Context([]),
            Facts(),
            new DirectiveExecutionContract(),
            proposal,
            Policy(),
            new OutcomeVerificationArtifact(
                OutcomeKind.Escalation,
                [new("escalation.context", "Choose a mitigation.")])));
    }

    [Fact]
    public void Semantic_completion_eligibility_is_closed_and_requires_grounded_directive_input()
    {
        var eligible = OutcomeSemanticCompletionEligibility.Evaluate(
            SemanticCompletionRequest(
                OutcomeEvidenceSource.DirectiveInput,
                "directive.objective"));
        var ungroundedRuntimeFact = OutcomeSemanticCompletionEligibility.Evaluate(
            SemanticCompletionRequest(
                OutcomeEvidenceSource.RuntimeFact,
                "directive.missing"));
        var structured = OutcomeSemanticCompletionEligibility.Evaluate(
            SemanticCompletionRequest(
                OutcomeEvidenceSource.DirectiveInput,
                "directive.objective",
                withStructuredCriterion: true));

        Assert.True(eligible.IsEligible);
        Assert.Empty(eligible.IneligibilityReasons);
        Assert.False(ungroundedRuntimeFact.IsEligible);
        Assert.Equal(
            [
                OutcomeSemanticCompletionIneligibilityReason
                    .EvidenceSourceNotDirectiveInput,
                OutcomeSemanticCompletionIneligibilityReason
                    .EvidenceReferenceNotInContext,
            ],
            ungroundedRuntimeFact.IneligibilityReasons);
        Assert.Equal(
            [
                "evidence-source-not-directive-input",
                "evidence-reference-not-in-context",
            ],
            ungroundedRuntimeFact.IneligibilityReasons.Select(
                OutcomeSemanticCompletionIneligibilityReasonContract.ToWireValue));
        Assert.Equal(
            [
                OutcomeSemanticCompletionIneligibilityReason
                    .StructuredCompletionCriteriaPresent,
            ],
            structured.IneligibilityReasons);
        Assert.Equal(
            10,
            OutcomeSemanticCompletionIneligibilityReasonContract.WireValues.Length);
    }

    private static OutcomeVerificationRequest SemanticCompletionRequest(
        OutcomeEvidenceSource evidenceSource,
        string evidenceReference,
        bool withStructuredCriterion = false) =>
        new(
            Context(
            [
                new OutcomeVerificationContextEntry(
                    "directive.objective",
                    "Assess the work item."),
            ]),
            new ExecutionFacts(
                iterationCount: 1,
                retryCount: 0,
                deadlineExceeded: false,
                budgetExhausted: false,
                humanApprovalRequired: false,
                approvalPending: false,
                OutcomeDependencyState.Available,
                OutcomeAuthorityState.Authorized,
                OutcomeRoutingState.Available,
                autonomousActionAvailable: false,
                delegationRequired: false,
                pendingActions: false,
                externalInterventionRequired: false,
                verifiableProgress: false,
                responsibilityRetained: true,
                OutcomeCompletionState.NotDeclared),
            withStructuredCriterion
                ? new DirectiveExecutionContract(
                    completionCriteria: [new("criterion.complete", "Complete.")])
                : new DirectiveExecutionContract(),
            new OutcomeProposal(
                OutcomeProposedIntent.ReportDone,
                OutcomeWorkState.Completed,
                OutcomeRequiredIntervention.None,
                blockers: [],
                nextAction: null,
                [new OutcomeEvidenceReference(evidenceSource, evidenceReference)]),
            new OutcomePolicySnapshot(
                "outcome-policy-v1",
                "sha256:semantic-eligibility",
                maximumIterations: 4,
                maximumRetries: 3,
                verifierEnabled: true),
            new OutcomeVerificationArtifact(
                OutcomeKind.ReportDone,
                [new("report.body", "The requested assessment is complete.")]));

    private static OutcomeVerificationContext Context(
        IEnumerable<OutcomeVerificationContextEntry> entries) =>
        new(
            OrganizationId.From("org-verifier"),
            PositionId.From("delivery-lead"),
            ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            DirectiveId.From(Guid.Parse("33333333-3333-3333-3333-333333333333")),
            TimeSpan.FromSeconds(5),
            entries);

    private static ExecutionFacts Facts() =>
        new(
            iterationCount: 1,
            retryCount: 0,
            deadlineExceeded: false,
            budgetExhausted: false,
            humanApprovalRequired: true,
            approvalPending: false,
            OutcomeDependencyState.Available,
            OutcomeAuthorityState.Authorized,
            OutcomeRoutingState.Available,
            autonomousActionAvailable: false,
            delegationRequired: false,
            pendingActions: true,
            externalInterventionRequired: true,
            verifiableProgress: false,
            responsibilityRetained: true,
            OutcomeCompletionState.NotDeclared);

    private static OutcomePolicySnapshot Policy() =>
        new(
            "outcome-policy-v1",
            "sha256:verifier-contract",
            maximumIterations: 4,
            maximumRetries: 3,
            verifierEnabled: true);
}
