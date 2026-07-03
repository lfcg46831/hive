using System.Collections.Immutable;
using System.Text.Json;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Actors.Positions;

internal sealed record AiDirectiveDecisionParseError
{
    public AiDirectiveDecisionParseError(string code, string path)
    {
        Code = AiAgentGatewayText.Require(code, nameof(code));
        Path = AiAgentGatewayText.Require(path, nameof(path));
    }

    public string Code { get; }

    public string Path { get; }
}

internal sealed record AiDirectiveDecisionParseResult
{
    private AiDirectiveDecisionParseResult(
        AiDirectiveDecision? decision,
        ImmutableArray<AiDirectiveDecisionParseError> errors)
    {
        Decision = decision;
        Errors = errors;
    }

    public bool IsSuccess => Decision is not null;

    public bool IsFailure => !IsSuccess;

    public AiDirectiveDecision? Decision { get; }

    public IReadOnlyList<AiDirectiveDecisionParseError> Errors { get; }

    public static AiDirectiveDecisionParseResult Success(AiDirectiveDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return new AiDirectiveDecisionParseResult(
            decision,
            ImmutableArray<AiDirectiveDecisionParseError>.Empty);
    }

    public static AiDirectiveDecisionParseResult Failure(
        IEnumerable<AiDirectiveDecisionParseError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var snapshot = errors.ToImmutableArray();
        if (snapshot.Any(error => error is null))
        {
            throw new ArgumentException(
                "AI directive parse errors cannot contain null entries.",
                nameof(errors));
        }

        if (snapshot.IsEmpty)
        {
            throw new ArgumentException(
                "A failed AI directive parse result must carry at least one error.",
                nameof(errors));
        }

        var ordered = snapshot
            .OrderBy(error => error.Path, StringComparer.Ordinal)
            .ThenBy(error => error.Code, StringComparer.Ordinal)
            .ToImmutableArray();

        return new AiDirectiveDecisionParseResult(null, ordered);
    }
}

internal static class AiDirectiveDecisionParser
{
    private const string EmptyResponseCode = "empty-response";
    private const string InvalidJsonCode = "invalid-json";
    private const string TopLevelObjectRequiredCode = "top-level-object-required";
    private const string RequiredFieldCode = "required-field";
    private const string InvalidSchemaVersionCode = "invalid-schema-version";
    private const string InvalidIntentCode = "invalid-intent";
    private const string PayloadRequiredCode = "payload-required";
    private const string PayloadAmbiguousCode = "payload-ambiguous";
    private const string PayloadIntentMismatchCode = "payload-intent-mismatch";
    private const string UnknownFieldCode = "unknown-field";
    private const string InvalidFieldCode = "invalid-field";

    private static readonly string[] TopLevelFields =
    [
        AiDirectiveDecisionSchema.SchemaVersionProperty,
        AiDirectiveDecisionSchema.IntentProperty,
        AiDirectiveDecisionSchema.ReportPayloadProperty,
        AiDirectiveDecisionSchema.EscalationPayloadProperty,
        AiDirectiveDecisionSchema.DirectivePayloadProperty,
    ];

    private static readonly string[] ReportFields =
    [
        AiDirectiveDecisionSchema.ReportKindField,
        AiDirectiveDecisionSchema.ReportBodyField,
    ];

    private static readonly string[] EscalationFields =
    [
        AiDirectiveDecisionSchema.EscalationIssueField,
        AiDirectiveDecisionSchema.EscalationContextField,
        AiDirectiveDecisionSchema.EscalationOptionsConsideredField,
    ];

    private static readonly string[] DirectiveFields =
    [
        AiDirectiveDecisionSchema.DirectiveTargetPositionIdField,
        AiDirectiveDecisionSchema.DirectiveObjectiveField,
        AiDirectiveDecisionSchema.DirectiveContextField,
    ];

