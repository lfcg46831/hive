using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Domain.Ai;
using Hive.Infrastructure.Evaluation;

namespace Hive.Actors.Positions;

/// <summary>
/// Contractual transport for the evaluation envelope (US-F0-13-T12a).
/// When an evaluation profile is active for the request, the negotiated structured output
/// constraint is composed with a required top-level "evaluation" section generated exclusively
/// from the rubric-declared envelope dimensions (opaque ids, cardinality, label enums), and an
/// accepted structured section is canonicalized into exactly one textual envelope line inside
/// the selected result payload so every downstream consumer (projector, parser, datasets)
/// keeps its existing contract. No organizational function semantics are compiled here.
/// </summary>
internal static class AiDirectiveEvaluationEnvelope
{
    public const string PropertyName = "evaluation";
    public const string DimensionsPropertyName = "dimensions";
    public const string ComposedSchemaName = "hive_ai_directive_decision_v1_evaluation";

    /// <summary>
    /// Composes the canonical decision constraint with a required top-level evaluation
    /// section derived from the rubric-declared envelope dimensions. Dimensions and labels
    /// are emitted as opaque enum tokens in lexical id order; single-label dimensions carry
    /// their cardinality in the schema, and the local parser remains the authority in every
    /// negotiated mode. Fallback modes are preserved unchanged.
    /// </summary>
    public static AiOutputConstraint ComposeOutputConstraint(EvaluationInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);

        if (instruction.EnvelopeDimensions.IsDefaultOrEmpty)
        {
            return AiDirectiveDecisionSchema.OutputConstraint;
        }

        var baseConstraint = AiDirectiveDecisionSchema.OutputConstraint;
        var root = JsonNode.Parse(baseConstraint.JsonSchema.GetRawText())!.AsObject();
        var properties = root["properties"]!.AsObject();
        properties[PropertyName] = BuildEvaluationSchema(instruction);
        root["required"]!.AsArray().Add(PropertyName);

        using var document = JsonDocument.Parse(root.ToJsonString());
        return new AiOutputConstraint(
            ComposedSchemaName,
            baseConstraint.SchemaVersion,
            document.RootElement,
            baseConstraint.AllowedFallbackModes);
    }

    /// <summary>
    /// Serializes an accepted structured evaluation property into its compact canonical JSON.
    /// Shape and label validity remain the projection parser's responsibility downstream.
    /// </summary>
    public static string Canonicalize(JsonElement evaluationProperty) =>
        JsonSerializer.Serialize(evaluationProperty);

    /// <summary>
    /// Rewrites the selected free-text payload so it carries exactly one envelope line:
    /// any model-emitted marker lines are removed and the canonical envelope derived from the
    /// structured section is appended as the final line, matching the textual convention the
    /// evaluation projector already parses.
    /// </summary>
    public static string ComposePayloadText(string payloadText, string envelopeJson)
    {
        ArgumentNullException.ThrowIfNull(payloadText);
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeJson);

        var kept = payloadText
            .Split('\n')
            .Where(line => !line.Contains(
                EvaluationInstruction.EnvelopeMarker,
                StringComparison.Ordinal));
        var body = string.Join("\n", kept).TrimEnd();
        var envelopeLine = EvaluationInstruction.EnvelopeMarker + envelopeJson;

        return string.IsNullOrWhiteSpace(body)
            ? envelopeLine
            : body + "\n" + envelopeLine;
    }

    private static JsonObject BuildEvaluationSchema(EvaluationInstruction instruction)
    {
        var dimensionProperties = new JsonObject();
        var requiredDimensions = new JsonArray();
        foreach (var dimension in instruction.EnvelopeDimensions
            .OrderBy(dimension => dimension.Id, StringComparer.Ordinal))
        {
            dimensionProperties[dimension.Id] = BuildDimensionSchema(dimension);
            requiredDimensions.Add(JsonValue.Create(dimension.Id));
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [DimensionsPropertyName] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = dimensionProperties,
                    ["required"] = requiredDimensions,
                    ["additionalProperties"] = false,
                },
            },
            ["required"] = new JsonArray(JsonValue.Create(DimensionsPropertyName)),
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject BuildDimensionSchema(EvaluationEnvelopeDimension dimension)
    {
        var labelEnum = new JsonArray();
        foreach (var label in dimension.Labels)
        {
            labelEnum.Add(JsonValue.Create(label));
        }

        var schema = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = labelEnum,
            },
        };
        if (dimension.SingleLabel)
        {
            schema["minItems"] = 1;
            schema["maxItems"] = 1;
        }

        return schema;
    }
}
