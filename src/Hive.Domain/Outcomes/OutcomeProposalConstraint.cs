using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Domain.Ai;

namespace Hive.Domain.Outcomes;

public static class OutcomeProposalConstraint
{
    public const int MaximumInformationGaps = 32;
    public const int SchemaVersion = OrganizationalOutcomeContractVersions.OutcomeProposal;
    public const string SchemaName = "hive_outcome_proposal_v3";
    public const string SchemaVersionProperty = "schema_version";
    public const string ProposalProperty = "proposal";
    public const string ProposedIntentProperty = "proposed_intent";
    public const string WorkStateProperty = "work_state";
    public const string RequiredInterventionProperty = "required_intervention";
    public const string BlockersProperty = "blockers";
    public const string NextActionProperty = "next_action";
    public const string EvidenceReferencesProperty = "evidence_references";
    public const string EvidenceSourceProperty = "source";
    public const string EvidenceReferenceProperty = "reference";
    public const string InformationGapsProperty = "information_gaps";
    public const string MissingEvidenceReferenceProperty = "missing_evidence_reference";
    public const string MaterialityProperty = "materiality";
    public const string MaterialityReasonProperty = "materiality_reason";
    public const string AuthorityRequestProperty = "authority_request";
    public const string AuthorityDecisionProperty = "decision";
    public const string AuthorityKindProperty = "authority_kind";
    public const string AuthorityReferenceProperty = "authority_reference";
    public const string PositionLimitReasonProperty = "position_limit_reason";

    public static ImmutableArray<string> ProposalRequiredFields { get; } =
    [
        ProposedIntentProperty,
        WorkStateProperty,
        RequiredInterventionProperty,
        BlockersProperty,
        NextActionProperty,
        EvidenceReferencesProperty,
        InformationGapsProperty,
        AuthorityRequestProperty,
    ];

    public static ImmutableArray<string> EvidenceRequiredFields { get; } =
    [
        EvidenceSourceProperty,
        EvidenceReferenceProperty,
    ];

    public static ImmutableArray<string> InformationGapRequiredFields { get; } =
    [
        MissingEvidenceReferenceProperty,
        MaterialityProperty,
        MaterialityReasonProperty,
    ];

    public static ImmutableArray<string> AuthorityRequestRequiredFields { get; } =
    [
        AuthorityDecisionProperty,
        AuthorityKindProperty,
        AuthorityReferenceProperty,
        PositionLimitReasonProperty,
    ];

    private static readonly JsonElement CanonicalJsonSchema = CreateJsonSchema(
        evidenceContext: null,
        allowProgressReports: false);
    private static readonly JsonElement CheckpointableJsonSchema = CreateJsonSchema(
        evidenceContext: null,
        allowProgressReports: true);

    public static AiOutputConstraint OutputConstraint { get; } = new(
        SchemaName,
        SchemaVersion,
        CanonicalJsonSchema,
        [AiOutputConstraintMode.JsonObject, AiOutputConstraintMode.Text]);

    private static AiOutputConstraint CheckpointableOutputConstraint { get; } = new(
        SchemaName,
        SchemaVersion,
        CheckpointableJsonSchema,
        [AiOutputConstraintMode.JsonObject, AiOutputConstraintMode.Text]);

    public static AiOutputConstraint CreateOutputConstraint(bool allowProgressReports) =>
        allowProgressReports ? CheckpointableOutputConstraint : OutputConstraint;

    public static AiOutputConstraint CreateOutputConstraint(
        OutcomeProposalEvidenceContext evidenceContext,
        bool allowProgressReports = false)
    {
        ArgumentNullException.ThrowIfNull(evidenceContext);

        return new AiOutputConstraint(
            SchemaName,
            SchemaVersion,
            CreateJsonSchema(evidenceContext, allowProgressReports),
            [AiOutputConstraintMode.JsonObject, AiOutputConstraintMode.Text]);
    }

