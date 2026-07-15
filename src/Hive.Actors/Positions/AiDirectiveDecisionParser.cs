using System.Collections.Immutable;
using System.Text.Json;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Actors.Positions;

internal sealed record AiDirectiveDecisionParseError
{
    public AiDirectiveDecisionParseError(string code, string path)
    {
        Code = AiDirectiveDecisionParseDiagnosticContract.RequireCode(code);
        Path = AiDirectiveDecisionParseDiagnosticContract.RequirePath(path);
    }

    public string Code { get; }

    public string Path { get; }
}

internal static class AiDirectiveDecisionParseDiagnosticContract
{
    public const int Version = 1;

    public const string EmptyResponseCode = "empty-response";
    public const string InvalidJsonCode = "invalid-json";
    public const string TopLevelObjectRequiredCode = "top-level-object-required";
    public const string RequiredFieldCode = "required-field";
    public const string InvalidSchemaVersionCode = "invalid-schema-version";
    public const string InvalidIntentCode = "invalid-intent";
    public const string PayloadRequiredCode = "payload-required";
    public const string PayloadAmbiguousCode = "payload-ambiguous";
    public const string PayloadIntentMismatchCode = "payload-intent-mismatch";
    public const string UnknownFieldCode = "unknown-field";
    public const string InvalidFieldCode = "invalid-field";

    public static ImmutableArray<string> Codes { get; } =
    [
        EmptyResponseCode,
        InvalidFieldCode,
        InvalidIntentCode,
        InvalidJsonCode,
        InvalidSchemaVersionCode,
        PayloadAmbiguousCode,
        PayloadIntentMismatchCode,
        PayloadRequiredCode,
        RequiredFieldCode,
        TopLevelObjectRequiredCode,
        UnknownFieldCode,
    ];

    public static ImmutableArray<string> Paths { get; } =
    [
        "$",
        AiDirectiveDecisionSchema.ActingUnderProperty,
        AiDirectiveDecisionSchema.DecisionProperty,
        $"{AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.DirectivePayloadProperty}",
        $"{AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.DirectivePayloadProperty}.{AiDirectiveDecisionSchema.DirectiveContextField}",
        $"{AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.DirectivePayloadProperty}.{AiDirectiveDecisionSchema.DirectiveObjectiveField}",
        $"{AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.DirectivePayloadProperty}.{AiDirectiveDecisionSchema.DirectiveTargetPositionIdField}",
        $"{AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.EscalationPayloadProperty}",
        $"{AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.EscalationPayloadProperty}.{AiDirectiveDecisionSchema.EscalationContextField}",
        $"{AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.EscalationPayloadProperty}.{AiDirectiveDecisionSchema.EscalationIssueField}",
        $"{AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.EscalationPayloadProperty}.{AiDirectiveDecisionSchema.EscalationOptionsConsideredField}",
        $"{AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.EscalationPayloadProperty}.{AiDirectiveDecisionSchema.EscalationOptionsConsideredField}.item",
        $"{AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.IntentProperty}",
        $"{AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.ReportPayloadProperty}",
        $"{AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.ReportPayloadProperty}.{AiDirectiveDecisionSchema.ReportBodyField}",
        $"{AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.ReportPayloadProperty}.{AiDirectiveDecisionSchema.ReportKindField}",
        AiDirectiveDecisionSchema.DirectivePayloadProperty,
        $"{AiDirectiveDecisionSchema.DirectivePayloadProperty}.{AiDirectiveDecisionSchema.DirectiveContextField}",
        $"{AiDirectiveDecisionSchema.DirectivePayloadProperty}.{AiDirectiveDecisionSchema.DirectiveObjectiveField}",
        $"{AiDirectiveDecisionSchema.DirectivePayloadProperty}.{AiDirectiveDecisionSchema.DirectiveTargetPositionIdField}",
        AiDirectiveDecisionSchema.EscalationPayloadProperty,
        $"{AiDirectiveDecisionSchema.EscalationPayloadProperty}.{AiDirectiveDecisionSchema.EscalationContextField}",
        $"{AiDirectiveDecisionSchema.EscalationPayloadProperty}.{AiDirectiveDecisionSchema.EscalationIssueField}",
        $"{AiDirectiveDecisionSchema.EscalationPayloadProperty}.{AiDirectiveDecisionSchema.EscalationOptionsConsideredField}",
        $"{AiDirectiveDecisionSchema.EscalationPayloadProperty}.{AiDirectiveDecisionSchema.EscalationOptionsConsideredField}.item",
        AiDirectiveDecisionSchema.IntentProperty,
        AiDirectiveDecisionSchema.ReportPayloadProperty,
        $"{AiDirectiveDecisionSchema.ReportPayloadProperty}.{AiDirectiveDecisionSchema.ReportBodyField}",
        $"{AiDirectiveDecisionSchema.ReportPayloadProperty}.{AiDirectiveDecisionSchema.ReportKindField}",
        AiDirectiveDecisionSchema.SchemaVersionProperty,
    ];

