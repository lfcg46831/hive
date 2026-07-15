using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Hive.Infrastructure.Evaluation;

/// <summary>
/// Closed, versioned vocabulary for evaluation envelope projection failures
/// (US-F0-13-T12c). Codes are stable tokens in the T10b pattern: they carry no model text,
/// rejected values, or payload fragments, and the affected <c>dimension_id</c> travels as
/// data next to the code, never as a type or branch.
/// </summary>
internal static class EvaluationEnvelopeDiagnostics
{
    public const int ContractVersion = 1;

    public const string CardinalityViolation = "cardinality-violation";
    public const string EnvelopeDuplicated = "envelope-duplicated";
    public const string EnvelopeMissing = "envelope-missing";
    public const string PayloadNotJson = "payload-not-json";
    public const string UnexpectedShape = "unexpected-shape";
    public const string UnknownLabel = "unknown-label";

    public static ImmutableArray<string> Codes { get; } =
    [
        CardinalityViolation,
        EnvelopeDuplicated,
        EnvelopeMissing,
        PayloadNotJson,
        UnexpectedShape,
        UnknownLabel,
    ];

    public static string Require(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (!Codes.Contains(code, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Evaluation envelope diagnostic code is outside the closed vocabulary.",
                nameof(code));
        }

        return code;
    }
}

internal static class EvaluationProjectionParser
{
    public const int ContractVersion = 1;

    public static EvaluationProjection Parse(
        string? content,
        string resultMessageKind,
        EvaluationRubricContract rubric)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultMessageKind);
        ArgumentNullException.ThrowIfNull(rubric);

        var envelopeDimensions = ParseEnvelope(content, rubric);
        var dimensions = rubric.Dimensions
            .OrderBy(dimension => dimension.Id, StringComparer.Ordinal)
            .Select(dimension => dimension.Source switch
            {
                "evaluation-envelope" => envelopeDimensions[dimension.Id],
                "result-message-kind" => ProjectResultMessageKind(
                    dimension,
                    resultMessageKind),
                _ => throw new InvalidOperationException(
                    $"Unsupported evaluation dimension source '{dimension.Source}'."),
            })
            .ToImmutableArray();

        return new EvaluationProjection(ContractVersion, rubric.RubricVersion, dimensions);
    }

    private static IReadOnlyDictionary<string, EvaluationDimensionProjection> ParseEnvelope(
        string? content,
        EvaluationRubricContract rubric)
    {
        var dimensions = rubric.Dimensions
            .Where(dimension => dimension.Source == "evaluation-envelope")
            .ToArray();
        var source = content ?? string.Empty;
        var markerIndexes = new List<int>();
        var searchFrom = 0;
        while (searchFrom < source.Length)
        {
            var index = source.IndexOf(
                EvaluationInstruction.EnvelopeMarker,
                searchFrom,
                StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            markerIndexes.Add(index);
            searchFrom = index + EvaluationInstruction.EnvelopeMarker.Length;
        }

        if (markerIndexes.Count == 0)
        {
            return WithStatus(
                dimensions,
                EvaluationDimensionStatus.Missing,
                EvaluationEnvelopeDiagnostics.EnvelopeMissing);
        }

        if (markerIndexes.Count != 1)
        {
            return WithStatus(
                dimensions,
                EvaluationDimensionStatus.Invalid,
                EvaluationEnvelopeDiagnostics.EnvelopeDuplicated);
        }

        // US-F0-13-T12b: the payload is delimited to the first complete JSON object after
        // the marker; text following that object is tolerated instead of poisoning the
        // parse. A missing, truncated, or non-object payload remains invalid, and label,
        // cardinality, and shape validation below are unchanged — no semantic
        // normalization, no defaults.
        var payloadRegion = source[
            (markerIndexes[0] + EvaluationInstruction.EnvelopeMarker.Length)..].TrimStart();
        if (payloadRegion.Length == 0
            || !TryExtractLeadingJsonObject(payloadRegion, out var payload))
        {
            return WithStatus(
                dimensions,
                EvaluationDimensionStatus.Invalid,
                EvaluationEnvelopeDiagnostics.PayloadNotJson);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var rootProperties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().ToArray()
                : [];
            var dimensionContainers = rootProperties
                .Where(property => property.NameEquals("dimensions"))
                .ToArray();
            if (root.ValueKind != JsonValueKind.Object
                || rootProperties.Any(property => !property.NameEquals("dimensions"))
                || dimensionContainers.Length != 1
                || dimensionContainers[0].Value.ValueKind != JsonValueKind.Object)
            {
                return WithStatus(
                    dimensions,
                    EvaluationDimensionStatus.Invalid,
                    EvaluationEnvelopeDiagnostics.UnexpectedShape);
            }

            var projected = new Dictionary<string, EvaluationDimensionProjection>(
                StringComparer.Ordinal);
            var properties = dimensionContainers[0].Value
                .EnumerateObject()
                .ToArray();
            foreach (var dimension in dimensions)
            {
                var matches = properties
                    .Where(property => property.NameEquals(dimension.Id))
                    .ToArray();
                projected.Add(
                    dimension.Id,
                    matches.Length switch
                    {
                        0 => EvaluationDimensionProjection.Missing(
                            dimension.Id,
                            EvaluationEnvelopeDiagnostics.EnvelopeMissing),
                        1 => ProjectEnvelopeDimension(dimension, matches[0].Value),
                        _ => EvaluationDimensionProjection.Invalid(
                            dimension.Id,
                            EvaluationEnvelopeDiagnostics.UnexpectedShape),
                    });
            }

            return projected;
        }
        catch (JsonException)
        {
            return WithStatus(
                dimensions,
                EvaluationDimensionStatus.Invalid,
                EvaluationEnvelopeDiagnostics.PayloadNotJson);
        }
    }

    private static bool TryExtractLeadingJsonObject(string payloadRegion, out string payload)
    {
        payload = string.Empty;
        var bytes = Encoding.UTF8.GetBytes(payloadRegion);
        var reader = new Utf8JsonReader(bytes);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            reader.Skip();
        }
        catch (JsonException)
        {
            return false;
        }

        payload = Encoding.UTF8.GetString(bytes, 0, (int)reader.BytesConsumed);
        return true;
    }

    private static EvaluationDimensionProjection ProjectEnvelopeDimension(
        EvaluationDimensionContract dimension,
        JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return EvaluationDimensionProjection.Invalid(
                dimension.Id,
                EvaluationEnvelopeDiagnostics.UnexpectedShape);
        }

        var labels = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var label = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : null;
            if (label is null)
            {
                return EvaluationDimensionProjection.Invalid(
                    dimension.Id,
                    EvaluationEnvelopeDiagnostics.UnexpectedShape);
            }

            if (!dimension.Labels.Contains(label, StringComparer.Ordinal))
            {
                return EvaluationDimensionProjection.Invalid(
                    dimension.Id,
                    EvaluationEnvelopeDiagnostics.UnknownLabel);
            }

            labels.Add(label);
        }

        if (dimension.ValueKind == "single-label" && labels.Count != 1)
        {
            return EvaluationDimensionProjection.Invalid(
                dimension.Id,
                EvaluationEnvelopeDiagnostics.CardinalityViolation);
        }

        var canonicalLabels = dimension.ValueKind == "label-set"
            ? labels.Distinct(StringComparer.Ordinal)
                .OrderBy(label => label, StringComparer.Ordinal)
                .ToImmutableArray()
            : labels.ToImmutableArray();
        return EvaluationDimensionProjection.Valid(dimension.Id, canonicalLabels);
    }

    private static EvaluationDimensionProjection ProjectResultMessageKind(
        EvaluationDimensionContract dimension,
        string resultMessageKind) =>
        dimension.SourceMapping.TryGetValue(resultMessageKind, out var label)
            ? EvaluationDimensionProjection.Valid(dimension.Id, [label])
            : EvaluationDimensionProjection.Invalid(
                dimension.Id,
                EvaluationEnvelopeDiagnostics.UnexpectedShape);

    private static IReadOnlyDictionary<string, EvaluationDimensionProjection> WithStatus(
        IEnumerable<EvaluationDimensionContract> dimensions,
        EvaluationDimensionStatus status,
        string diagnosticCode) => dimensions.ToDictionary(
            dimension => dimension.Id,
            dimension => status == EvaluationDimensionStatus.Missing
                ? EvaluationDimensionProjection.Missing(dimension.Id, diagnosticCode)
                : EvaluationDimensionProjection.Invalid(dimension.Id, diagnosticCode),
            StringComparer.Ordinal);
}

