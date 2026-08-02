using System.Collections.Immutable;
using System.Text.Json;

namespace Hive.Domain.Outcomes;

public sealed record OutcomeProposalParseError
{
    public OutcomeProposalParseError(string code, string path)
    {
        Code = OutcomeProposalParseDiagnosticContract.RequireCode(code);
        Path = OutcomeProposalParseDiagnosticContract.RequirePath(path);
    }

    public string Code { get; }

    public string Path { get; }
}

public static class OutcomeProposalParseDiagnosticContract
{
    public const int Version = 2;

    public const string EmptyResponseCode = "empty-response";
    public const string InvalidJsonCode = "invalid-json";
    public const string TopLevelObjectRequiredCode = "top-level-object-required";
    public const string RequiredFieldCode = "required-field";
    public const string InvalidSchemaVersionCode = "invalid-schema-version";
    public const string InvalidVocabularyCode = "invalid-vocabulary";
    public const string InvalidFieldCode = "invalid-field";
    public const string UnknownFieldCode = "unknown-field";
    public const string DuplicateFieldCode = "duplicate-field";
    public const string ContradictoryCombinationCode = "contradictory-combination";

    public static ImmutableArray<string> Codes { get; } =
    [
        ContradictoryCombinationCode,
        DuplicateFieldCode,
        EmptyResponseCode,
        InvalidFieldCode,
        InvalidJsonCode,
        InvalidSchemaVersionCode,
        InvalidVocabularyCode,
        RequiredFieldCode,
        TopLevelObjectRequiredCode,
        UnknownFieldCode,
    ];

    public static ImmutableArray<string> Paths { get; } =
    [
        "$",
        OutcomeProposalConstraint.ProposalProperty,
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.AuthorityRequestProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.AuthorityRequestProperty}.{OutcomeProposalConstraint.AuthorityKindProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.AuthorityRequestProperty}.{OutcomeProposalConstraint.AuthorityReferenceProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.AuthorityRequestProperty}.{OutcomeProposalConstraint.AuthorityDecisionProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.AuthorityRequestProperty}.{OutcomeProposalConstraint.PositionLimitReasonProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.BlockersProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.BlockersProperty}.item",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.EvidenceReferencesProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.EvidenceReferencesProperty}.item",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.EvidenceReferencesProperty}.item.{OutcomeProposalConstraint.EvidenceReferenceProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.EvidenceReferencesProperty}.item.{OutcomeProposalConstraint.EvidenceSourceProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.InformationGapsProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.InformationGapsProperty}.item",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.InformationGapsProperty}.item.{OutcomeProposalConstraint.MaterialityProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.InformationGapsProperty}.item.{OutcomeProposalConstraint.MaterialityReasonProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.InformationGapsProperty}.item.{OutcomeProposalConstraint.MissingEvidenceReferenceProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.NextActionProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.ProposedIntentProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.RequiredInterventionProperty}",
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.WorkStateProperty}",
        OutcomeProposalConstraint.SchemaVersionProperty,
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
                "Outcome proposal parse diagnostic is outside the closed vocabulary.",
                parameterName);
        }

        return value;
    }
}

public sealed record OutcomeProposalParseResult
{
    private OutcomeProposalParseResult(
        OutcomeProposal? proposal,
        ImmutableArray<OutcomeProposalParseError> errors)
    {
        Proposal = proposal;
        Errors = errors;
    }

    public bool IsSuccess => Proposal is not null;

    public bool IsFailure => !IsSuccess;

    public OutcomeProposal? Proposal { get; }

    public IReadOnlyList<OutcomeProposalParseError> Errors { get; }

    public static OutcomeProposalParseResult Success(OutcomeProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return new OutcomeProposalParseResult(proposal, []);
    }

    public static OutcomeProposalParseResult Failure(
        IEnumerable<OutcomeProposalParseError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var snapshot = errors.ToImmutableArray();
        if (snapshot.IsEmpty || snapshot.Any(error => error is null))
        {
            throw new ArgumentException(
                "A failed outcome proposal parse must carry non-null errors.",
                nameof(errors));
        }

        var ordered = snapshot
            .Distinct()
            .OrderBy(error => error.Path, StringComparer.Ordinal)
            .ThenBy(error => error.Code, StringComparer.Ordinal)
            .ToImmutableArray();

        return new OutcomeProposalParseResult(null, ordered);
    }
}

