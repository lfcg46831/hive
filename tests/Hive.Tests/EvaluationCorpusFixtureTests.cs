using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hive.Tests;

public sealed partial class EvaluationCorpusFixtureTests
{
    private static readonly HashSet<string> AllowedSeverities =
        new(StringComparer.Ordinal) { "low", "medium", "high", "critical" };

    private static readonly HashSet<string> AllowedDecisions =
        new(StringComparer.Ordinal) { "report", "escalation" };

    private static readonly HashSet<string> AllowedSourceCategories =
        new(StringComparer.Ordinal)
        {
            "accessibility-audit",
            "api-consumer-note",
            "chat-transcript",
            "compliance-review",
            "crash-report",
            "customer-report",
            "data-import-report",
            "database-alert",
            "dependency-scan",
            "engineering-chat",
            "finance-review",
            "incident-note",
            "internal-report",
            "localization-review",
            "operations-handoff",
            "partner-report",
            "payment-incident",
            "performance-investigation",
            "product-feedback",
            "release-note",
            "security-alert",
            "security-review",
            "support-summary",
        };

    [Fact]
    public void Corpus_is_a_versioned_example_fixture_with_anonymization_guarantees()
    {
        using var document = LoadCorpus();
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("corpus_version").GetInt32());
        Assert.Equal("evaluation-example", root.GetProperty("fixture_kind").GetString());
        Assert.Equal(
            "anonymized-historical-reconstructions",
            root.GetProperty("provenance").GetString());

        var anonymization = root.GetProperty("anonymization");
        Assert.True(anonymization.GetProperty("direct_identifiers_removed").GetBoolean());
        Assert.True(anonymization.GetProperty("organizations_generalized").GetBoolean());
        Assert.True(anonymization.GetProperty("timestamps_shifted").GetBoolean());
        Assert.True(anonymization.GetProperty("technical_values_generalized").GetBoolean());
    }

    [Fact]
    public void Corpus_has_30_to_50_unique_well_formed_cases()
    {
        using var document = LoadCorpus();
        using var rubricDocument = JsonDocument.Parse(File.ReadAllText(RubricFile));
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var allowedMissingInformation = rubricDocument.RootElement
            .GetProperty("dimensions")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "missing-information")
            .GetProperty("allowed_labels")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.InRange(cases.Length, 30, 50);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in cases)
        {
            var id = RequiredString(item, "case_id");
            Assert.Matches(CanonicalCaseId(), id);
            Assert.True(ids.Add(id), $"Duplicate evaluation case id '{id}'.");

            var category = RequiredString(item, "source_category");
            Assert.Contains(category, AllowedSourceCategories);

            var context = RequiredString(item, "context");
            Assert.True(context.Length >= 120, $"Case '{id}' has too little evaluation context.");

            var reference = item.GetProperty("human_reference");
            Assert.Contains(RequiredString(reference, "severity"), AllowedSeverities);
            Assert.Contains(RequiredString(reference, "expected_decision"), AllowedDecisions);
            Assert.Matches(CanonicalSlug(), RequiredString(reference, "expected_routing"));

            var missingInformation = reference
                .GetProperty("missing_information")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();

            Assert.All(missingInformation, value =>
            {
                Assert.False(string.IsNullOrWhiteSpace(value));
                Assert.Matches(CanonicalSlug(), value!);
                Assert.Contains(value!, allowedMissingInformation);
            });
            Assert.Equal(
                missingInformation.Length,
                missingInformation.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void Corpus_covers_triage_boundaries_without_leaking_references_into_context()
    {
        using var document = LoadCorpus();
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();

        var severities = cases
            .Select(item => RequiredString(item.GetProperty("human_reference"), "severity"))
            .ToHashSet(StringComparer.Ordinal);
        var decisions = cases
            .Select(item => RequiredString(item.GetProperty("human_reference"), "expected_decision"))
            .ToHashSet(StringComparer.Ordinal);
        var categories = cases
            .Select(item => RequiredString(item, "source_category"))
            .ToHashSet(StringComparer.Ordinal);
        var routes = cases
            .Select(item => RequiredString(item.GetProperty("human_reference"), "expected_routing"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(severities.SetEquals(AllowedSeverities));
        Assert.True(decisions.SetEquals(AllowedDecisions));
        Assert.True(categories.Count >= 10);
        Assert.True(routes.Count >= 10);
        Assert.Contains(
            cases,
            item => item.GetProperty("human_reference")
                .GetProperty("missing_information")
                .GetArrayLength() == 0);
        Assert.Contains(
            cases,
            item => item.GetProperty("human_reference")
                .GetProperty("missing_information")
                .GetArrayLength() > 0);

        Assert.All(cases, item =>
        {
            var context = RequiredString(item, "context");
            Assert.DoesNotContain("human_reference", context, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("expected_decision", context, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("expected_routing", context, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("missing_information", context, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Corpus_contexts_do_not_contain_direct_identifier_patterns()
    {
        using var document = LoadCorpus();
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();

        Assert.All(cases, item =>
        {
            var id = RequiredString(item, "case_id");
            var context = RequiredString(item, "context");

            Assert.False(EmailAddress().IsMatch(context), $"Case '{id}' contains an email address.");
            Assert.False(WebAddress().IsMatch(context), $"Case '{id}' contains a web address.");
            Assert.False(IpAddress().IsMatch(context), $"Case '{id}' contains an IP address.");
            Assert.False(GuidValue().IsMatch(context), $"Case '{id}' contains a UUID.");
            Assert.False(TicketReference().IsMatch(context), $"Case '{id}' contains a ticket reference.");
        });
    }

    private static JsonDocument LoadCorpus() =>
        JsonDocument.Parse(File.ReadAllText(CorpusFile));

    private static string RequiredString(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName).GetString();
        Assert.False(string.IsNullOrWhiteSpace(value));
        return value!;
    }

    private static string CorpusFile => Path.Combine(
        RepositoryRoot,
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "evaluation",
        "bug-triage-corpus.v1.json");

    private static string RubricFile => Path.Combine(
        RepositoryRoot,
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "evaluation",
        "bug-triage-rubric.v1.json");

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);

            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate the Hive repository root.");
        }
    }

    [GeneratedRegex("^triage-[0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalCaseId();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalSlug();

    [GeneratedRegex(@"\b[^\s@]+@[^\s@]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"(?:https?://|www\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebAddress();

    [GeneratedRegex(@"\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IpAddress();

    [GeneratedRegex(
        @"\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GuidValue();

    [GeneratedRegex(@"\b[A-Z]{2,10}-[0-9]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex TicketReference();
}
