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

        if (corpus.Cases is null || corpus.Cases.Count is < 30 or > 50)
        {
            throw new InvalidDataException("Evaluation corpus must contain between 30 and 50 cases.");
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

            item.HumanReference.Validate(item.CaseId);
        }

        return corpus with
        {
            Cases = corpus.Cases.OrderBy(item => item.CaseId, StringComparer.Ordinal).ToArray(),
        };
    }

    [GeneratedRegex("^triage-[0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CaseIdPattern();
}

public sealed record EvaluationCase(
    [property: JsonPropertyName("case_id")] string CaseId,
    [property: JsonPropertyName("source_category")] string SourceCategory,
    [property: JsonPropertyName("context")] string Context,
    [property: JsonPropertyName("human_reference")] EvaluationHumanReference HumanReference);

public sealed record EvaluationHumanReference(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("missing_information")] IReadOnlyList<string> MissingInformation,
    [property: JsonPropertyName("expected_routing")] string ExpectedRouting,
    [property: JsonPropertyName("expected_decision")] string ExpectedDecision)
{
    private static readonly HashSet<string> Severities =
        ["low", "medium", "high", "critical"];
    private static readonly HashSet<string> Decisions =
        ["report", "escalation"];

    public void Validate(string caseId)
    {
        if (!Severities.Contains(Severity)
            || MissingInformation is null
            || MissingInformation.Any(string.IsNullOrWhiteSpace)
            || string.IsNullOrWhiteSpace(ExpectedRouting)
            || !Decisions.Contains(ExpectedDecision))
        {
            throw new InvalidDataException($"Evaluation case '{caseId}' has an invalid human_reference.");
        }
    }
}