public static class OutcomeProposalParser
{
    private static readonly string[] RootFields =
    [
        OutcomeProposalConstraint.SchemaVersionProperty,
        OutcomeProposalConstraint.ProposalProperty,
    ];

    private static readonly string[] ProposalFields =
    [
        OutcomeProposalConstraint.ProposedIntentProperty,
        OutcomeProposalConstraint.WorkStateProperty,
        OutcomeProposalConstraint.RequiredInterventionProperty,
        OutcomeProposalConstraint.BlockersProperty,
        OutcomeProposalConstraint.NextActionProperty,
        OutcomeProposalConstraint.EvidenceReferencesProperty,
        OutcomeProposalConstraint.InformationGapsProperty,
        OutcomeProposalConstraint.AuthorityRequestProperty,
    ];

    private static readonly string[] EvidenceFields =
    [
        OutcomeProposalConstraint.EvidenceSourceProperty,
        OutcomeProposalConstraint.EvidenceReferenceProperty,
    ];

    private static readonly string[] InformationGapFields =
    [
        OutcomeProposalConstraint.MissingEvidenceReferenceProperty,
        OutcomeProposalConstraint.MaterialityProperty,
        OutcomeProposalConstraint.MaterialityReasonProperty,
    ];

    private static readonly string[] AuthorityRequestFields =
    [
        OutcomeProposalConstraint.AuthorityDecisionProperty,
        OutcomeProposalConstraint.AuthorityKindProperty,
        OutcomeProposalConstraint.AuthorityReferenceProperty,
        OutcomeProposalConstraint.PositionLimitReasonProperty,
    ];

