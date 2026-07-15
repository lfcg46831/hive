using System.Text.Json;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Evaluation;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

/// <summary>
/// US-F0-13-T12a: the evaluation envelope becomes contractual transport. The negotiated
/// structured constraint gains a required rubric-driven evaluation section, the decision
/// parser captures it as opaque transport, and the result-message factory canonicalizes it
/// into the textual envelope line the projection parser already owns. Everything is driven
/// by rubric data; no organizational function semantics are compiled.
/// </summary>
public sealed class AiDirectiveEvaluationEnvelopeTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly PositionId Superior = PositionId.From("delivery-lead");
    private static readonly PositionId Engineer = PositionId.From("engineer");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000912"));
    private static readonly MessageId IncomingMessage =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000912"));
    private static readonly DirectiveId IncomingDirective =
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000912"));

    private const string CanonicalEnvelopeJson =
        "{\"dimensions\":{\"missing-information\":[],\"severity\":[\"high\"]}}";

    // --- Constraint composition ---------------------------------------------------------

    [Fact]
    public void Compose_adds_required_rubric_driven_evaluation_section_to_the_constraint()
    {
        var rubric = EvaluationRubricContract.Load(BugTriageRubricFile, 1);
        var instruction = rubric.BuildInstruction();

        var composed = AiDirectiveEvaluationEnvelope.ComposeOutputConstraint(instruction);

        Assert.Equal(AiDirectiveEvaluationEnvelope.ComposedSchemaName, composed.SchemaName);
        Assert.Equal(AiDirectiveDecisionSchema.SchemaVersion, composed.SchemaVersion);
        Assert.Equal(
            AiDirectiveDecisionSchema.OutputConstraint.AllowedFallbackModes,
            composed.AllowedFallbackModes);

        var root = composed.JsonSchema;
        Assert.Contains(
            AiDirectiveEvaluationEnvelope.PropertyName,
            root.GetProperty("required").EnumerateArray().Select(item => item.GetString()));

        var evaluation = root.GetProperty("properties")
            .GetProperty(AiDirectiveEvaluationEnvelope.PropertyName);
        Assert.False(evaluation.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            [AiDirectiveEvaluationEnvelope.DimensionsPropertyName],
            evaluation.GetProperty("required").EnumerateArray().Select(item => item.GetString()));

        var dimensions = evaluation.GetProperty("properties")
            .GetProperty(AiDirectiveEvaluationEnvelope.DimensionsPropertyName);
        Assert.False(dimensions.GetProperty("additionalProperties").GetBoolean());

        var declared = rubric.Dimensions
            .Where(dimension => dimension.Source == "evaluation-envelope")
            .OrderBy(dimension => dimension.Id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            declared.Select(dimension => dimension.Id),
            dimensions.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            declared.Select(dimension => dimension.Id),
            dimensions.GetProperty("required").EnumerateArray().Select(item => item.GetString()));

        foreach (var dimension in declared)
        {
            var schema = dimensions.GetProperty("properties").GetProperty(dimension.Id);
            Assert.Equal("array", schema.GetProperty("type").GetString());
            Assert.Equal(
                dimension.Labels,
                schema.GetProperty("items").GetProperty("enum")
                    .EnumerateArray().Select(item => item.GetString()));
            if (dimension.ValueKind == "single-label")
            {
                Assert.Equal(1, schema.GetProperty("minItems").GetInt32());
                Assert.Equal(1, schema.GetProperty("maxItems").GetInt32());
            }
            else
            {
                Assert.False(schema.TryGetProperty("minItems", out _));
                Assert.False(schema.TryGetProperty("maxItems", out _));
            }
        }

        // Dimensions derived from HIVE result facts never enter the schema section.
        Assert.DoesNotContain(
            rubric.Dimensions.Where(dimension => dimension.Source == "result-message-kind"),
            dimension => dimensions.GetProperty("properties").TryGetProperty(dimension.Id, out _));
    }

    [Fact]
    public void Compose_leaves_the_canonical_decision_constraint_untouched()
    {
        var rubric = EvaluationRubricContract.Load(BugTriageRubricFile, 1);
        _ = AiDirectiveEvaluationEnvelope.ComposeOutputConstraint(rubric.BuildInstruction());

        var baseRoot = AiDirectiveDecisionSchema.OutputConstraint.JsonSchema;
        Assert.False(baseRoot.GetProperty("properties")
            .TryGetProperty(AiDirectiveEvaluationEnvelope.PropertyName, out _));
        Assert.Equal(3, baseRoot.GetProperty("required").GetArrayLength());
    }

    [Fact]
    public void Compose_without_envelope_dimensions_returns_the_base_constraint()
    {
        var instruction = new EvaluationInstruction(1, "appendix without dimensions");

        Assert.Same(
            AiDirectiveDecisionSchema.OutputConstraint,
            AiDirectiveEvaluationEnvelope.ComposeOutputConstraint(instruction));
    }

    [Fact]
    public void Compose_is_invariant_to_the_organizational_function_of_the_rubric()
    {
        // The follow-up coordination fixture has different ids and vocabularies; the same
        // composition must serve it without any code or schema change (US-F0-13-T11e).
        var rubric = EvaluationRubricContract.Load(FollowUpRubricFile, 1);

        var composed = AiDirectiveEvaluationEnvelope.ComposeOutputConstraint(
            rubric.BuildInstruction());

        var dimensions = composed.JsonSchema.GetProperty("properties")
            .GetProperty(AiDirectiveEvaluationEnvelope.PropertyName)
            .GetProperty("properties")
            .GetProperty(AiDirectiveEvaluationEnvelope.DimensionsPropertyName);
        foreach (var dimension in rubric.Dimensions
            .Where(dimension => dimension.Source == "evaluation-envelope"))
        {
            var schema = dimensions.GetProperty("properties").GetProperty(dimension.Id);
            Assert.Equal(
                dimension.Labels,
                schema.GetProperty("items").GetProperty("enum")
                    .EnumerateArray().Select(item => item.GetString()));
        }
    }

    // --- Constraint → projection parser parity ------------------------------------------

    [Fact]
    public void Every_envelope_admitted_by_the_composed_constraint_is_scoreable()
    {
        var rubric = EvaluationRubricContract.Load(BugTriageRubricFile, 1);
        var envelope = rubric.Dimensions
            .Where(dimension => dimension.Source == "evaluation-envelope")
            .ToArray();
        var singleLabel = envelope.Single(dimension => dimension.ValueKind == "single-label");
        var labelSet = envelope.Single(dimension => dimension.ValueKind == "label-set");
        var setChoices = new string[][]
        {
            [],
            [labelSet.Labels[0]],
            [.. labelSet.Labels],
        };

        foreach (var single in singleLabel.Labels)
        {
            foreach (var set in setChoices)
            {
                var payload = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["dimensions"] = new Dictionary<string, object>
                    {
                        [singleLabel.Id] = new[] { single },
                        [labelSet.Id] = set,
                    },
                });
                var text = AiDirectiveEvaluationEnvelope.ComposePayloadText(
                    "Business summary.",
                    payload);

                var projection = EvaluationProjectionParser.Parse(text, "report", rubric);
                var byId = projection.Dimensions.ToDictionary(
                    dimension => dimension.DimensionId,
                    StringComparer.Ordinal);

                Assert.Equal(EvaluationDimensionStatus.Valid, byId[singleLabel.Id].Status);
                Assert.Equal([single], byId[singleLabel.Id].Labels);
                Assert.Equal(EvaluationDimensionStatus.Valid, byId[labelSet.Id].Status);
                Assert.Equal(
                    set.Distinct(StringComparer.Ordinal).OrderBy(label => label, StringComparer.Ordinal),
                    byId[labelSet.Id].Labels);
            }
        }
    }

    // --- Payload canonicalization --------------------------------------------------------

    [Fact]
    public void ComposePayloadText_appends_exactly_one_canonical_envelope_line()
    {
        var text = AiDirectiveEvaluationEnvelope.ComposePayloadText(
            "Business summary.\nSecond line.",
            CanonicalEnvelopeJson);

        Assert.Equal(
            "Business summary.\nSecond line.\n"
                + EvaluationInstruction.EnvelopeMarker
                + CanonicalEnvelopeJson,
            text);
        Assert.Equal(1, CountMarkers(text));
    }

    [Fact]
    public void ComposePayloadText_replaces_model_emitted_marker_lines()
    {
        var text = AiDirectiveEvaluationEnvelope.ComposePayloadText(
            "Summary.\n"
                + EvaluationInstruction.EnvelopeMarker
                + "{\"dimensions\":{\"severity\":[\"stale\"]}}\n"
                + "Trailing note.",
            CanonicalEnvelopeJson);

        Assert.Equal(1, CountMarkers(text));
        Assert.EndsWith(
            EvaluationInstruction.EnvelopeMarker + CanonicalEnvelopeJson,
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("stale", text, StringComparison.Ordinal);
        Assert.Contains("Trailing note.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposePayloadText_with_envelope_only_payload_keeps_a_single_line()
    {
        var text = AiDirectiveEvaluationEnvelope.ComposePayloadText(
            EvaluationInstruction.EnvelopeMarker + "{\"dimensions\":{}}",
            CanonicalEnvelopeJson);

        Assert.Equal(
            EvaluationInstruction.EnvelopeMarker + CanonicalEnvelopeJson,
            text);
    }

    // --- Decision parser transport -------------------------------------------------------

    [Fact]
    public void Parser_captures_the_structured_evaluation_section_when_accepted()
    {
        var result = AiDirectiveDecisionParser.Parse(
            """
            {"schema_version":1,"decision":{"intent":"Report","report":{"kind":"Done","body":"Triage complete."}},"evaluation":{"dimensions":{"severity":["high"],"missing-information":[]}}}
            """,
            acceptEvaluationEnvelope: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            "{\"dimensions\":{\"severity\":[\"high\"],\"missing-information\":[]}}",
            result.EvaluationEnvelopeJson);
    }

    [Fact]
    public void Parser_rejects_the_evaluation_property_outside_evaluation_scopes()
    {
        var result = AiDirectiveDecisionParser.Parse(
            """
            {"schema_version":1,"decision":{"intent":"Report","report":{"kind":"Done","body":"Triage complete."}},"evaluation":{"dimensions":{}}}
            """);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Errors,
            error => error.Code == AiDirectiveDecisionParseDiagnosticContract.UnknownFieldCode
                && error.Path == "$");
    }

    [Fact]
    public void Parser_treats_a_non_object_evaluation_section_as_absent()
    {
        var result = AiDirectiveDecisionParser.Parse(
            """
            {"schema_version":1,"decision":{"intent":"Report","report":{"kind":"Done","body":"Triage complete."}},"evaluation":"not-an-object"}
            """,
            acceptEvaluationEnvelope: true);

        Assert.True(result.IsSuccess);
        Assert.Null(result.EvaluationEnvelopeJson);
    }

    [Fact]
    public void Parser_keeps_rejecting_evaluation_nested_inside_the_decision_envelope()
    {
        var result = AiDirectiveDecisionParser.Parse(
            """
            {"schema_version":1,"decision":{"intent":"Report","report":{"kind":"Done","body":"Triage complete."},"evaluation":{"dimensions":{}}}}
            """,
            acceptEvaluationEnvelope: true);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Errors,
            error => error.Code == AiDirectiveDecisionParseDiagnosticContract.UnknownFieldCode
                && error.Path == AiDirectiveDecisionSchema.DecisionProperty);
    }

    // --- Result message factory ----------------------------------------------------------

    [Fact]
    public void Factory_canonicalizes_the_envelope_into_the_report_body()
    {
        var context = AiDirectiveExecutionContext.From(Request());

        var result = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveReportDecision(ReportKind.Done, "Bug triage is complete."),
            evaluationEnvelopeJson: CanonicalEnvelopeJson);

        var report = Assert.IsType<Report>(result.Message);
        Assert.Equal(
            "Bug triage is complete.\n"
                + EvaluationInstruction.EnvelopeMarker
                + CanonicalEnvelopeJson,
            report.Body);
    }

    [Fact]
    public void Factory_canonicalizes_the_envelope_into_the_escalation_context()
    {
        var context = AiDirectiveExecutionContext.From(Request());

        var result = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveEscalationDecision(
                "Severity cannot be confirmed.",
                "Evidence is contradictory.",
                ["Wait for logs", "Escalate now"]),
            evaluationEnvelopeJson: CanonicalEnvelopeJson);

        var escalation = Assert.IsType<Escalation>(result.Message);
        Assert.Equal(
            "Evidence is contradictory.\n"
                + EvaluationInstruction.EnvelopeMarker
                + CanonicalEnvelopeJson,
            escalation.Context);
        Assert.Equal("Severity cannot be confirmed.", escalation.Issue);
    }

    [Fact]
    public void Factory_preserves_payloads_verbatim_without_a_structured_envelope()
    {
        var context = AiDirectiveExecutionContext.From(Request());

        var result = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveReportDecision(ReportKind.Done, "Bug triage is complete."));

        var report = Assert.IsType<Report>(result.Message);
        Assert.Equal("Bug triage is complete.", report.Body);
    }

    [Fact]
    public void Factory_never_injects_the_envelope_into_child_directives()
    {
        var context = AiDirectiveExecutionContext.From(
            Request(directSubordinates: [Engineer]));

        var result = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveChildDirectiveDecision(
                Engineer,
                "Fix the regression.",
                "Reproduction steps attached."),
            evaluationEnvelopeJson: CanonicalEnvelopeJson);

        var directive = Assert.IsType<OrgDirective>(result.Message);
        Assert.DoesNotContain(
            EvaluationInstruction.EnvelopeMarker,
            directive.Context,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_output_with_envelope_is_scoreable_by_the_projection_parser()
    {
        var rubric = EvaluationRubricContract.Load(BugTriageRubricFile, 1);
        var context = AiDirectiveExecutionContext.From(Request());

        var result = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveReportDecision(ReportKind.Done, "Bug triage is complete."),
            evaluationEnvelopeJson:
                "{\"dimensions\":{\"severity\":[\"medium\"],\"missing-information\":[\"run-log\"]}}");

        var report = Assert.IsType<Report>(result.Message);
        var byId = EvaluationProjectionParser.Parse(report.Body, "report", rubric)
            .Dimensions
            .ToDictionary(dimension => dimension.DimensionId, StringComparer.Ordinal);

        Assert.Equal(EvaluationDimensionStatus.Valid, byId["severity"].Status);
        Assert.Equal(["medium"], byId["severity"].Labels);
        Assert.Equal(EvaluationDimensionStatus.Valid, byId["missing-information"].Status);
        Assert.Equal(["run-log"], byId["missing-information"].Labels);
    }

    // --- Prompt/output constraint wiring -------------------------------------------------

    [Fact]
    public void Prompt_uses_the_composed_constraint_only_inside_evaluation_scopes()
    {
        var rubric = EvaluationRubricContract.Load(BugTriageRubricFile, 1);
        var evaluationContext = AiDirectiveExecutionContext.From(
            Request(),
            rubric.BuildInstruction());
        var normalContext = AiDirectiveExecutionContext.From(Request());

        var evaluationRequest = AiDirectivePrompt.CreateInitialRequest(evaluationContext);
        var normalRequest = AiDirectivePrompt.CreateInitialRequest(normalContext);

        Assert.Equal(
            AiDirectiveEvaluationEnvelope.ComposedSchemaName,
            evaluationRequest.OutputConstraint!.SchemaName);
        Assert.True(evaluationRequest.OutputConstraint.JsonSchema
            .GetProperty("properties")
            .TryGetProperty(AiDirectiveEvaluationEnvelope.PropertyName, out _));
        Assert.Same(
            AiDirectiveDecisionSchema.OutputConstraint,
            normalRequest.OutputConstraint);
    }

    // --- Helpers --------------------------------------------------------------------------

    private static int CountMarkers(string text)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(
            EvaluationInstruction.EnvelopeMarker,
            index,
            StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += EvaluationInstruction.EnvelopeMarker.Length;
        }

        return count;
    }

    private static AiDirectiveProcessingRequest Request(
        IEnumerable<PositionId>? directSubordinates = null)
    {
        var entity = PositionEntityId.From(Organization, Position);
        var occupant = OccupantId.From("agent-12");
        var directive = new OrgDirective(
            IncomingMessage,
            Organization,
            new PositionEndpointRef(Superior),
            new PositionEndpointRef(Position),
            Thread,
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: At.AddHours(2),
            IncomingDirective,
            parentDirectiveId: null,
            objective: "Triage checkout regression",
            context: "Customer reports checkout failures.");
        var configuration = new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(21, "sha256:t12a"),
            Organization,
            Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: Superior,
                name: "Bug triage",
                timezone: "Europe/Lisbon",
                directSubordinates: directSubordinates),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "triage-v1",
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "triage"),
                    new AiModelParameters(maxOutputTokens: 256),
                    timeout: TimeSpan.FromSeconds(15)),
                identityPrompt: new IdentityPromptRuntimeConfiguration(
                    "triage-v1",
                    "prompts/triage-v1.md",
                    "You are responsible for triaging incoming bugs.")),
            new PositionAuthorityRuntimeConfiguration());

        return AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            PositionState.Restore(new PositionSnapshot(At)),
            occupant,
            directive);
    }

    private static string BugTriageRubricFile => Path.Combine(
        RepositoryRoot,
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "evaluation",
        "bug-triage-rubric.v1.json");

    private static string FollowUpRubricFile => Path.Combine(
        RepositoryRoot,
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "evaluation",
        "follow-up-coordination-rubric.v1.json");

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate the Hive repository root.");
        }
    }
}
