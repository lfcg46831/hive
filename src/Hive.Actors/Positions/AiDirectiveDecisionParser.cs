using System.Collections.Immutable;
using System.Text.Json;
using Hive.Domain.Directives;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Outcomes;

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
    public const int Version = 3;

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
    public const string InvalidVocabularyCode = "invalid-vocabulary";
    public const string DuplicateFieldCode = "duplicate-field";
    public const string ContradictoryCombinationCode = "contradictory-combination";

    public static ImmutableArray<string> Codes { get; } =
    [
        ContradictoryCombinationCode,
        DuplicateFieldCode,
        EmptyResponseCode,
        InvalidFieldCode,
        InvalidIntentCode,
        InvalidJsonCode,
        InvalidSchemaVersionCode,
        InvalidVocabularyCode,
        PayloadAmbiguousCode,
        PayloadIntentMismatchCode,
        PayloadRequiredCode,
        RequiredFieldCode,
        TopLevelObjectRequiredCode,
        UnknownFieldCode,
    ];

    public static ImmutableArray<string> Paths { get; } = BuildPaths();

    private static ImmutableArray<string> BuildPaths()
    {
        var paths = new List<string>
        {
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
        };
        AddCheckpointPaths(
            paths,
            $"{AiDirectiveDecisionSchema.DecisionProperty}.{AiDirectiveDecisionSchema.ReportPayloadProperty}.{AiDirectiveDecisionSchema.CheckpointPayloadField}");
        AddCheckpointPaths(
            paths,
            $"{AiDirectiveDecisionSchema.ReportPayloadProperty}.{AiDirectiveDecisionSchema.CheckpointPayloadField}");
        paths.AddRange(OutcomeProposalParseDiagnosticContract.Paths.Select(path =>
            path == "$"
                ? AiDirectiveOutcomeProposalEnvelope.PropertyName
                : $"{AiDirectiveOutcomeProposalEnvelope.PropertyName}.{path}"));
        return paths
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static void AddCheckpointPaths(ICollection<string> paths, string prefix)
    {
        paths.Add(prefix);
        paths.Add($"{prefix}.contract_version");
        paths.Add($"{prefix}.plan");
        paths.Add($"{prefix}.plan.contract_version");
        paths.Add($"{prefix}.plan.subtasks");
        paths.Add($"{prefix}.plan.subtasks.item");
        paths.Add($"{prefix}.plan.subtasks.item.sequence");
        paths.Add($"{prefix}.plan.subtasks.item.local_id");
        paths.Add($"{prefix}.plan.subtasks.item.objective");
        paths.Add($"{prefix}.plan.subtasks.item.completion_criteria");
        paths.Add($"{prefix}.plan.subtasks.item.completion_criteria.item");
        paths.Add($"{prefix}.plan.subtasks.item.estimated_duration_ms");
        paths.Add($"{prefix}.completed_subtasks");
        paths.Add($"{prefix}.completed_subtasks.item");
        paths.Add($"{prefix}.completed_subtasks.item.local_id");
        paths.Add($"{prefix}.completed_subtasks.item.evidence_references");
        paths.Add($"{prefix}.completed_subtasks.item.evidence_references.item");
        paths.Add($"{prefix}.completed_subtasks.item.evidence_references.item.source");
        paths.Add($"{prefix}.completed_subtasks.item.evidence_references.item.reference");
        paths.Add($"{prefix}.blockers");
        paths.Add($"{prefix}.blockers.item");
        paths.Add($"{prefix}.next_subtask_id");
    }

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
        OutcomeProposal? proposal,
        AiDirectiveDecision? acceptedDecision,
        OutcomeProposal? acceptedProposal,
        ImmutableArray<AiDirectiveDecisionParseError> errors)
    {
        Decision = decision;
        Proposal = proposal;
        AcceptedDecision = acceptedDecision ?? decision;
        AcceptedProposal = acceptedProposal ?? proposal;
        Errors = errors;
    }

    public bool IsSuccess => Decision is not null;

    public bool IsFailure => !IsSuccess;

    public AiDirectiveDecision? Decision { get; }

    public OutcomeProposal? Proposal { get; }

    public AiDirectiveDecision? AcceptedDecision { get; }

    public OutcomeProposal? AcceptedProposal { get; }

    public IReadOnlyList<AiDirectiveDecisionParseError> Errors { get; }

    public static AiDirectiveDecisionParseResult Success(
        AiDirectiveDecision decision,
        OutcomeProposal? proposal = null)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return new AiDirectiveDecisionParseResult(
            decision,
            proposal,
            decision,
            proposal,
            ImmutableArray<AiDirectiveDecisionParseError>.Empty);
    }

    public static AiDirectiveDecisionParseResult Failure(
        IEnumerable<AiDirectiveDecisionParseError> errors,
        AiDirectiveDecision? acceptedDecision = null,
        OutcomeProposal? acceptedProposal = null)
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

        return new AiDirectiveDecisionParseResult(
            decision: null,
            proposal: null,
            acceptedDecision,
            acceptedProposal,
            errors: ordered);
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
    private const string InvalidVocabularyCode = AiDirectiveDecisionParseDiagnosticContract.InvalidVocabularyCode;
    private const string DuplicateFieldCode = AiDirectiveDecisionParseDiagnosticContract.DuplicateFieldCode;

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
        AiDirectiveDecisionSchema.CheckpointPayloadField,
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

    private static readonly string[] CheckpointFields =
    [
        "contract_version",
        "plan",
        "completed_subtasks",
        "blockers",
        "next_subtask_id",
    ];

    private static readonly string[] CheckpointPlanFields =
    [
        "contract_version",
        "subtasks",
    ];

    private static readonly string[] CheckpointSubtaskFields =
    [
        "sequence",
        "local_id",
        "objective",
        "completion_criteria",
        "estimated_duration_ms",
    ];

    private static readonly string[] CompletedSubtaskFields =
    [
        "local_id",
        "evidence_references",
    ];

    private static readonly string[] EvidenceReferenceFields =
    [
        "source",
        "reference",
    ];

    public static AiDirectiveDecisionParseResult Parse(
        string? output,
        IEnumerable<AuthorityKey>? canDecide = null,
        bool requireOutcomeProposal = false,
        OutcomeProposalEvidenceContext? outcomeProposalEvidenceContext = null,
        bool allowProgressReports = false)
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
        var proposal = ReadOutcomeProposal(
            root,
            requireOutcomeProposal,
            outcomeProposalEvidenceContext,
            errors);
        AddUnknownFields(
            root,
            "$",
            AllowedTopLevelFields(
                hasCanonicalEnvelope,
                requireOutcomeProposal),
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
                    allowProgressReports,
                    errors);
            }
        }

        if (decision is not null && proposal is not null &&
            !AiDirectiveOutcomeProposalEnvelope.IsCompatible(decision, proposal))
        {
            errors.Add(Error(
                AiDirectiveDecisionParseDiagnosticContract.ContradictoryCombinationCode,
                $"{AiDirectiveOutcomeProposalEnvelope.PropertyName}." +
                $"{OutcomeProposalConstraint.ProposalProperty}." +
                OutcomeProposalConstraint.ProposedIntentProperty));
        }

        return errors.Count == 0 && decision is not null
            ? AiDirectiveDecisionParseResult.Success(decision, proposal)
            : AiDirectiveDecisionParseResult.Failure(
                errors,
                decision,
                proposal);
    }

    private static string[] AllowedTopLevelFields(
        bool hasCanonicalEnvelope,
        bool requireOutcomeProposal)
    {
        var allowed = hasCanonicalEnvelope ? CanonicalTopLevelFields : TopLevelFields;
        return requireOutcomeProposal
            ? [.. allowed, AiDirectiveOutcomeProposalEnvelope.PropertyName]
            : allowed;
    }

    private static OutcomeProposal? ReadOutcomeProposal(
        JsonElement root,
        bool requireOutcomeProposal,
        OutcomeProposalEvidenceContext? evidenceContext,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (!requireOutcomeProposal)
        {
            return null;
        }

        if (root.EnumerateObject().Count(property =>
            property.NameEquals(AiDirectiveOutcomeProposalEnvelope.PropertyName)) > 1)
        {
            errors.Add(Error(
                DuplicateFieldCode,
                AiDirectiveOutcomeProposalEnvelope.PropertyName));
        }

        if (!root.TryGetProperty(AiDirectiveOutcomeProposalEnvelope.PropertyName, out var value))
        {
            errors.Add(Error(
                RequiredFieldCode,
                AiDirectiveOutcomeProposalEnvelope.PropertyName));
            return null;
        }

        var parsed = OutcomeProposalParser.Parse(
            JsonSerializer.Serialize(value),
            evidenceContext);
        if (parsed.IsSuccess)
        {
            return parsed.Proposal;
        }

        foreach (var error in parsed.Errors)
        {
            errors.Add(Error(
                error.Code,
                error.Path == "$"
                    ? AiDirectiveOutcomeProposalEnvelope.PropertyName
                    : $"{AiDirectiveOutcomeProposalEnvelope.PropertyName}.{error.Path}"));
        }

        return null;
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
        bool allowProgressReports,
        ICollection<AiDirectiveDecisionParseError> errors) =>
        intent switch
        {
            AiDirectiveDecisionIntent.Report => ParseReport(
                payload,
                decisionPath,
                actingUnder,
                allowProgressReports,
                errors),
            AiDirectiveDecisionIntent.Escalation => ParseEscalation(payload, decisionPath, actingUnder, errors),
            AiDirectiveDecisionIntent.Directive => ParseDirective(payload, decisionPath, actingUnder, errors),
            _ => throw new InvalidOperationException("Validated decision intent is not mapped."),
        };

    private static AiDirectiveReportDecision? ParseReport(
        JsonElement payload,
        string decisionPath,
        ActingUnderDeclaration actingUnder,
        bool allowProgressReports,
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

        var checkpointPath = $"{payloadPath}.{AiDirectiveDecisionSchema.CheckpointPayloadField}";
        var checkpoint = ReadCheckpointProposal(payload, checkpointPath, errors);
        if ((kind != ReportKind.Progress || !allowProgressReports) && checkpoint is not null)
        {
            errors.Add(Error(
                AiDirectiveDecisionParseDiagnosticContract.ContradictoryCombinationCode,
                checkpointPath));
        }

        return kind is { } parsedKind && body is not null && !HasPayloadErrors(errors, payloadPath)
            ? new AiDirectiveReportDecision(parsedKind, body, actingUnder, checkpoint)
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

    private static AiDirectiveCheckpointProposal? ReadCheckpointProposal(
        JsonElement report,
        string path,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (!report.TryGetProperty(
                AiDirectiveDecisionSchema.CheckpointPayloadField,
                out var checkpoint))
        {
            return null;
        }

        if (!RequireObject(checkpoint, path, errors))
        {
            return null;
        }

        AddUnknownFields(checkpoint, path, CheckpointFields, errors);
        var version = ReadRequiredInt32(
            checkpoint,
            "contract_version",
            $"{path}.contract_version",
            errors);
        var plan = ReadCheckpointPlan(checkpoint, $"{path}.plan", errors);
        var completed = ReadCompletedSubtasks(
            checkpoint,
            $"{path}.completed_subtasks",
            errors);
        var blockers = ReadCheckpointBlockers(checkpoint, $"{path}.blockers", errors);
        var next = ReadRequiredString(
            checkpoint,
            "next_subtask_id",
            $"{path}.next_subtask_id",
            errors);

        if (version is null || plan is null || completed is null || blockers is null ||
            next is null || HasPayloadErrors(errors, path))
        {
            return null;
        }

        try
        {
            return new AiDirectiveCheckpointProposal(
                version.Value,
                plan,
                completed.Value,
                blockers.Value,
                next);
        }
        catch (ArgumentException)
        {
            errors.Add(Error(InvalidFieldCode, path));
            return null;
        }
    }

    private static DirectiveCheckpointPlan? ReadCheckpointPlan(
        JsonElement checkpoint,
        string path,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (!checkpoint.TryGetProperty("plan", out var plan))
        {
            errors.Add(Error(RequiredFieldCode, path));
            return null;
        }

        if (!RequireObject(plan, path, errors))
        {
            return null;
        }

        AddUnknownFields(plan, path, CheckpointPlanFields, errors);
        var version = ReadRequiredInt32(
            plan,
            "contract_version",
            $"{path}.contract_version",
            errors);
        if (!plan.TryGetProperty("subtasks", out var subtasks) ||
            subtasks.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Error(
                plan.TryGetProperty("subtasks", out _)
                    ? InvalidFieldCode
                    : RequiredFieldCode,
                $"{path}.subtasks"));
            return null;
        }

        var parsed = ImmutableArray.CreateBuilder<DirectiveCheckpointSubtask>();
        foreach (var subtask in subtasks.EnumerateArray())
        {
            var itemPath = $"{path}.subtasks.item";
            if (!RequireObject(subtask, itemPath, errors))
            {
                continue;
            }

            AddUnknownFields(subtask, itemPath, CheckpointSubtaskFields, errors);
            var sequence = ReadRequiredInt32(
                subtask,
                "sequence",
                $"{itemPath}.sequence",
                errors);
            var localId = ReadRequiredString(
                subtask,
                "local_id",
                $"{itemPath}.local_id",
                errors);
            var objective = ReadRequiredString(
                subtask,
                "objective",
                $"{itemPath}.objective",
                errors);
            var criteria = ReadStringArray(
                subtask,
                "completion_criteria",
                $"{itemPath}.completion_criteria",
                errors);
            var estimatedMilliseconds = ReadRequiredInt64(
                subtask,
                "estimated_duration_ms",
                $"{itemPath}.estimated_duration_ms",
                errors);
            if (sequence is null || localId is null || objective is null || criteria is null ||
                estimatedMilliseconds is null)
            {
                continue;
            }

            if (estimatedMilliseconds.Value <= 0 ||
                estimatedMilliseconds.Value >
                DirectiveCheckpointContractLimits.MaximumEstimatedDuration.TotalMilliseconds)
            {
                errors.Add(Error(InvalidFieldCode, $"{itemPath}.estimated_duration_ms"));
                continue;
            }

            try
            {
                parsed.Add(new DirectiveCheckpointSubtask(
                    sequence.Value,
                    localId,
                    objective,
                    criteria.Value,
                    TimeSpan.FromMilliseconds(estimatedMilliseconds.Value)));
            }
            catch (ArgumentException)
            {
                errors.Add(Error(InvalidFieldCode, itemPath));
            }
        }

        if (version is null || HasPayloadErrors(errors, path))
        {
            return null;
        }

        try
        {
            return new DirectiveCheckpointPlan(version.Value, parsed.ToImmutable());
        }
        catch (ArgumentException)
        {
            errors.Add(Error(InvalidFieldCode, path));
            return null;
        }
    }

    private static ImmutableArray<CompletedDirectiveCheckpointSubtask>?
        ReadCompletedSubtasks(
        JsonElement checkpoint,
        string path,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (!checkpoint.TryGetProperty("completed_subtasks", out var completed) ||
            completed.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Error(
                checkpoint.TryGetProperty("completed_subtasks", out _)
                    ? InvalidFieldCode
                    : RequiredFieldCode,
                path));
            return null;
        }

        var parsed = ImmutableArray.CreateBuilder<CompletedDirectiveCheckpointSubtask>();
        foreach (var item in completed.EnumerateArray())
        {
            var itemPath = $"{path}.item";
            if (!RequireObject(item, itemPath, errors))
            {
                continue;
            }

            AddUnknownFields(item, itemPath, CompletedSubtaskFields, errors);
            var localId = ReadRequiredString(
                item,
                "local_id",
                $"{itemPath}.local_id",
                errors);
            var references = ReadEvidenceReferences(
                item,
                $"{itemPath}.evidence_references",
                errors);
            if (localId is null || references is null)
            {
                continue;
            }

            try
            {
                parsed.Add(new CompletedDirectiveCheckpointSubtask(
                    localId,
                    references.Value));
            }
            catch (ArgumentException)
            {
                errors.Add(Error(InvalidFieldCode, itemPath));
            }
        }

        if (parsed.Count == 0)
        {
            errors.Add(Error(InvalidFieldCode, path));
        }

        return HasPayloadErrors(errors, path) ? null : parsed.ToImmutable();
    }

    private static ImmutableArray<OutcomeEvidenceReference>? ReadEvidenceReferences(
        JsonElement completed,
        string path,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (!completed.TryGetProperty("evidence_references", out var references) ||
            references.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Error(
                completed.TryGetProperty("evidence_references", out _)
                    ? InvalidFieldCode
                    : RequiredFieldCode,
                path));
            return null;
        }

        var parsed = ImmutableArray.CreateBuilder<OutcomeEvidenceReference>();
        foreach (var item in references.EnumerateArray())
        {
            var itemPath = $"{path}.item";
            if (!RequireObject(item, itemPath, errors))
            {
                continue;
            }

            AddUnknownFields(item, itemPath, EvidenceReferenceFields, errors);
            var sourceValue = ReadRequiredString(
                item,
                "source",
                $"{itemPath}.source",
                errors);
            var reference = ReadRequiredString(
                item,
                "reference",
                $"{itemPath}.reference",
                errors);
            if (sourceValue is null || reference is null)
            {
                continue;
            }

            if (!OutcomeEvidenceSourceContract.TryParseWireValue(sourceValue, out var source))
            {
                errors.Add(Error(InvalidVocabularyCode, $"{itemPath}.source"));
                continue;
            }

            try
            {
                parsed.Add(new OutcomeEvidenceReference(source, reference));
            }
            catch (ArgumentException)
            {
                errors.Add(Error(InvalidFieldCode, itemPath));
            }
        }

        return HasPayloadErrors(errors, path) ? null : parsed.ToImmutable();
    }

    private static ImmutableArray<OutcomeBlocker>? ReadCheckpointBlockers(
        JsonElement checkpoint,
        string path,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (!checkpoint.TryGetProperty("blockers", out var blockers) ||
            blockers.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Error(
                checkpoint.TryGetProperty("blockers", out _)
                    ? InvalidFieldCode
                    : RequiredFieldCode,
                path));
            return null;
        }

        var parsed = ImmutableArray.CreateBuilder<OutcomeBlocker>();
        foreach (var blocker in blockers.EnumerateArray())
        {
            if (blocker.ValueKind != JsonValueKind.String ||
                !OutcomeBlockerContract.TryParseWireValue(blocker.GetString(), out var value))
            {
                errors.Add(Error(InvalidVocabularyCode, $"{path}.item"));
                continue;
            }

            parsed.Add(value);
        }

        return HasPayloadErrors(errors, path) ? null : parsed.ToImmutable();
    }

    private static ImmutableArray<string>? ReadStringArray(
        JsonElement parent,
        string propertyName,
        string path,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (!parent.TryGetProperty(propertyName, out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Error(
                parent.TryGetProperty(propertyName, out _)
                    ? InvalidFieldCode
                    : RequiredFieldCode,
                path));
            return null;
        }

        var parsed = ImmutableArray.CreateBuilder<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                errors.Add(Error(InvalidFieldCode, $"{path}.item"));
                continue;
            }

            parsed.Add(value.GetString()!.Trim());
        }

        return HasPayloadErrors(errors, path) ? null : parsed.ToImmutable();
    }

    private static int? ReadRequiredInt32(
        JsonElement parent,
        string propertyName,
        string path,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            errors.Add(Error(RequiredFieldCode, path));
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed))
        {
            errors.Add(Error(InvalidFieldCode, path));
            return null;
        }

        return parsed;
    }

    private static long? ReadRequiredInt64(
        JsonElement parent,
        string propertyName,
        string path,
        ICollection<AiDirectiveDecisionParseError> errors)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            errors.Add(Error(RequiredFieldCode, path));
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var parsed))
        {
            errors.Add(Error(InvalidFieldCode, path));
            return null;
        }

        return parsed;
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
