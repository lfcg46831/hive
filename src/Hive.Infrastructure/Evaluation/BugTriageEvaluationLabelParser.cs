using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hive.Infrastructure.Evaluation;

public static partial class BugTriageEvaluationLabelParser
{
    public const int ProjectionVersion = 1;
    public const string Marker = "hive-evaluation-v1:";

    public static EvaluationLabels? Parse(
        string content,
        BugTriageEvaluationVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);

        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var payloads = content
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(Marker, StringComparison.Ordinal))
            .Select(line => line[Marker.Length..].Trim())
            .ToArray();

        if (payloads.Length != 1 || payloads[0].Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloads[0]);
            var root = document.RootElement;
            var properties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().ToArray()
                : [];
            if (root.ValueKind != JsonValueKind.Object
                || properties.Any(property =>
                    property.Name is not "severity" and not "missing_information")
                || properties.GroupBy(property => property.Name, StringComparer.Ordinal)
                    .Any(group => group.Count() > 1))
            {
                return null;
            }

            return new EvaluationLabels(
                ReadSeverity(root, vocabulary),
                ReadMissingInformation(root, vocabulary));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadSeverity(
        JsonElement root,
        BugTriageEvaluationVocabulary vocabulary)
    {
        if (!root.TryGetProperty("severity", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var label = value.GetString();
        return label is not null && vocabulary.ContainsSeverity(label)
            ? label
            : null;
    }

    private static IReadOnlyList<string>? ReadMissingInformation(
        JsonElement root,
        BugTriageEvaluationVocabulary vocabulary)
    {
        if (!root.TryGetProperty("missing_information", out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var labels = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || item.GetString() is not { } label
                || !CanonicalLabel().IsMatch(label)
                || !vocabulary.ContainsMissingInformation(label))
            {
                return null;
            }

            labels.Add(label);
        }

        return labels
            .Distinct(StringComparer.Ordinal)
            .OrderBy(label => label, StringComparer.Ordinal)
            .ToArray();
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalLabel();
}

public sealed record EvaluationLabels(
    string? Severity,
    IReadOnlyList<string>? MissingInformation);