    public static string RequireCode(string code) => Require(code, Codes, nameof(code));

    public static string RequirePath(string path) => Require(path, Paths, nameof(path));

    private static string Require(
        string value,
        ImmutableArray<string> vocabulary,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!vocabulary.Contains(value, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "AI directive parse diagnostic value is outside the closed vocabulary.",
                parameterName);
        }

        return value;
    }
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
            .Distinct()
            .OrderBy(error => error.Path, StringComparer.Ordinal)
            .ThenBy(error => error.Code, StringComparer.Ordinal)
            .ToImmutableArray();

        return new AiDirectiveDecisionParseResult(null, ordered);
    }
}

internal static class AiDirectiveDecisionParser
{
    private const string EmptyResponseCode = AiDirectiveDecisionParseDiagnosticContract.EmptyResponseCode;
    private const string InvalidJsonCode = AiDirectiveDecisionParseDiagnosticContract.InvalidJsonCode;
    private const string TopLevelObjectRequiredCode = AiDirectiveDecisionParseDiagnosticContract.TopLevelObjectRequiredCode;
    private const string RequiredFieldCode = AiDirectiveDecisionParseDiagnosticContract.RequiredFieldCode;
    private const string InvalidSchemaVersionCode = AiDirectiveDecisionParseDiagnosticContract.InvalidSchemaVersionCode;
    private const string InvalidIntentCode = AiDirectiveDecisionParseDiagnosticContract.InvalidIntentCode;
    private const string PayloadRequiredCode = AiDirectiveDecisionParseDiagnosticContract.PayloadRequiredCode;
    private const string PayloadAmbiguousCode = AiDirectiveDecisionParseDiagnosticContract.PayloadAmbiguousCode;
    private const string PayloadIntentMismatchCode = AiDirectiveDecisionParseDiagnosticContract.PayloadIntentMismatchCode;
    private const string UnknownFieldCode = AiDirectiveDecisionParseDiagnosticContract.UnknownFieldCode;
    private const string InvalidFieldCode = AiDirectiveDecisionParseDiagnosticContract.InvalidFieldCode;

    private static readonly string[] TopLevelFields =
    [
        AiDirectiveDecisionSchema.SchemaVersionProperty,
        AiDirectiveDecisionSchema.IntentProperty,
        AiDirectiveDecisionSchema.ActingUnderProperty,
        AiDirectiveDecisionSchema.ReportPayloadProperty,
        AiDirectiveDecisionSchema.EscalationPayloadProperty,
        AiDirectiveDecisionSchema.DirectivePayloadProperty,
    ];

    private static readonly string[] CanonicalTopLevelFields =
    [
        AiDirectiveDecisionSchema.SchemaVersionProperty,
        AiDirectiveDecisionSchema.ActingUnderProperty,
        AiDirectiveDecisionSchema.DecisionProperty,
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

    public static AiDirectiveDecisionParseResult Parse(
        string? output,
        IEnumerable<AuthorityKey>? canDecide = null)
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
        var schemaVersionValid = ValidateSchemaVersion(root, errors);
        var actingUnder = ReadActingUnder(root, canDecide ?? []);
        var hasCanonicalEnvelope = root.TryGetProperty(
            AiDirectiveDecisionSchema.DecisionProperty,
            out _);
        var decisionEnvelope = ReadDecisionEnvelope(root, errors);
        AddUnknownFields(
            root,
            "$",
            hasCanonicalEnvelope ? CanonicalTopLevelFields : TopLevelFields,
            errors);
        if (hasCanonicalEnvelope && !decisionEnvelope.HasValue)
        {
            return AiDirectiveDecisionParseResult.Failure(errors);
        }

        var canonical = decisionEnvelope.HasValue;
        var decisionRoot = decisionEnvelope ?? root;
        var decisionPath = canonical ? AiDirectiveDecisionSchema.DecisionProperty : "$";
        if (canonical)
        {
            AddUnknownFields(
                decisionRoot,
                decisionPath,
                TopLevelFields.Where(field =>
                    field != AiDirectiveDecisionSchema.SchemaVersionProperty &&
                    field != AiDirectiveDecisionSchema.ActingUnderProperty).ToArray(),
                errors);
        }

        var intent = ReadIntent(decisionRoot, decisionPath, errors);
        var payloads = ReadPayloads(decisionRoot);

        if (payloads.Count == 0)
        {
            errors.Add(Error(PayloadRequiredCode, decisionPath));
        }
        else if (payloads.Count > 1)
        {
            errors.Add(Error(PayloadAmbiguousCode, decisionPath));
        }

        AiDirectiveDecision? decision = null;
        if (schemaVersionValid && intent is { } expectedIntent && payloads.Count == 1)
        {
            var payload = payloads[0];
            if (payload.Intent != expectedIntent)
            {
                errors.Add(Error(PayloadIntentMismatchCode, decisionPath));
            }
            else
            {
                decision = ParsePayload(
                    expectedIntent,
                    payload.Element,
                    decisionPath,
                    actingUnder,
                    errors);
            }
        }

        return errors.Count == 0 && decision is not null
            ? AiDirectiveDecisionParseResult.Success(decision)
            : AiDirectiveDecisionParseResult.Failure(errors);
    }

