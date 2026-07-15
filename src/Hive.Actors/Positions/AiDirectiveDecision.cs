using System.Collections.Immutable;
using System.Text.Json;
using Hive.Domain.Ai;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Actors.Positions;

internal enum AiDirectiveDecisionIntent
{
    Report = 1,
    Escalation = 2,
    Directive = 3,
}

internal static class AiDirectiveDecisionIntentContract
{
    public static AiDirectiveDecisionIntent RequireDefined(
        AiDirectiveDecisionIntent value,
        string parameterName)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "AI directive decision intent must be Report, Escalation, or Directive.");
        }

        return value;
    }

    public static string ToWireValue(AiDirectiveDecisionIntent value) =>
        RequireDefined(value, nameof(value)) switch
        {
            AiDirectiveDecisionIntent.Report => "Report",
            AiDirectiveDecisionIntent.Escalation => "Escalation",
            AiDirectiveDecisionIntent.Directive => "Directive",
            _ => throw new InvalidOperationException("Validated decision intent is not mapped."),
        };

    public static AiDirectiveDecisionIntent ParseWireValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            "Report" => AiDirectiveDecisionIntent.Report,
            "Escalation" => AiDirectiveDecisionIntent.Escalation,
            "Directive" => AiDirectiveDecisionIntent.Directive,
            _ => throw new ArgumentException(
                "AI directive decision intent must be Report, Escalation, or Directive.",
                nameof(value)),
        };
    }

    public static bool TryParseWireValue(string? value, out AiDirectiveDecisionIntent intent)
    {
        switch (value)
        {
            case "Report":
                intent = AiDirectiveDecisionIntent.Report;
                return true;
            case "Escalation":
                intent = AiDirectiveDecisionIntent.Escalation;
                return true;
            case "Directive":
                intent = AiDirectiveDecisionIntent.Directive;
                return true;
            default:
                intent = default;
                return false;
        }
    }
}

internal static class AiDirectiveDecisionSchema
{
    public const int SchemaVersion = 1;
    public const string SchemaName = "hive_ai_directive_decision_v1";
    public const string SchemaVersionProperty = "schema_version";
    public const string DecisionProperty = "decision";
    public const string IntentProperty = "intent";
    public const string ActingUnderProperty = "acting_under";
    public const string ReportPayloadProperty = "report";
    public const string ReportKindField = "kind";
    public const string ReportBodyField = "body";
    public const string EscalationPayloadProperty = "escalation";
    public const string EscalationIssueField = "issue";
    public const string EscalationContextField = "context";
    public const string EscalationOptionsConsideredField = "options_considered";
    public const string DirectivePayloadProperty = "directive";
    public const string DirectiveTargetPositionIdField = "target_position_id";
    public const string DirectiveObjectiveField = "objective";
    public const string DirectiveContextField = "context";

    private static readonly JsonElement CanonicalJsonSchema = CreateJsonSchema();

    public static AiOutputConstraint OutputConstraint { get; } = new(
        SchemaName,
        SchemaVersion,
        CanonicalJsonSchema,
        [AiOutputConstraintMode.JsonObject, AiOutputConstraintMode.Text]);

    public static ImmutableArray<string> AllowedIntents { get; } =
    [
        AiDirectiveDecisionIntentContract.ToWireValue(AiDirectiveDecisionIntent.Report),
        AiDirectiveDecisionIntentContract.ToWireValue(AiDirectiveDecisionIntent.Escalation),
        AiDirectiveDecisionIntentContract.ToWireValue(AiDirectiveDecisionIntent.Directive),
    ];

    public static ImmutableArray<string> ReportRequiredFields { get; } =
    [
        ReportKindField,
        ReportBodyField,
    ];

    public static ImmutableArray<string> EscalationRequiredFields { get; } =
    [
        EscalationIssueField,
        EscalationContextField,
        EscalationOptionsConsideredField,
    ];

    public static ImmutableArray<string> DirectiveRequiredFields { get; } =
    [
        DirectiveTargetPositionIdField,
        DirectiveObjectiveField,
        DirectiveContextField,
    ];

