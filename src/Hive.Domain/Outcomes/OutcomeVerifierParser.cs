using System.Collections.Immutable;
using System.Text.Json;

namespace Hive.Domain.Outcomes;

public sealed record OutcomeVerifierParseError
{
    public OutcomeVerifierParseError(string code, string path)
    {
        Code = OutcomeVerifierParseDiagnosticContract.RequireCode(code);
        Path = OutcomeVerifierParseDiagnosticContract.RequirePath(path);
    }

    public string Code { get; }

    public string Path { get; }
}

public static class OutcomeVerifierParseDiagnosticContract
{
    public const int Version = 1;
    public const string EmptyResponseCode = "empty-response";
    public const string InvalidJsonCode = "invalid-json";
    public const string TopLevelObjectRequiredCode = "top-level-object-required";
    public const string UnknownFieldCode = "unknown-field";
    public const string DuplicateFieldCode = "duplicate-field";
    public const string RequiredFieldCode = "required-field";
    public const string InvalidSchemaVersionCode = "invalid-schema-version";
    public const string InvalidVocabularyCode = "invalid-vocabulary";

    private static readonly ImmutableHashSet<string> Codes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        EmptyResponseCode,
        InvalidJsonCode,
        TopLevelObjectRequiredCode,
        UnknownFieldCode,
        DuplicateFieldCode,
        RequiredFieldCode,
        InvalidSchemaVersionCode,
        InvalidVocabularyCode);

    private static readonly ImmutableHashSet<string> Paths = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "$",
        OutcomeVerifierConstraint.SchemaVersionProperty,
        OutcomeVerifierConstraint.ClassificationProperty);

    public static string RequireCode(string value)
    {
        if (!Codes.Contains(value))
        {
            throw new ArgumentException("Unknown outcome verifier parse diagnostic code.", nameof(value));
        }

        return value;
    }

    public static string RequirePath(string value)
    {
        if (!Paths.Contains(value))
        {
            throw new ArgumentException("Unknown outcome verifier parse diagnostic path.", nameof(value));
        }

        return value;
    }
}

public sealed record OutcomeVerifierParseResult
{
    private OutcomeVerifierParseResult(
        OutcomeVerifierClassification? classification,
        IEnumerable<OutcomeVerifierParseError>? errors)
    {
        Classification = classification;
        Errors = errors is null
            ? []
            : errors
                .Distinct()
                .OrderBy(error => error.Path, StringComparer.Ordinal)
                .ThenBy(error => error.Code, StringComparer.Ordinal)
                .ToImmutableArray();
    }

    public bool IsSuccess => Classification.HasValue;

    public OutcomeVerifierClassification? Classification { get; }

    public ImmutableArray<OutcomeVerifierParseError> Errors { get; }

    public static OutcomeVerifierParseResult Success(
        OutcomeVerifierClassification classification) =>
        new(
            OutcomeVerifierClassificationContract.RequireDefined(
                classification,
                nameof(classification)),
            errors: null);

    public static OutcomeVerifierParseResult Failure(
        IEnumerable<OutcomeVerifierParseError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var snapshot = errors.ToImmutableArray();
        if (snapshot.IsEmpty || snapshot.Any(error => error is null))
        {
            throw new ArgumentException(
                "A failed verifier parse requires at least one diagnostic.",
                nameof(errors));
        }

        return new OutcomeVerifierParseResult(classification: null, snapshot);
    }
}

public static class OutcomeVerifierParser
{
    private static readonly ImmutableHashSet<string> AllowedProperties =
        OutcomeVerifierConstraint.RequiredFields.ToImmutableHashSet(StringComparer.Ordinal);

    public static OutcomeVerifierParseResult Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return Failure(
                OutcomeVerifierParseDiagnosticContract.EmptyResponseCode,
                "$");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return Failure(
                OutcomeVerifierParseDiagnosticContract.InvalidJsonCode,
                "$");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure(
                    OutcomeVerifierParseDiagnosticContract.TopLevelObjectRequiredCode,
                    "$");
            }

            var errors = new List<OutcomeVerifierParseError>();
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!AllowedProperties.Contains(property.Name))
                {
                    errors.Add(new OutcomeVerifierParseError(
                        OutcomeVerifierParseDiagnosticContract.UnknownFieldCode,
                        "$"));
                }

                if (!propertyNames.Add(property.Name) && AllowedProperties.Contains(property.Name))
                {
                    errors.Add(new OutcomeVerifierParseError(
                        OutcomeVerifierParseDiagnosticContract.DuplicateFieldCode,
                        property.Name));
                }
            }

            ValidateSchemaVersion(root, errors);
            var classification = ValidateClassification(root, errors);

            return errors.Count == 0
                ? OutcomeVerifierParseResult.Success(classification!.Value)
                : OutcomeVerifierParseResult.Failure(errors);
        }
    }

    private static void ValidateSchemaVersion(
        JsonElement root,
        ICollection<OutcomeVerifierParseError> errors)
    {
        if (!root.TryGetProperty(OutcomeVerifierConstraint.SchemaVersionProperty, out var version))
        {
            errors.Add(new OutcomeVerifierParseError(
                OutcomeVerifierParseDiagnosticContract.RequiredFieldCode,
                OutcomeVerifierConstraint.SchemaVersionProperty));
            return;
        }

        if (version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var parsed) ||
            parsed != OutcomeVerifierConstraint.SchemaVersion)
        {
            errors.Add(new OutcomeVerifierParseError(
                OutcomeVerifierParseDiagnosticContract.InvalidSchemaVersionCode,
                OutcomeVerifierConstraint.SchemaVersionProperty));
        }
    }

    private static OutcomeVerifierClassification? ValidateClassification(
        JsonElement root,
        ICollection<OutcomeVerifierParseError> errors)
    {
        if (!root.TryGetProperty(
            OutcomeVerifierConstraint.ClassificationProperty,
            out var classification))
        {
            errors.Add(new OutcomeVerifierParseError(
                OutcomeVerifierParseDiagnosticContract.RequiredFieldCode,
                OutcomeVerifierConstraint.ClassificationProperty));
            return null;
        }

        if (classification.ValueKind != JsonValueKind.String ||
            !OutcomeVerifierClassificationContract.TryParseWireValue(
                classification.GetString(),
                out var parsed))
        {
            errors.Add(new OutcomeVerifierParseError(
                OutcomeVerifierParseDiagnosticContract.InvalidVocabularyCode,
                OutcomeVerifierConstraint.ClassificationProperty));
            return null;
        }

        return parsed;
    }

    private static OutcomeVerifierParseResult Failure(string code, string path) =>
        OutcomeVerifierParseResult.Failure([new OutcomeVerifierParseError(code, path)]);
}