    public static AiDirectiveDecisionParseResult Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return Failure(Error(EmptyResponseCode, "$"));
        }

        using var document = ParseJson(output, out var invalidJson);
        if (invalidJson is not null)
        {
            return Failure(invalidJson);
        }

        var root = document!.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            return Failure(Error(TopLevelObjectRequiredCode, "$"));
        }

        var errors = new List<AiDirectiveDecisionParseError>();
        AddUnknownFields(root, "$", TopLevelFields, errors);

        var schemaVersionValid = ValidateSchemaVersion(root, errors);
        var intent = ReadIntent(root, errors);
        var payloads = ReadPayloads(root);

        if (payloads.Count == 0)
        {
            errors.Add(Error(PayloadRequiredCode, "$"));
        }
        else if (payloads.Count > 1)
        {
            errors.Add(Error(PayloadAmbiguousCode, "$"));
        }

        AiDirectiveDecision? decision = null;
        if (schemaVersionValid && intent is { } expectedIntent && payloads.Count == 1)
        {
            var payload = payloads[0];
            if (payload.Intent != expectedIntent)
            {
                errors.Add(Error(PayloadIntentMismatchCode, "$"));
            }
            else
            {
                decision = ParsePayload(expectedIntent, payload.Element, errors);
            }
        }

        return errors.Count == 0 && decision is not null
            ? AiDirectiveDecisionParseResult.Success(decision)
            : AiDirectiveDecisionParseResult.Failure(errors);
    }

    private static JsonDocument? ParseJson(
        string output,
        out AiDirectiveDecisionParseError? error)
    {
        try
        {
            error = null;
            return JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            error = Error(InvalidJsonCode, "$");
            return null;
        }
    }

    private static bool ValidateSchemaVersion(
        JsonElement root,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (!root.TryGetProperty(AiDirectiveDecisionSchema.SchemaVersionProperty, out var version))
        {
            errors.Add(Error(RequiredFieldCode, AiDirectiveDecisionSchema.SchemaVersionProperty));
            return false;
        }

        if (version.ValueKind is not JsonValueKind.Number ||
            !version.TryGetInt32(out var parsed) ||
            parsed != AiDirectiveDecisionSchema.SchemaVersion)
        {
            errors.Add(Error(InvalidSchemaVersionCode, AiDirectiveDecisionSchema.SchemaVersionProperty));
            return false;
        }

        return true;
    }

    private static AiDirectiveDecisionIntent? ReadIntent(
        JsonElement root,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (!root.TryGetProperty(AiDirectiveDecisionSchema.IntentProperty, out var intent))
        {
            errors.Add(Error(RequiredFieldCode, AiDirectiveDecisionSchema.IntentProperty));
            return null;
        }

        if (intent.ValueKind is not JsonValueKind.String ||
            !AiDirectiveDecisionIntentContract.TryParseWireValue(intent.GetString(), out var parsed))
        {
            errors.Add(Error(InvalidIntentCode, AiDirectiveDecisionSchema.IntentProperty));
            return null;
        }

        return parsed;
    }

    private static List<DecisionPayload> ReadPayloads(JsonElement root)
    {
        var payloads = new List<DecisionPayload>(capacity: 3);

        AddPayloadIfPresent(
            root,
            AiDirectiveDecisionSchema.ReportPayloadProperty,
            AiDirectiveDecisionIntent.Report,
            payloads);
        AddPayloadIfPresent(
            root,
            AiDirectiveDecisionSchema.EscalationPayloadProperty,
            AiDirectiveDecisionIntent.Escalation,
            payloads);
        AddPayloadIfPresent(
            root,
            AiDirectiveDecisionSchema.DirectivePayloadProperty,
            AiDirectiveDecisionIntent.Directive,
            payloads);

        return payloads;
    }

    private static void AddPayloadIfPresent(
        JsonElement root,
        string propertyName,
        AiDirectiveDecisionIntent intent,
        ICollection<DecisionPayload> payloads)
    {
        if (root.TryGetProperty(propertyName, out var payload))
        {
            payloads.Add(new DecisionPayload(intent, propertyName, payload));
        }
    }

    private static AiDirectiveDecision? ParsePayload(
        AiDirectiveDecisionIntent intent,
        JsonElement payload,
        ICollection<AiDirectiveDecisionParseError> errors) =>
        intent switch
        {
            AiDirectiveDecisionIntent.Report => ParseReport(payload, errors),
            AiDirectiveDecisionIntent.Escalation => ParseEscalation(payload, errors),
            AiDirectiveDecisionIntent.Directive => ParseDirective(payload, errors),
            _ => throw new InvalidOperationException("Validated decision intent is not mapped."),
        };

    private static AiDirectiveReportDecision? ParseReport(
        JsonElement payload,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        const string payloadPath = AiDirectiveDecisionSchema.ReportPayloadProperty;
        if (!RequireObject(payload, payloadPath, errors))
        {
            return null;
        }

        AddUnknownFields(payload, payloadPath, ReportFields, errors);

        var kind = ReadReportKind(
            payload,
            AiDirectiveDecisionSchema.ReportKindField,
            $"{payloadPath}.{AiDirectiveDecisionSchema.ReportKindField}",
            errors);
        var body = ReadRequiredString(
            payload,
            AiDirectiveDecisionSchema.ReportBodyField,
            $"{payloadPath}.{AiDirectiveDecisionSchema.ReportBodyField}",
            errors);

        return kind is { } parsedKind && body is not null && !HasPayloadErrors(errors, payloadPath)
            ? new AiDirectiveReportDecision(parsedKind, body)
            : null;
    }

    private static AiDirectiveEscalationDecision? ParseEscalation(
        JsonElement payload,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        const string payloadPath = AiDirectiveDecisionSchema.EscalationPayloadProperty;
        if (!RequireObject(payload, payloadPath, errors))
        {
            return null;
        }

        AddUnknownFields(payload, payloadPath, EscalationFields, errors);

        var issue = ReadRequiredString(
            payload,
            AiDirectiveDecisionSchema.EscalationIssueField,
            $"{payloadPath}.{AiDirectiveDecisionSchema.EscalationIssueField}",
            errors);
        var context = ReadRequiredString(
            payload,
            AiDirectiveDecisionSchema.EscalationContextField,
            $"{payloadPath}.{AiDirectiveDecisionSchema.EscalationContextField}",
            errors);
        var options = ReadOptionsConsidered(
            payload,
            $"{payloadPath}.{AiDirectiveDecisionSchema.EscalationOptionsConsideredField}",
            errors);

        return issue is not null &&
            context is not null &&
            options is { } parsedOptions &&
            !HasPayloadErrors(errors, payloadPath)
                ? new AiDirectiveEscalationDecision(issue, context, parsedOptions)
                : null;
    }

    private static AiDirectiveChildDirectiveDecision? ParseDirective(
        JsonElement payload,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        const string payloadPath = AiDirectiveDecisionSchema.DirectivePayloadProperty;
        if (!RequireObject(payload, payloadPath, errors))
        {
            return null;
        }

        AddUnknownFields(payload, payloadPath, DirectiveFields, errors);

        var target = ReadPositionId(
            payload,
            $"{payloadPath}.{AiDirectiveDecisionSchema.DirectiveTargetPositionIdField}",
            errors);
        var objective = ReadRequiredString(
            payload,
            AiDirectiveDecisionSchema.DirectiveObjectiveField,
            $"{payloadPath}.{AiDirectiveDecisionSchema.DirectiveObjectiveField}",
            errors);
        var context = ReadRequiredString(
            payload,
            AiDirectiveDecisionSchema.DirectiveContextField,
            $"{payloadPath}.{AiDirectiveDecisionSchema.DirectiveContextField}",
            errors);

        return target is not null &&
            objective is not null &&
            context is not null &&
            !HasPayloadErrors(errors, payloadPath)
                ? new AiDirectiveChildDirectiveDecision(target, objective, context)
                : null;
    }

    private static bool RequireObject(
        JsonElement element,
        string path,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (element.ValueKind is JsonValueKind.Object)
        {
            return true;
        }

        errors.Add(Error(InvalidFieldCode, path));
        return false;
    }

    private static string? ReadRequiredString(
        JsonElement payload,
        string propertyName,
        string path,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (!payload.TryGetProperty(propertyName, out var value))
        {
            errors.Add(Error(RequiredFieldCode, path));
            return null;
        }

        if (value.ValueKind is not JsonValueKind.String)
        {
            errors.Add(Error(InvalidFieldCode, path));
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) ||
            !string.Equals(text, text.Trim(), StringComparison.Ordinal))
        {
            errors.Add(Error(InvalidFieldCode, path));
            return null;
        }

        return text;
    }

    private static ReportKind? ReadReportKind(
        JsonElement payload,
        string propertyName,
        string path,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (!payload.TryGetProperty(propertyName, out var value))
        {
            errors.Add(Error(RequiredFieldCode, path));
            return null;
        }

        if (value.ValueKind is not JsonValueKind.String ||
            !TryParseAiReportKind(value.GetString(), out var kind))
        {
            errors.Add(Error(InvalidFieldCode, path));
            return null;
        }

        return kind;
    }

    private static bool TryParseAiReportKind(string? value, out ReportKind kind)
    {
        switch (value)
        {
            case "Progress":
                kind = ReportKind.Progress;
                return true;
            case "Done":
                kind = ReportKind.Done;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static ImmutableArray<string>? ReadOptionsConsidered(
        JsonElement payload,
        string path,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (!payload.TryGetProperty(
            AiDirectiveDecisionSchema.EscalationOptionsConsideredField,
            out var value))
        {
            errors.Add(Error(RequiredFieldCode, path));
            return null;
        }

        if (value.ValueKind is not JsonValueKind.Array)
        {
            errors.Add(Error(InvalidFieldCode, path));
            return null;
        }

        var options = ImmutableArray.CreateBuilder<string>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var itemPath = $"{path}[{index}]";
            if (item.ValueKind is not JsonValueKind.String)
            {
                errors.Add(Error(InvalidFieldCode, itemPath));
            }
            else
            {
                var option = item.GetString();
                if (string.IsNullOrWhiteSpace(option) ||
                    !string.Equals(option, option.Trim(), StringComparison.Ordinal))
                {
                    errors.Add(Error(InvalidFieldCode, itemPath));
                }
                else
                {
                    options.Add(option);
                }
            }

            index++;
        }

        return options.ToImmutable();
    }

    private static PositionId? ReadPositionId(
        JsonElement payload,
        string path,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        var raw = ReadRequiredString(
            payload,
            AiDirectiveDecisionSchema.DirectiveTargetPositionIdField,
            path,
            errors);
        if (raw is null)
        {
            return null;
        }

        try
        {
            return PositionId.From(raw);
        }
        catch (ArgumentException)
        {
            errors.Add(Error(InvalidFieldCode, path));
            return null;
        }
    }

    private static void AddUnknownFields(
        JsonElement element,
        string pathPrefix,
        IReadOnlyCollection<string> allowedFields,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedFields.Contains(property.Name, StringComparer.Ordinal))
            {
                var path = pathPrefix == "$"
                    ? property.Name
                    : $"{pathPrefix}.{property.Name}";
                errors.Add(Error(UnknownFieldCode, path));
            }
        }
    }

    private static bool HasPayloadErrors(
        IEnumerable<AiDirectiveDecisionParseError> errors,
        string payloadPath) =>
        errors.Any(error =>
            error.Path == payloadPath ||
            error.Path.StartsWith($"{payloadPath}.", StringComparison.Ordinal));

    private static AiDirectiveDecisionParseResult Failure(AiDirectiveDecisionParseError error) =>
        AiDirectiveDecisionParseResult.Failure([error]);

    private static AiDirectiveDecisionParseError Error(string code, string path) =>
        new(code, path);

    private readonly record struct DecisionPayload(
        AiDirectiveDecisionIntent Intent,
        string PropertyName,
        JsonElement Element);
}