    private static JsonElement CreateJsonSchema()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "schema_version": { "type": "integer", "const": 1 },
                "acting_under": { "type": "string" },
                "decision": {
                  "anyOf": [
                    {
                      "type": "object",
                      "properties": {
                        "intent": { "type": "string", "const": "Report" },
                        "report": {
                          "type": "object",
                          "properties": {
                            "kind": { "type": "string", "enum": ["Progress", "Done"] },
                            "body": {
                              "type": "string",
                              "description": "Business content; include any mandatory runtime appendix assigned to this field exactly as instructed.",
                              "pattern": "[^\\u0009-\\u000D\\u0020\\u0085\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000]"
                            }
                          },
                          "required": ["kind", "body"],
                          "additionalProperties": false
                        }
                      },
                      "required": ["intent", "report"],
                      "additionalProperties": false
                    },
                    {
                      "type": "object",
                      "properties": {
                        "intent": { "type": "string", "const": "Escalation" },
                        "escalation": {
                          "type": "object",
                          "properties": {
                            "issue": {
                              "type": "string",
                              "pattern": "[^\\u0009-\\u000D\\u0020\\u0085\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000]"
                            },
                            "context": {
                              "type": "string",
                              "description": "Business context; include any mandatory runtime appendix assigned to this field exactly as instructed.",
                              "pattern": "[^\\u0009-\\u000D\\u0020\\u0085\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000]"
                            },
                            "options_considered": {
                              "type": "array",
                              "items": {
                                "type": "string",
                                "pattern": "[^\\u0009-\\u000D\\u0020\\u0085\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000]"
                              }
                            }
                          },
                          "required": ["issue", "context", "options_considered"],
                          "additionalProperties": false
                        }
                      },
                      "required": ["intent", "escalation"],
                      "additionalProperties": false
                    },
                    {
                      "type": "object",
                      "properties": {
                        "intent": { "type": "string", "const": "Directive" },
                        "directive": {
                          "type": "object",
                          "properties": {
                            "target_position_id": {
                              "type": "string",
                              "pattern": "[^\\u0009-\\u000D\\u0020\\u0085\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000]"
                            },
                            "objective": {
                              "type": "string",
                              "pattern": "[^\\u0009-\\u000D\\u0020\\u0085\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000]"
                            },
                            "context": {
                              "type": "string",
                              "pattern": "[^\\u0009-\\u000D\\u0020\\u0085\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000]"
                            }
                          },
                          "required": ["target_position_id", "objective", "context"],
                          "additionalProperties": false
                        }
                      },
                      "required": ["intent", "directive"],
                      "additionalProperties": false
                    }
                  ]
                }
              },
              "required": [
                "schema_version",
                "acting_under",
                "decision"
              ],
              "additionalProperties": false
            }
            """);

        return document.RootElement.Clone();
    }
}

internal abstract record AiDirectiveDecision
{
    protected AiDirectiveDecision(
        AiDirectiveDecisionIntent intent,
        ActingUnderDeclaration? actingUnder = null)
    {
        Intent = AiDirectiveDecisionIntentContract.RequireDefined(intent, nameof(intent));
        ActingUnder = actingUnder ?? ActingUnderDeclaration.Missing();
    }

    public AiDirectiveDecisionIntent Intent { get; }

    public ActingUnderDeclaration ActingUnder { get; }
}

internal sealed record AiDirectiveReportDecision : AiDirectiveDecision
{
    public AiDirectiveReportDecision(
        ReportKind kind,
        string body,
        ActingUnderDeclaration? actingUnder = null)
        : base(AiDirectiveDecisionIntent.Report, actingUnder)
    {
        Kind = ReportKindContract.RequireDefined(kind, nameof(kind));
        Body = AiAgentGatewayText.Require(body, nameof(body));
    }

    public ReportKind Kind { get; }

    public string Body { get; }
}

internal sealed record AiDirectiveEscalationDecision : AiDirectiveDecision
{
    public AiDirectiveEscalationDecision(
        string issue,
        string context,
        IEnumerable<string> optionsConsidered,
        ActingUnderDeclaration? actingUnder = null)
        : base(AiDirectiveDecisionIntent.Escalation, actingUnder)
    {
        Issue = AiAgentGatewayText.Require(issue, nameof(issue));
        Context = AiAgentGatewayText.Require(context, nameof(context));
        OptionsConsidered = SnapshotOptions(optionsConsidered);
    }

    public string Issue { get; }

    public string Context { get; }

    public ImmutableArray<string> OptionsConsidered { get; }

    private static ImmutableArray<string> SnapshotOptions(IEnumerable<string> optionsConsidered)
    {
        ArgumentNullException.ThrowIfNull(optionsConsidered);

        return optionsConsidered
            .Select(option => AiAgentGatewayText.Require(option, nameof(optionsConsidered)))
            .ToImmutableArray();
    }
}

internal sealed record AiDirectiveChildDirectiveDecision : AiDirectiveDecision
{
    public AiDirectiveChildDirectiveDecision(
        PositionId targetPositionId,
        string objective,
        string context,
        ActingUnderDeclaration? actingUnder = null)
        : base(AiDirectiveDecisionIntent.Directive, actingUnder)
    {
        TargetPositionId = targetPositionId
            ?? throw new ArgumentNullException(nameof(targetPositionId));
        Objective = AiAgentGatewayText.Require(objective, nameof(objective));
        Context = AiAgentGatewayText.Require(context, nameof(context));
    }

    public PositionId TargetPositionId { get; }

    public string Objective { get; }

    public string Context { get; }
}