    private static JsonElement CreateJsonSchema(
        OutcomeProposalEvidenceContext? evidenceContext,
        bool allowProgressReports)
    {
        var proposalBranches = new JsonArray
        {
            CreateProposalBranch(
                OutcomeProposedIntent.ContinueWork,
                [OutcomeWorkState.NotStarted, OutcomeWorkState.InProgress],
                [OutcomeRequiredIntervention.None],
                blockersAllowed: [],
                nextActionMode: NextActionMode.Required,
                minimumEvidenceReferences: 0,
                evidenceContext: evidenceContext),
        };
        if (allowProgressReports)
        {
            proposalBranches.Add(CreateProposalBranch(
                OutcomeProposedIntent.ReportProgress,
                [OutcomeWorkState.InProgress],
                [OutcomeRequiredIntervention.None],
                blockersAllowed: [],
                nextActionMode: NextActionMode.Required,
                minimumEvidenceReferences: 1,
                evidenceContext: evidenceContext));
        }

        proposalBranches.Add(CreateProposalBranch(
                OutcomeProposedIntent.ReportDone,
                [OutcomeWorkState.Completed],
                [OutcomeRequiredIntervention.None],
                blockersAllowed: [],
                nextActionMode: NextActionMode.Forbidden,
                minimumEvidenceReferences: 1,
                evidenceContext: evidenceContext));
        proposalBranches.Add(CreateProposalBranch(
                OutcomeProposedIntent.Escalation,
                [OutcomeWorkState.Blocked, OutcomeWorkState.Failed],
                [
                    OutcomeRequiredIntervention.SuperiorDecision,
                    OutcomeRequiredIntervention.ExternalAction,
                ],
                blockersAllowed: Enum.GetValues<OutcomeBlocker>()
                    .Where(blocker => blocker is not OutcomeBlocker.HumanApproval),
                nextActionMode: NextActionMode.Optional,
                minimumEvidenceReferences: 0,
                evidenceContext: evidenceContext,
                minimumBlockers: 1));
        proposalBranches.Add(CreateProposalBranch(
                OutcomeProposedIntent.Directive,
                [OutcomeWorkState.NotStarted, OutcomeWorkState.InProgress],
                [OutcomeRequiredIntervention.Delegation],
                blockersAllowed: [],
                nextActionMode: NextActionMode.Required,
                minimumEvidenceReferences: 0,
                evidenceContext: evidenceContext));
        proposalBranches.Add(CreateProposalBranch(
                OutcomeProposedIntent.ApprovalRequired,
                [OutcomeWorkState.Blocked],
                [OutcomeRequiredIntervention.HumanApproval],
                blockersAllowed: [OutcomeBlocker.HumanApproval],
                nextActionMode: NextActionMode.Optional,
                minimumEvidenceReferences: 0,
                evidenceContext: evidenceContext,
                minimumBlockers: 1));

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
                [ProposalProperty] = new JsonObject
                {
                    ["anyOf"] = proposalBranches,
                },
            },
            ["required"] = JsonArray(SchemaVersionProperty, ProposalProperty),
            ["additionalProperties"] = false,
        };

        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonObject CreateProposalBranch(
        OutcomeProposedIntent proposedIntent,
        IEnumerable<OutcomeWorkState> workStates,
        IEnumerable<OutcomeRequiredIntervention> interventions,
        IEnumerable<OutcomeBlocker> blockersAllowed,
        NextActionMode nextActionMode,
        int minimumEvidenceReferences,
        OutcomeProposalEvidenceContext? evidenceContext,
        int minimumBlockers = 0)
    {
        var blockerWireValues = blockersAllowed
            .Select(OutcomeBlockerContract.ToWireValue)
            .ToArray();
        var blockers = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = JsonArray(
                    blockerWireValues.Length == 0
                        ? OutcomeBlockerContract.WireValues
                        : blockerWireValues),
            },
            ["uniqueItems"] = true,
        };
        if (minimumBlockers > 0)
        {
            blockers["minItems"] = minimumBlockers;
        }
        else
        {
            blockers["maxItems"] = 0;
        }

        if (proposedIntent is OutcomeProposedIntent.ApprovalRequired)
        {
            blockers["maxItems"] = 1;
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [ProposedIntentProperty] = new JsonObject
                {
                    ["type"] = "string",
                    ["const"] = OutcomeProposedIntentContract.ToWireValue(proposedIntent),
                },
                [WorkStateProperty] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = JsonArray(workStates.Select(OutcomeWorkStateContract.ToWireValue)),
                },
                [RequiredInterventionProperty] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = JsonArray(
                        interventions.Select(OutcomeRequiredInterventionContract.ToWireValue)),
                },
                [BlockersProperty] = blockers,
                [NextActionProperty] = CreateNextActionSchema(nextActionMode),
                [EvidenceReferencesProperty] = CreateEvidenceReferencesSchema(
                    minimumEvidenceReferences,
                    evidenceContext),
                [InformationGapsProperty] = CreateInformationGapsSchema(),
                [AuthorityRequestProperty] = CreateAuthorityRequestSchema(
                    interventions.Any(IsExternalIntervention)),
            },
            ["required"] = JsonArray(ProposalRequiredFields),
            ["additionalProperties"] = false,
        };
    }

    private static JsonNode CreateNextActionSchema(NextActionMode mode) =>
        mode switch
        {
            NextActionMode.Required => CreateNonBlankStringSchema(),
            NextActionMode.Forbidden => new JsonObject { ["type"] = "null" },
            NextActionMode.Optional => new JsonObject
            {
                ["anyOf"] = new JsonArray
                {
                    CreateNonBlankStringSchema(),
                    new JsonObject { ["type"] = "null" },
                },
            },
            _ => throw new InvalidOperationException("Unknown next-action schema mode."),
        };

    private static JsonObject CreateEvidenceReferencesSchema(
        int minimumEvidenceReferences,
        OutcomeProposalEvidenceContext? evidenceContext) =>
        new()
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    [EvidenceSourceProperty] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = JsonArray(evidenceContext is null
                            ? OutcomeEvidenceSourceContract.WireValues
                            :
                            [
                                OutcomeEvidenceSourceContract.ToWireValue(
                                    OutcomeEvidenceSource.DirectiveInput),
                            ]),
                    },
                    [EvidenceReferenceProperty] =
                        CreateEvidenceReferenceSchema(evidenceContext),
                },
                ["required"] = JsonArray(EvidenceRequiredFields),
                ["additionalProperties"] = false,
            },
            ["minItems"] = minimumEvidenceReferences,
            ["uniqueItems"] = true,
        };

    private static JsonObject CreateEvidenceReferenceSchema(
        OutcomeProposalEvidenceContext? evidenceContext)
    {
        var schema = new JsonObject
        {
            ["type"] = "string",
            ["minLength"] = 1,
            ["maxLength"] = 128,
            ["pattern"] = "^[A-Za-z0-9._:/-]+$",
        };
        if (evidenceContext is not null)
        {
            schema["enum"] = JsonArray(evidenceContext.DirectiveInputReferences);
        }

        return schema;
    }

    private static JsonObject CreateInformationGapsSchema() =>
        new()
        {
            ["type"] = "array",
            ["maxItems"] = MaximumInformationGaps,
            ["items"] = new JsonObject
            {
                ["anyOf"] = new JsonArray
                {
                    CreateInformationGapBranch(
                        OutcomeInformationGapMateriality.Material,
                        new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = JsonArray(
                                OutcomeInformationGapMaterialityReasonContract.WireValues),
                        }),
                    CreateInformationGapBranch(
                        OutcomeInformationGapMateriality.NonMaterial,
                        new JsonObject { ["type"] = "null" }),
                },
            },
            ["uniqueItems"] = true,
        };

    private static JsonObject CreateInformationGapBranch(
        OutcomeInformationGapMateriality materiality,
        JsonNode materialityReasonSchema) =>
        new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [MissingEvidenceReferenceProperty] = CreateEvidenceReferenceSchema(
                    evidenceContext: null),
                [MaterialityProperty] = new JsonObject
                {
                    ["type"] = "string",
                    ["const"] = OutcomeInformationGapMaterialityContract.ToWireValue(
                        materiality),
                },
                [MaterialityReasonProperty] = materialityReasonSchema,
            },
            ["required"] = JsonArray(InformationGapRequiredFields),
            ["additionalProperties"] = false,
        };

    private static JsonNode CreateAuthorityRequestSchema(bool required) =>
        required
            ? new JsonObject
            {
                ["anyOf"] = new JsonArray
                {
                    CreateAuthorityRequestBranch(OutcomeAuthorityKind.ActionDomain),
                    CreateAuthorityRequestBranch(OutcomeAuthorityKind.ApprovalPolicy),
                },
            }
            : new JsonObject { ["type"] = "null" };

    private static JsonObject CreateAuthorityRequestBranch(OutcomeAuthorityKind authorityKind) =>
        new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [AuthorityDecisionProperty] = CreateNonBlankStringSchema(),
                [AuthorityKindProperty] = new JsonObject
                {
                    ["type"] = "string",
                    ["const"] = OutcomeAuthorityKindContract.ToWireValue(authorityKind),
                },
                [AuthorityReferenceProperty] = CreateEvidenceReferenceSchema(
                    evidenceContext: null),
                [PositionLimitReasonProperty] = CreateNonBlankStringSchema(),
            },
            ["required"] = JsonArray(AuthorityRequestRequiredFields),
            ["additionalProperties"] = false,
        };

    private static bool IsExternalIntervention(OutcomeRequiredIntervention intervention) =>
        intervention is OutcomeRequiredIntervention.HumanApproval or
            OutcomeRequiredIntervention.SuperiorDecision or
            OutcomeRequiredIntervention.ExternalAction;

    private static JsonObject CreateNonBlankStringSchema() =>
        new()
        {
            ["type"] = "string",
            ["pattern"] = "[^\\u0009-\\u000D\\u0020\\u0085\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000]",
        };

    private static JsonArray JsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonArray JsonArray(params string[] values) => JsonArray((IEnumerable<string>)values);

    private enum NextActionMode
    {
        Required = 1,
        Optional = 2,
        Forbidden = 3,
    }
}
