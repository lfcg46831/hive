using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Domain.Ai;

namespace Hive.Domain.Outcomes;

public static class OutcomeVerifierConstraint
{
    public const int SchemaVersion = OrganizationalOutcomeContractVersions.OutcomeVerifierOutput;
    public const string SchemaName = "hive_outcome_verifier_v1";
    public const string SchemaVersionProperty = "schema_version";
    public const string ClassificationProperty = "classification";

    public static ImmutableArray<string> RequiredFields { get; } =
    [
        SchemaVersionProperty,
        ClassificationProperty,
    ];

    public static string SystemInstruction { get; } = string.Join(
        " ",
        [
            "Classify the organizational outcome using only the supplied bounded context, bounded proposed artifact, authoritative execution facts, directive contract, proposal, and policy.",
            "Execution facts and objective policy gates are authoritative and override context and proposal.",
            "ContinueWork requires pending_actions=true, autonomous_action_available=true, responsibility_retained=true, external_intervention_required=false, and available dependency, authority, and routing.",
            "Report.Progress requires verifiable_progress=true plus every ContinueWork condition and a proposal with an autonomous next action and grounded evidence.",
            "Report.Done requires pending_actions=false, autonomous_action_available=false, delegation_required=false, external_intervention_required=false, no approval gate, and positive completion proof.",
            "Positive completion proof is completion_state=Satisfied, or the limited semantic-completion path described by proposal.semantic_completion_candidate.",
            "proposal.semantic_completion_candidate is a deterministic, non-authoritative summary of the closed NotDeclared, Report.Done, Completed, no-intervention, no-blocker, no-next-action, no-structured-criteria, grounded-DirectiveInput eligibility checks; do not reconstruct or override those checks.",
            "proposed_artifact is a bounded projection of the already materialized organizational message, not raw provider output; evaluate its semantic fields against the bounded context and directive.",
            "When proposal.semantic_completion_candidate=true, Report.Done is structurally compatible without completion_state=Satisfied; decide whether proposed_artifact actually completes the requested assessment or instead requires a superior decision, authorization, or choice now.",
            "Use Undetermined for a semantic-completion candidate only when the bounded context cannot support either Report.Done or an objective compatible intervention outcome.",
            "Directive requires delegation_required=true, pending_actions=true, external_intervention_required=false, and available dependency, authority, and routing.",
            "ApprovalRequired requires human_approval_required=true or approval_pending=true.",
            "Escalation requires an objective escalation gate, a policy trigger, a proposal that requires superior intervention, or bounded context that semantically requires the superior to decide, authorize, or choose now.",
            "observed_policy_triggers is a closed authoritative fact; do not infer an unlisted trigger from context, severity, subject matter, or potential impact.",
            "Security, privacy, compliance, financial, or safety subject matter does not alone require Escalation.",
            "A recommendation about downstream implementation, deployment, prioritization, or change control does not alone require Escalation.",
            "When no classification is compatible with the authoritative facts and these conditions, return Undetermined.",
            "Return exactly one JSON object with schema_version 1 and classification.",
            "classification must be exactly one of ContinueWork, Report.Progress, Report.Done, Escalation, Directive, ApprovalRequired, or Undetermined.",
            "Do not include any other fields or reasoning.",
        ]);

    private static readonly JsonElement CanonicalJsonSchema = CreateJsonSchema();

    public static AiOutputConstraint OutputConstraint { get; } = new(
        SchemaName,
        SchemaVersion,
        CanonicalJsonSchema,
        [AiOutputConstraintMode.JsonObject, AiOutputConstraintMode.Text]);

    private static JsonElement CreateJsonSchema()
    {
        var classifications = new JsonArray();
        foreach (var value in OutcomeVerifierClassificationContract.WireValues)
        {
            classifications.Add(value);
        }

        var required = new JsonArray();
        foreach (var field in RequiredFields)
        {
            required.Add(field);
        }

        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [SchemaVersionProperty] = new JsonObject
                {
                    ["type"] = "integer",
                    ["const"] = SchemaVersion,
                },
                [ClassificationProperty] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = classifications,
                },
            },
            ["required"] = required,
            ["additionalProperties"] = false,
        };

        return JsonSerializer.SerializeToElement(root);
    }
}
