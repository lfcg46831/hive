using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hive.Infrastructure.Evaluation;

public sealed partial class BugTriageEvaluationVocabulary
{
    private readonly HashSet<string> _severities;
    private readonly HashSet<string> _missingInformationLabels;

    private BugTriageEvaluationVocabulary(
        int rubricVersion,
        int corpusVersion,
        string[] severities,
        string[] missingInformationLabels)
    {
        RubricVersion = rubricVersion;
        CorpusVersion = corpusVersion;
        Severities = Array.AsReadOnly(severities);
        MissingInformationLabels = Array.AsReadOnly(missingInformationLabels);
        _severities = severities.ToHashSet(StringComparer.Ordinal);
        _missingInformationLabels = missingInformationLabels.ToHashSet(StringComparer.Ordinal);
    }

    public int RubricVersion { get; }

    public int CorpusVersion { get; }

    public IReadOnlyList<string> Severities { get; }

    public IReadOnlyList<string> MissingInformationLabels { get; }

    public bool ContainsSeverity(string label) => _severities.Contains(label);

    public bool ContainsMissingInformation(string label) =>
        _missingInformationLabels.Contains(label);

    public static BugTriageEvaluationVocabulary Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Evaluation rubric path is required.", nameof(path));
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var dimensions = root.GetProperty("dimensions")
                .EnumerateArray()
                .ToArray();
            var severity = RequiredDimension(dimensions, "severity");
            var missingInformation = RequiredDimension(dimensions, "missing-information");
            var severities = RequiredLabels(severity, "ordered_labels", requireLexicalOrder: false);
            var missingInformationLabels = RequiredLabels(
                missingInformation,
                "allowed_labels",
                requireLexicalOrder: true);

            var rubricVersion = root.GetProperty("rubric_version").GetInt32();
            var corpusVersion = root.GetProperty("applies_to")
                .GetProperty("corpus_version")
                .GetInt32();
            if (root.GetProperty("fixture_kind").GetString() != "evaluation-example"
                || rubricVersion != 1
                || corpusVersion != 1
                || severity.GetProperty("scorer").GetString() != "ordinal-distance"
                || missingInformation.GetProperty("scorer").GetString() != "set-f1"
                || missingInformation.GetProperty("label_matching").GetString()
                    != "canonical-slug-exact")
            {
                throw new InvalidDataException(
                    "Evaluation rubric has an unsupported vocabulary contract.");
            }

            return new BugTriageEvaluationVocabulary(
                rubricVersion,
                corpusVersion,
                severities,
                missingInformationLabels);
        }
        catch (Exception exception)
            when (exception is JsonException
                or KeyNotFoundException
                or InvalidOperationException
                or FormatException)
        {
            throw new InvalidDataException(
                "Evaluation rubric vocabulary is malformed.",
                exception);
        }
    }

    private static JsonElement RequiredDimension(JsonElement[] dimensions, string id)
    {
        var matches = dimensions
            .Where(item => item.GetProperty("id").GetString() == id)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"Evaluation rubric must contain exactly one '{id}' dimension.");
    }

    private static string[] RequiredLabels(
        JsonElement dimension,
        string property,
        bool requireLexicalOrder)
    {
        var labels = dimension.GetProperty(property)
            .EnumerateArray()
            .Select(item => item.GetString()
                ?? throw new InvalidDataException(
                    $"Evaluation rubric property '{property}' contains a null label."))
            .ToArray();
        if (labels.Length == 0
            || labels.Any(label => !CanonicalLabel().IsMatch(label))
            || labels.Distinct(StringComparer.Ordinal).Count() != labels.Length
            || (requireLexicalOrder
                && !labels.SequenceEqual(
                    labels.OrderBy(label => label, StringComparer.Ordinal),
                    StringComparer.Ordinal)))
        {
            throw new InvalidDataException(
                $"Evaluation rubric property '{property}' is not a closed canonical vocabulary.");
        }

        return labels;
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalLabel();
}