    public static OutcomeProposalParseResult Parse(
        string? output,
        OutcomeProposalEvidenceContext? evidenceContext = null)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return Failure(Error(
                OutcomeProposalParseDiagnosticContract.EmptyResponseCode,
                "$"));
        }

        using var document = ParseJson(output, out var parseError);
        if (parseError is not null)
        {
            return Failure(parseError);
        }

        var root = document!.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            return Failure(Error(
                OutcomeProposalParseDiagnosticContract.TopLevelObjectRequiredCode,
                "$"));
        }

        var errors = new List<OutcomeProposalParseError>();
        AddUnknownAndDuplicateFields(root, "$", RootFields, errors);
        var schemaVersionValid = ValidateSchemaVersion(root, errors);

        if (!root.TryGetProperty(OutcomeProposalConstraint.ProposalProperty, out var proposalElement))
        {
            errors.Add(Error(
                OutcomeProposalParseDiagnosticContract.RequiredFieldCode,
                OutcomeProposalConstraint.ProposalProperty));
            return OutcomeProposalParseResult.Failure(errors);
        }

        if (proposalElement.ValueKind is not JsonValueKind.Object)
        {
            errors.Add(Error(
                OutcomeProposalParseDiagnosticContract.InvalidFieldCode,
                OutcomeProposalConstraint.ProposalProperty));
            return OutcomeProposalParseResult.Failure(errors);
        }

        AddUnknownAndDuplicateFields(
            proposalElement,
            OutcomeProposalConstraint.ProposalProperty,
            ProposalFields,
            errors);

        var proposedIntent = ReadProposedIntent(proposalElement, errors);
        var workState = ReadWorkState(proposalElement, errors);
        var requiredIntervention = ReadRequiredIntervention(proposalElement, errors);
        var blockers = ReadBlockers(proposalElement, errors);
        var nextAction = ReadNextAction(proposalElement, errors);
        var evidenceReferences = ReadEvidenceReferences(
            proposalElement,
            evidenceContext,
            errors);
        var informationGaps = ReadInformationGaps(proposalElement, errors);
        var authorityRequest = ReadAuthorityRequest(proposalElement, errors);

        ValidateAuthorityRequestCombination(
            proposalElement,
            requiredIntervention,
            authorityRequest,
            errors);

        if (evidenceContext is not null &&
            proposedIntent is (OutcomeProposedIntent.ReportProgress or
                OutcomeProposedIntent.ReportDone) &&
            evidenceReferences is { IsEmpty: true })
        {
            errors.Add(Error(
                OutcomeProposalParseDiagnosticContract.InvalidFieldCode,
                ProposalPath(OutcomeProposalConstraint.EvidenceReferencesProperty)));
        }

        OutcomeProposal? proposal = null;
        if (schemaVersionValid &&
            proposedIntent is { } parsedIntent &&
            workState is { } parsedWorkState &&
            requiredIntervention is { } parsedIntervention &&
            blockers is { } parsedBlockers &&
            evidenceReferences is { } parsedEvidence &&
            informationGaps is { } parsedInformationGaps &&
            !HasFieldErrors(errors))
        {
            try
            {
                proposal = new OutcomeProposal(
                    parsedIntent,
                    parsedWorkState,
                    parsedIntervention,
                    parsedBlockers,
                    nextAction,
                    parsedEvidence,
                    parsedInformationGaps,
                    authorityRequest);
            }
            catch (ArgumentException)
            {
                errors.Add(Error(
                    OutcomeProposalParseDiagnosticContract.ContradictoryCombinationCode,
                    OutcomeProposalConstraint.ProposalProperty));
            }
        }

        return errors.Count == 0 && proposal is not null
            ? OutcomeProposalParseResult.Success(proposal)
            : OutcomeProposalParseResult.Failure(errors);
    }

    private static JsonDocument? ParseJson(
        string output,
        out OutcomeProposalParseError? error)
    {
        try
        {
            error = null;
            return JsonDocument.Parse(
                output,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
        }
        catch (JsonException)
        {
            error = Error(OutcomeProposalParseDiagnosticContract.InvalidJsonCode, "$");
            return null;
        }
    }

    private static bool ValidateSchemaVersion(
        JsonElement root,
        ICollection<OutcomeProposalParseError> errors)
    {
        if (!root.TryGetProperty(OutcomeProposalConstraint.SchemaVersionProperty, out var version))
        {
            errors.Add(Error(
                OutcomeProposalParseDiagnosticContract.RequiredFieldCode,
                OutcomeProposalConstraint.SchemaVersionProperty));
            return false;
        }

        if (version.ValueKind is not JsonValueKind.Number ||
            !version.TryGetInt32(out var parsed) ||
            parsed != OutcomeProposalConstraint.SchemaVersion)
        {
            errors.Add(Error(
                OutcomeProposalParseDiagnosticContract.InvalidSchemaVersionCode,
                OutcomeProposalConstraint.SchemaVersionProperty));
            return false;
        }

        return true;
    }

    private static OutcomeProposedIntent? ReadProposedIntent(
        JsonElement proposal,
        ICollection<OutcomeProposalParseError> errors)
    {
        var path = ProposalPath(OutcomeProposalConstraint.ProposedIntentProperty);
        if (!TryReadRequiredString(
            proposal,
            OutcomeProposalConstraint.ProposedIntentProperty,
            path,
            errors,
            out var value))
        {
            return null;
        }

        if (!OutcomeProposedIntentContract.TryParseWireValue(value, out var parsed))
        {
            errors.Add(Error(
                OutcomeProposalParseDiagnosticContract.InvalidVocabularyCode,
                path));
            return null;
        }

        return parsed;
    }

    private static OutcomeWorkState? ReadWorkState(
        JsonElement proposal,
        ICollection<OutcomeProposalParseError> errors)
    {
        var path = ProposalPath(OutcomeProposalConstraint.WorkStateProperty);
        if (!TryReadRequiredString(
            proposal,
            OutcomeProposalConstraint.WorkStateProperty,
            path,
            errors,
            out var value))
        {
            return null;
        }

        if (!OutcomeWorkStateContract.TryParseWireValue(value, out var parsed))
        {
            errors.Add(Error(
                OutcomeProposalParseDiagnosticContract.InvalidVocabularyCode,
                path));
            return null;
        }

        return parsed;
    }

    private static OutcomeRequiredIntervention? ReadRequiredIntervention(
        JsonElement proposal,
        ICollection<OutcomeProposalParseError> errors)
    {
        var path = ProposalPath(OutcomeProposalConstraint.RequiredInterventionProperty);
        if (!TryReadRequiredString(
            proposal,
            OutcomeProposalConstraint.RequiredInterventionProperty,
            path,
            errors,
            out var value))
        {
            return null;
        }

        if (!OutcomeRequiredInterventionContract.TryParseWireValue(value, out var parsed))
        {
            errors.Add(Error(
                OutcomeProposalParseDiagnosticContract.InvalidVocabularyCode,
                path));
            return null;
        }

        return parsed;
    }

    private static ImmutableArray<OutcomeBlocker>? ReadBlockers(
        JsonElement proposal,
        ICollection<OutcomeProposalParseError> errors)
    {
        var path = ProposalPath(OutcomeProposalConstraint.BlockersProperty);
        if (!proposal.TryGetProperty(OutcomeProposalConstraint.BlockersProperty, out var blockers))
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.RequiredFieldCode, path));
            return null;
        }

        if (blockers.ValueKind is not JsonValueKind.Array)
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.InvalidFieldCode, path));
            return null;
        }

        var builder = ImmutableArray.CreateBuilder<OutcomeBlocker>();
        foreach (var item in blockers.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.String ||
                !OutcomeBlockerContract.TryParseWireValue(item.GetString(), out var blocker))
            {
                errors.Add(Error(
                    OutcomeProposalParseDiagnosticContract.InvalidVocabularyCode,
                    $"{path}.item"));
                continue;
            }

            if (builder.Contains(blocker))
            {
                errors.Add(Error(OutcomeProposalParseDiagnosticContract.InvalidFieldCode, path));
                continue;
            }

            builder.Add(blocker);
        }

        return builder.ToImmutable();
    }

    private static string? ReadNextAction(
        JsonElement proposal,
        ICollection<OutcomeProposalParseError> errors)
    {
        var path = ProposalPath(OutcomeProposalConstraint.NextActionProperty);
        if (!proposal.TryGetProperty(OutcomeProposalConstraint.NextActionProperty, out var value))
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.RequiredFieldCode, path));
            return null;
        }

        if (value.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind is not JsonValueKind.String)
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.InvalidFieldCode, path));
            return null;
        }

        var normalized = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.InvalidFieldCode, path));
            return null;
        }

        return normalized;
    }

    private static ImmutableArray<OutcomeEvidenceReference>? ReadEvidenceReferences(
        JsonElement proposal,
        OutcomeProposalEvidenceContext? evidenceContext,
        ICollection<OutcomeProposalParseError> errors)
    {
        var path = ProposalPath(OutcomeProposalConstraint.EvidenceReferencesProperty);
        if (!proposal.TryGetProperty(
            OutcomeProposalConstraint.EvidenceReferencesProperty,
            out var evidence))
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.RequiredFieldCode, path));
            return null;
        }

        if (evidence.ValueKind is not JsonValueKind.Array)
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.InvalidFieldCode, path));
            return null;
        }

        var builder = ImmutableArray.CreateBuilder<OutcomeEvidenceReference>();
        foreach (var item in evidence.EnumerateArray())
        {
            var itemPath = $"{path}.item";
            if (item.ValueKind is not JsonValueKind.Object)
            {
                errors.Add(Error(
                    OutcomeProposalParseDiagnosticContract.InvalidFieldCode,
                    itemPath));
                continue;
            }

            AddUnknownAndDuplicateFields(item, itemPath, EvidenceFields, errors);
            var source = ReadEvidenceSource(item, errors);
            var reference = ReadEvidenceReference(item, errors);
            if (source is not { } parsedSource || reference is null)
            {
                continue;
            }

            var parsed = new OutcomeEvidenceReference(parsedSource, reference);
            if (evidenceContext is not null && !evidenceContext.Allows(parsed))
            {
                errors.Add(Error(
                    parsed.Source == OutcomeEvidenceSource.DirectiveInput
                        ? OutcomeProposalParseDiagnosticContract.InvalidFieldCode
                        : OutcomeProposalParseDiagnosticContract.InvalidVocabularyCode,
                    parsed.Source == OutcomeEvidenceSource.DirectiveInput
                        ? EvidencePath(OutcomeProposalConstraint.EvidenceReferenceProperty)
                        : EvidencePath(OutcomeProposalConstraint.EvidenceSourceProperty)));
                continue;
            }

            if (builder.Contains(parsed))
            {
                errors.Add(Error(OutcomeProposalParseDiagnosticContract.InvalidFieldCode, path));
                continue;
            }

            builder.Add(parsed);
        }

        return builder.ToImmutable();
    }

    private static OutcomeEvidenceSource? ReadEvidenceSource(
        JsonElement evidence,
        ICollection<OutcomeProposalParseError> errors)
    {
        var path = EvidencePath(OutcomeProposalConstraint.EvidenceSourceProperty);
        if (!TryReadRequiredString(
            evidence,
            OutcomeProposalConstraint.EvidenceSourceProperty,
            path,
            errors,
            out var value))
        {
            return null;
        }

        if (!OutcomeEvidenceSourceContract.TryParseWireValue(value, out var parsed))
        {
            errors.Add(Error(
                OutcomeProposalParseDiagnosticContract.InvalidVocabularyCode,
                path));
            return null;
        }

        return parsed;
    }

    private static string? ReadEvidenceReference(
        JsonElement evidence,
        ICollection<OutcomeProposalParseError> errors)
    {
        var path = EvidencePath(OutcomeProposalConstraint.EvidenceReferenceProperty);
        if (!TryReadRequiredString(
            evidence,
            OutcomeProposalConstraint.EvidenceReferenceProperty,
            path,
            errors,
            out var value))
        {
            return null;
        }

        try
        {
            return OutcomeContractGuards.RequireReference(value!, path);
        }
        catch (ArgumentException)
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.InvalidFieldCode, path));
            return null;
        }
    }

    private static ImmutableArray<OutcomeInformationGap>? ReadInformationGaps(
        JsonElement proposal,
        ICollection<OutcomeProposalParseError> errors)
    {
        var path = ProposalPath(OutcomeProposalConstraint.InformationGapsProperty);
        if (!proposal.TryGetProperty(
            OutcomeProposalConstraint.InformationGapsProperty,
            out var informationGaps))
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.RequiredFieldCode, path));
            return null;
        }

        if (informationGaps.ValueKind is not JsonValueKind.Array ||
            informationGaps.GetArrayLength() > OutcomeProposalConstraint.MaximumInformationGaps)
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.InvalidFieldCode, path));
            return null;
        }

        var builder = ImmutableArray.CreateBuilder<OutcomeInformationGap>();
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in informationGaps.EnumerateArray())
        {
            var itemPath = $"{path}.item";
            if (item.ValueKind is not JsonValueKind.Object)
            {
                errors.Add(Error(
                    OutcomeProposalParseDiagnosticContract.InvalidFieldCode,
                    itemPath));
                continue;
            }

            AddUnknownAndDuplicateFields(item, itemPath, InformationGapFields, errors);
            var missingEvidenceReference = ReadMissingEvidenceReference(item, errors);
            var materiality = ReadInformationGapMateriality(item, errors);
            var materialityReason = ReadInformationGapMaterialityReason(
                item,
                materiality,
                errors);

            if (missingEvidenceReference is null || materiality is null ||
                (materiality == OutcomeInformationGapMateriality.Material &&
                    materialityReason is null))
            {
                continue;
            }

            if (!references.Add(missingEvidenceReference))
            {
                errors.Add(Error(
                    OutcomeProposalParseDiagnosticContract.InvalidFieldCode,
                    path));
                continue;
            }

            builder.Add(new OutcomeInformationGap(
                missingEvidenceReference,
                materiality.Value,
                materialityReason));
        }

        return builder.ToImmutable();
    }

    private static string? ReadMissingEvidenceReference(
        JsonElement informationGap,
        ICollection<OutcomeProposalParseError> errors)
    {
        var path = InformationGapPath(
            OutcomeProposalConstraint.MissingEvidenceReferenceProperty);
        if (!TryReadRequiredString(
            informationGap,
            OutcomeProposalConstraint.MissingEvidenceReferenceProperty,
            path,
            errors,
            out var value))
        {
            return null;
        }

        try
        {
            return OutcomeContractGuards.RequireReference(value!, path);
        }
        catch (ArgumentException)
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.InvalidFieldCode, path));
            return null;
        }
    }

    private static OutcomeInformationGapMateriality? ReadInformationGapMateriality(
        JsonElement informationGap,
        ICollection<OutcomeProposalParseError> errors)
    {
        var path = InformationGapPath(OutcomeProposalConstraint.MaterialityProperty);
        if (!TryReadRequiredString(
            informationGap,
            OutcomeProposalConstraint.MaterialityProperty,
            path,
            errors,
            out var value))
        {
            return null;
        }

        if (!OutcomeInformationGapMaterialityContract.TryParseWireValue(value, out var parsed))
        {
            errors.Add(Error(
                OutcomeProposalParseDiagnosticContract.InvalidVocabularyCode,
                path));
            return null;
        }

        return parsed;
    }

    private static OutcomeInformationGapMaterialityReason?
        ReadInformationGapMaterialityReason(
            JsonElement informationGap,
            OutcomeInformationGapMateriality? materiality,
            ICollection<OutcomeProposalParseError> errors)
    {
        var path = InformationGapPath(OutcomeProposalConstraint.MaterialityReasonProperty);
        if (!informationGap.TryGetProperty(
            OutcomeProposalConstraint.MaterialityReasonProperty,
            out var reason))
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.RequiredFieldCode, path));
            return null;
        }

        if (materiality == OutcomeInformationGapMateriality.NonMaterial)
        {
            if (reason.ValueKind is not JsonValueKind.Null)
            {
                errors.Add(Error(
                    OutcomeProposalParseDiagnosticContract.ContradictoryCombinationCode,
                    $"{ProposalPath(OutcomeProposalConstraint.InformationGapsProperty)}.item"));
            }

            return null;
        }

        if (reason.ValueKind is not JsonValueKind.String)
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.InvalidFieldCode, path));
            return null;
        }

        if (!OutcomeInformationGapMaterialityReasonContract.TryParseWireValue(
            reason.GetString(),
            out var parsed))
        {
            errors.Add(Error(
                OutcomeProposalParseDiagnosticContract.InvalidVocabularyCode,
                path));
            return null;
        }

        return parsed;
    }

    private static OutcomeAuthorityRequest? ReadAuthorityRequest(
        JsonElement proposal,
        ICollection<OutcomeProposalParseError> errors)
    {
        var path = ProposalPath(OutcomeProposalConstraint.AuthorityRequestProperty);
        if (!proposal.TryGetProperty(
            OutcomeProposalConstraint.AuthorityRequestProperty,
            out var authorityRequest))
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.RequiredFieldCode, path));
            return null;
        }

        if (authorityRequest.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (authorityRequest.ValueKind is not JsonValueKind.Object)
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.InvalidFieldCode, path));
            return null;
        }

        AddUnknownAndDuplicateFields(
            authorityRequest,
            path,
            AuthorityRequestFields,
            errors);
        var decision = ReadRequiredText(
            authorityRequest,
            OutcomeProposalConstraint.AuthorityDecisionProperty,
            AuthorityRequestPath(OutcomeProposalConstraint.AuthorityDecisionProperty),
            errors);
        var authorityKind = ReadAuthorityKind(authorityRequest, errors);
        var authorityReference = ReadRequiredText(
            authorityRequest,
            OutcomeProposalConstraint.AuthorityReferenceProperty,
            AuthorityRequestPath(OutcomeProposalConstraint.AuthorityReferenceProperty),
            errors);
        var positionLimitReason = ReadRequiredText(
            authorityRequest,
            OutcomeProposalConstraint.PositionLimitReasonProperty,
            AuthorityRequestPath(OutcomeProposalConstraint.PositionLimitReasonProperty),
            errors);

        if (decision is null || authorityKind is null || authorityReference is null ||
            positionLimitReason is null)
        {
            return null;
        }

        try
        {
            return new OutcomeAuthorityRequest(
                decision,
                authorityKind.Value,
                authorityReference,
                positionLimitReason);
        }
        catch (ArgumentException)
        {
            errors.Add(Error(
                OutcomeProposalParseDiagnosticContract.InvalidFieldCode,
                AuthorityRequestPath(OutcomeProposalConstraint.AuthorityReferenceProperty)));
            return null;
        }
    }

    private static OutcomeAuthorityKind? ReadAuthorityKind(
        JsonElement authorityRequest,
        ICollection<OutcomeProposalParseError> errors)
    {
        var path = AuthorityRequestPath(OutcomeProposalConstraint.AuthorityKindProperty);
        if (!TryReadRequiredString(
            authorityRequest,
            OutcomeProposalConstraint.AuthorityKindProperty,
            path,
            errors,
            out var value))
        {
            return null;
        }

        if (!OutcomeAuthorityKindContract.TryParseWireValue(value, out var parsed))
        {
            errors.Add(Error(
                OutcomeProposalParseDiagnosticContract.InvalidVocabularyCode,
                path));
            return null;
        }

        return parsed;
    }

    private static string? ReadRequiredText(
        JsonElement parent,
        string propertyName,
        string path,
        ICollection<OutcomeProposalParseError> errors)
    {
        if (!TryReadRequiredString(parent, propertyName, path, errors, out var value))
        {
            return null;
        }

        try
        {
            return OutcomeContractGuards.RequireText(value!, path);
        }
        catch (ArgumentException)
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.InvalidFieldCode, path));
            return null;
        }
    }

    private static void ValidateAuthorityRequestCombination(
        JsonElement proposal,
        OutcomeRequiredIntervention? requiredIntervention,
        OutcomeAuthorityRequest? authorityRequest,
        ICollection<OutcomeProposalParseError> errors)
    {
        if (requiredIntervention is null ||
            !proposal.TryGetProperty(
                OutcomeProposalConstraint.AuthorityRequestProperty,
                out var authorityElement))
        {
            return;
        }

        var requiresAuthorityRequest = requiredIntervention is
            OutcomeRequiredIntervention.HumanApproval or
            OutcomeRequiredIntervention.SuperiorDecision or
            OutcomeRequiredIntervention.ExternalAction;
        if ((requiresAuthorityRequest &&
                authorityElement.ValueKind is JsonValueKind.Null) ||
            (!requiresAuthorityRequest &&
                authorityElement.ValueKind is JsonValueKind.Object &&
                authorityRequest is not null))
        {
            errors.Add(Error(
                OutcomeProposalParseDiagnosticContract.ContradictoryCombinationCode,
                ProposalPath(OutcomeProposalConstraint.AuthorityRequestProperty)));
        }
    }

    private static bool TryReadRequiredString(
        JsonElement parent,
        string propertyName,
        string path,
        ICollection<OutcomeProposalParseError> errors,
        out string? value)
    {
        if (!parent.TryGetProperty(propertyName, out var property))
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.RequiredFieldCode, path));
            value = null;
            return false;
        }

        if (property.ValueKind is not JsonValueKind.String)
        {
            errors.Add(Error(OutcomeProposalParseDiagnosticContract.InvalidFieldCode, path));
            value = null;
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static void AddUnknownAndDuplicateFields(
        JsonElement element,
        string path,
        IReadOnlyCollection<string> allowedFields,
        ICollection<OutcomeProposalParseError> errors)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!observed.Add(property.Name))
            {
                errors.Add(Error(
                    OutcomeProposalParseDiagnosticContract.DuplicateFieldCode,
                    path));
            }

            if (!allowedFields.Contains(property.Name, StringComparer.Ordinal))
            {
                errors.Add(Error(
                    OutcomeProposalParseDiagnosticContract.UnknownFieldCode,
                    path));
            }
        }
    }

    private static bool HasFieldErrors(IEnumerable<OutcomeProposalParseError> errors) =>
        errors.Any();

    private static string ProposalPath(string propertyName) =>
        $"{OutcomeProposalConstraint.ProposalProperty}.{propertyName}";

    private static string EvidencePath(string propertyName) =>
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.EvidenceReferencesProperty}.item.{propertyName}";

    private static string InformationGapPath(string propertyName) =>
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.InformationGapsProperty}.item.{propertyName}";

    private static string AuthorityRequestPath(string propertyName) =>
        $"{OutcomeProposalConstraint.ProposalProperty}.{OutcomeProposalConstraint.AuthorityRequestProperty}.{propertyName}";

    private static OutcomeProposalParseResult Failure(OutcomeProposalParseError error) =>
        OutcomeProposalParseResult.Failure([error]);

    private static OutcomeProposalParseError Error(string code, string path) => new(code, path);
}