internal enum EvaluationDimensionStatus
{
    Valid,
    Missing,
    Invalid,
}

internal sealed record EvaluationProjection(
    int ContractVersion,
    int RubricVersion,
    ImmutableArray<EvaluationDimensionProjection> Dimensions);

internal sealed record EvaluationDimensionProjection
{
    private EvaluationDimensionProjection(
        string dimensionId,
        EvaluationDimensionStatus status,
        ImmutableArray<string> labels,
        string? diagnosticCode)
    {
        DimensionId = dimensionId;
        Status = status;
        Labels = labels;
        DiagnosticCode = diagnosticCode;
    }

    public string DimensionId { get; }

    public EvaluationDimensionStatus Status { get; }

    public ImmutableArray<string> Labels { get; }

    /// <summary>
    /// Closed-vocabulary reason for a missing/invalid projection (US-F0-13-T12c);
    /// always null for valid dimensions, never free text.
    /// </summary>
    public string? DiagnosticCode { get; }

    public static EvaluationDimensionProjection Valid(
        string dimensionId,
        ImmutableArray<string> labels) =>
        new(dimensionId, EvaluationDimensionStatus.Valid, labels, diagnosticCode: null);

    public static EvaluationDimensionProjection Missing(
        string dimensionId,
        string diagnosticCode) =>
        new(
            dimensionId,
            EvaluationDimensionStatus.Missing,
            [],
            EvaluationEnvelopeDiagnostics.Require(diagnosticCode));

    public static EvaluationDimensionProjection Invalid(
        string dimensionId,
        string diagnosticCode) =>
        new(
            dimensionId,
            EvaluationDimensionStatus.Invalid,
            [],
            EvaluationEnvelopeDiagnostics.Require(diagnosticCode));
}
