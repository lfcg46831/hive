using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Hive.DemoClient.Evaluation;

public sealed partial record EvaluationCorpus(
    [property: JsonPropertyName("corpus_version")] int CorpusVersion,
    [property: JsonPropertyName("fixture_kind")] string FixtureKind,
    [property: JsonPropertyName("cases")] IReadOnlyList<EvaluationCase> Cases)
{
    public static EvaluationCorpus Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Corpus path is required.", nameof(path));
        }

        using var stream = File.OpenRead(path);
        var corpus = JsonSerializer.Deserialize<EvaluationCorpus>(stream)
            ?? throw new InvalidDataException("Evaluation corpus is empty.");

        if (corpus.CorpusVersion != 1)
        {
            throw new InvalidDataException($"Unsupported corpus version '{corpus.CorpusVersion}'.");
        }

        if (!string.Equals(corpus.FixtureKind, "evaluation-example", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Evaluation corpus has an unsupported fixture kind.");
        }

        if (corpus.Cases is null || corpus.Cases.Count == 0)
        {
            throw new InvalidDataException("Evaluation corpus must contain at least one case.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in corpus.Cases)
        {
            if (item is null
                || string.IsNullOrWhiteSpace(item.CaseId)
                || !CaseIdPattern().IsMatch(item.CaseId)
                || string.IsNullOrWhiteSpace(item.SourceCategory)
                || string.IsNullOrWhiteSpace(item.Context)
                || item.HumanReference is null)
            {
                throw new InvalidDataException("Every evaluation case requires canonical metadata, context, and human_reference.");
            }

            if (!ids.Add(item.CaseId))
            {
                throw new InvalidDataException($"Duplicate evaluation case id '{item.CaseId}'.");
            }

            ValidateReference(item.CaseId, item.HumanReference);
            ValidateMetadata(item.CaseId, item.AnalysisMetadata);
        }

        return corpus with
        {
            Cases = corpus.Cases.OrderBy(item => item.CaseId, StringComparer.Ordinal).ToArray(),
        };
    }

    private static void ValidateReference(
        string caseId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> reference)
    {
        if (reference.Count == 0
            || reference.Any(dimension =>
                string.IsNullOrWhiteSpace(dimension.Key)
                || dimension.Value is null
                || dimension.Value.Any(string.IsNullOrWhiteSpace)))
        {
            throw new InvalidDataException(
                $"Evaluation case '{caseId}' has an invalid human_reference dimension map.");
        }
    }

    private static void ValidateMetadata(
        string caseId,
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is not null
            && metadata.Any(item =>
                string.IsNullOrWhiteSpace(item.Key)
                || string.IsNullOrWhiteSpace(item.Value)))
        {
            throw new InvalidDataException(
                $"Evaluation case '{caseId}' has invalid analysis_metadata.");
        }
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CaseIdPattern();
}

public sealed record EvaluationCase(
    [property: JsonPropertyName("case_id")] string CaseId,
    [property: JsonPropertyName("source_category")] string SourceCategory,
    [property: JsonPropertyName("context")] string Context,
    [property: JsonPropertyName("human_reference")]
    IReadOnlyDictionary<string, IReadOnlyList<string>> HumanReference,
    [property: JsonPropertyName("analysis_metadata")]
    IReadOnlyDictionary<string, string>? AnalysisMetadata = null);