    private static JsonElement? ReadDecisionEnvelope(
        JsonElement root,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (!root.TryGetProperty(AiDirectiveDecisionSchema.DecisionProperty, out var decision))
        {
            return null;
        }

        if (decision.ValueKind is not JsonValueKind.Object)
        {
            errors.Add(Error(InvalidFieldCode, AiDirectiveDecisionSchema.DecisionProperty));
            return null;
        }

        return decision;
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
        string pathPrefix,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        var path = ChildPath(pathPrefix, AiDirectiveDecisionSchema.IntentProperty);
        if (!root.TryGetProperty(AiDirectiveDecisionSchema.IntentProperty, out var intent))
        {
            errors.Add(Error(RequiredFieldCode, path));
            return null;
        }

        if (intent.ValueKind is not JsonValueKind.String ||
            !AiDirectiveDecisionIntentContract.TryParseWireValue(intent.GetString(), out var parsed))
        {
            errors.Add(Error(InvalidIntentCode, path));
            return null;
        }

        return parsed;
    }

    private static ActingUnderDeclaration ReadActingUnder(
        JsonElement root,
        IEnumerable<AuthorityKey> canDecide)
    {
        if (!root.TryGetProperty(AiDirectiveDecisionSchema.ActingUnderProperty, out var value))
        {
            return ActingUnderDeclaration.Resolve(
                fieldPresent: false,
                value: null,
                allowedKeys: canDecide);
        }

        return ActingUnderDeclaration.Resolve(
            fieldPresent: true,
            value: value.ValueKind is JsonValueKind.String ? value.GetString() : null,
            allowedKeys: canDecide);
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
        if (root.TryGetProperty(propertyName, out var payload) &&
            payload.ValueKind is not JsonValueKind.Null)
        {
            payloads.Add(new DecisionPayload(intent, propertyName, payload));
        }
    }

    private static AiDirectiveDecision? ParsePayload(
        AiDirectiveDecisionIntent intent,
        JsonElement payload,
        string decisionPath,
        ActingUnderDeclaration actingUnder,
        ICollection<AiDirectiveDecisionParseError> errors) =>
        intent switch
        {
            AiDirectiveDecisionIntent.Report => ParseReport(payload, decisionPath, actingUnder, errors),
            AiDirectiveDecisionIntent.Escalation => ParseEscalation(payload, decisionPath, actingUnder, errors),
            AiDirectiveDecisionIntent.Directive => ParseDirective(payload, decisionPath, actingUnder, errors),
            _ => throw new InvalidOperationException("Validated decision intent is not mapped."),
        };

    private static AiDirectiveReportDecision? ParseReport(
        JsonElement payload,
        string decisionPath,
        ActingUnderDeclaration actingUnder,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        var payloadPath = ChildPath(decisionPath, AiDirectiveDecisionSchema.ReportPayloadProperty);
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
            ? new AiDirectiveReportDecision(parsedKind, body, actingUnder)
            : null;
    }

    private static AiDirectiveEscalationDecision? ParseEscalation(
        JsonElement payload,
        string decisionPath,
        ActingUnderDeclaration actingUnder,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        var payloadPath = ChildPath(decisionPath, AiDirectiveDecisionSchema.EscalationPayloadProperty);
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
                ? new AiDirectiveEscalationDecision(
                    issue,
                    context,
                    parsedOptions,
                    actingUnder)
                : null;
    }

    private static AiDirectiveChildDirectiveDecision? ParseDirective(
        JsonElement payload,
        string decisionPath,
        ActingUnderDeclaration actingUnder,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        var payloadPath = ChildPath(decisionPath, AiDirectiveDecisionSchema.DirectivePayloadProperty);
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
                ? new AiDirectiveChildDirectiveDecision(
                    target,
                    objective,
                    context,
                    actingUnder)
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

        var text = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
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
        foreach (var item in value.EnumerateArray())
        {
            var itemPath = $"{path}.item";
            if (item.ValueKind is not JsonValueKind.String)
            {
                errors.Add(Error(InvalidFieldCode, itemPath));
            }
            else
            {
                var option = item.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(option))
                {
                    errors.Add(Error(InvalidFieldCode, itemPath));
                }
                else
                {
                    options.Add(option);
                }
            }
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
                errors.Add(Error(UnknownFieldCode, pathPrefix));
            }
        }
    }

    private static string ChildPath(string parent, string child) =>
        parent == "$" ? child : $"{parent}.{child}";

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
