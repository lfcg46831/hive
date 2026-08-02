using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Evaluation.Tooling.Evaluation;

namespace Hive.Evaluation.Tooling.Tests;

public sealed class EvaluationRubricTests
{
    private readonly EvaluationRubric _rubric = EvaluationRubric.Load(RubricFile);

    [Fact]
    public void Scores_registered_algorithms_and_aggregates_without_rounding()
    {
        var exact = _rubric.Score(
            Reference("high", ["environment", "run-log"], "report"),
            Prediction("high", ["run-log", "environment", "run-log"], "report"));
        var partial = _rubric.Score(
            Reference("high", ["environment", "run-log"], "report"),
            Prediction("medium", ["run-log"], "escalation"));

        Assert.Equal("scored", exact.Status);
        Assert.Equal(1d, exact.CaseScore);
        Assert.Equal(0.5d, Dimension(partial, "severity").Score);
        Assert.Equal(2d / 3d, Dimension(partial, "missing-information").Score);
        Assert.Equal(0d, Dimension(partial, "decision").Score);
        Assert.Equal((0.35d * 0.5d) + (0.35d * (2d / 3d)), partial.CaseScore);
        Assert.Equal(
            (exact.CaseScore + partial.CaseScore) / 2d,
            _rubric.ScoreCorpus([exact, partial]));
        Assert.Equal(
            ["decision", "missing-information", "severity"],
            partial.Dimensions.Select(item => item.DimensionId));
    }

    [Fact]
    public void Missing_and_invalid_projections_use_generic_codes_and_zero_only_affected_dimensions()
    {
        var missing = _rubric.Score(
            Reference("medium", [], "report"),
            new EvaluationPrediction(
                1,
                1,
                [
                    Valid("decision", "report"),
                    Missing("missing-information"),
                    Missing("severity"),
                ]));
        var invalidVersion = _rubric.Score(
            Reference("medium", [], "report"),
            Prediction("medium", [], "report") with { ProjectionVersion = 2 });

        Assert.Equal("failed", missing.Status);
        Assert.Equal(["projection-missing"], missing.FailureCodes);
        Assert.Equal(EvaluationDimensionStatuses.Missing, Dimension(missing, "severity").Status);
        Assert.Equal(EvaluationDimensionStatuses.Missing, Dimension(missing, "missing-information").Status);
        Assert.Equal(1d, Dimension(missing, "decision").Score);
        Assert.Equal(0.30d, missing.CaseScore);
        Assert.Equal("failed", invalidVersion.Status);
        Assert.Contains("projection-invalid", invalidVersion.FailureCodes);
        Assert.Contains("projection-version-unsupported", invalidVersion.FailureCodes);
        Assert.All(invalidVersion.Dimensions, item =>
        {
            Assert.Equal(EvaluationDimensionStatuses.Invalid, item.Status);
            Assert.Empty(item.Labels);
            Assert.Equal(0d, item.Score);
        });
    }

    [Theory]
    [MemberData(nameof(CanonicalDiagnosticCases))]
    public void Canonical_diagnostics_survive_normalization_scoring_and_json_round_trip(
        string body,
        string expectedStatus,
        string expectedCode)
    {
        var content = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["Body"] = body,
        });

        var projected = Assert.IsType<EvaluationPrediction>(
            _rubric.ProjectResult("Report", content));
        var normalized = Assert.IsType<EvaluationPrediction>(
            _rubric.NormalizePrediction(projected));
        var scoring = _rubric.Score(
            Reference("high", [], "report"),
            normalized);
        var roundTrip = JsonSerializer.Deserialize<EvaluationPrediction>(
            JsonSerializer.Serialize(normalized));
        var renormalized = Assert.IsType<EvaluationPrediction>(
            _rubric.NormalizePrediction(roundTrip));

        var dimension = renormalized.Dimensions.Single(item => item.DimensionId == "severity");
        Assert.Equal(expectedStatus, dimension.Status);
        Assert.Empty(dimension.Labels);
        Assert.Equal(expectedCode, dimension.DiagnosticCode);
        Assert.Equal(expectedStatus, Dimension(scoring, "severity").Status);
        Assert.Equal(0d, Dimension(scoring, "severity").Score);
    }

    [Fact]
    public void Unknown_incompatible_or_duplicate_diagnostics_fail_closed_without_values()
    {
        var malformed = new EvaluationDimensionPrediction[]
        {
            new("severity", EvaluationDimensionStatuses.Invalid, ["rejected"], "unknown-label"),
            new("severity", EvaluationDimensionStatuses.Missing, [], "unknown-label"),
            new("severity", EvaluationDimensionStatuses.Valid, ["high"], "unexpected-shape"),
            new("severity", EvaluationDimensionStatuses.Invalid, [], "provider-secret"),
        };

        foreach (var item in malformed)
        {
            var prediction = Prediction("high", [], "report") with
            {
                Dimensions =
                [
                    .. Prediction("high", [], "report").Dimensions
                        .Where(dimension => dimension.DimensionId != "severity"),
                    item,
                ],
            };

            var normalized = Assert.IsType<EvaluationPrediction>(
                _rubric.NormalizePrediction(prediction));
            var severity = normalized.Dimensions.Single(
                dimension => dimension.DimensionId == "severity");

            Assert.Equal(EvaluationDimensionStatuses.Invalid, severity.Status);
            Assert.Empty(severity.Labels);
            Assert.Null(severity.DiagnosticCode);
        }

        var duplicated = Prediction("high", [], "report") with
        {
            Dimensions =
            [
                .. Prediction("high", [], "report").Dimensions,
                new("severity", EvaluationDimensionStatuses.Missing, [], "envelope-missing"),
            ],
        };
        var normalizedDuplicate = Assert.IsType<EvaluationPrediction>(
            _rubric.NormalizePrediction(duplicated));
        var duplicatedSeverity = normalizedDuplicate.Dimensions.Single(
            dimension => dimension.DimensionId == "severity");

        Assert.Equal(EvaluationDimensionStatuses.Invalid, duplicatedSeverity.Status);
        Assert.Empty(duplicatedSeverity.Labels);
        Assert.Null(duplicatedSeverity.DiagnosticCode);
    }

    [Fact]
    public void Historical_missing_and_invalid_dimensions_without_diagnostics_remain_compatible()
    {
        var historical = new EvaluationPrediction(
            1,
            1,
            [
                Valid("decision", "report"),
                new("missing-information", EvaluationDimensionStatuses.Invalid, []),
                Missing("severity"),
            ]);

        var normalized = Assert.IsType<EvaluationPrediction>(
            _rubric.NormalizePrediction(historical));

        Assert.Equal(
            EvaluationDimensionStatuses.Invalid,
            normalized.Dimensions.Single(item => item.DimensionId == "missing-information").Status);
        Assert.Equal(
            EvaluationDimensionStatuses.Missing,
            normalized.Dimensions.Single(item => item.DimensionId == "severity").Status);
        Assert.All(normalized.Dimensions, item => Assert.Null(item.DiagnosticCode));
    }

    [Theory]
    [InlineData("correlation_metadata")]
    [InlineData("unknown-label")]
    public void Undeclared_prediction_label_marks_only_its_dimension_invalid(
        string invalidLabel)
    {
        var scoring = _rubric.Score(
            Reference("high", ["environment"], "report"),
            new EvaluationPrediction(
                1,
                1,
                [
                    Valid("severity", "high"),
                    new EvaluationDimensionPrediction(
                        "missing-information",
                        EvaluationDimensionStatuses.Valid,
                        ["environment", invalidLabel]),
                    Valid("decision", "report"),
                ]));

        Assert.Equal("failed", scoring.Status);
        Assert.Equal(["projection-invalid"], scoring.FailureCodes);
        Assert.Equal(1d, Dimension(scoring, "severity").Score);
        Assert.Equal(EvaluationDimensionStatuses.Invalid, Dimension(scoring, "missing-information").Status);
        Assert.Empty(Dimension(scoring, "missing-information").Labels);
        Assert.Equal(1d, Dimension(scoring, "decision").Score);
        Assert.Equal(0.65d, scoring.CaseScore, precision: 10);
    }

    [Fact]
    public void Corpus_validation_rejects_reference_labels_outside_the_rubric()
    {
        var corpus = new EvaluationCorpus(
            1,
            "evaluation-example",
            [
                new EvaluationCase(
                    "example-999",
                    "test",
                    "A sufficiently descriptive test context for direct rubric validation.",
                    Reference("high", ["correlation_metadata"], "report")),
            ]);

        var exception = Assert.Throws<InvalidDataException>(() => _rubric.ValidateCorpus(corpus));

        Assert.Contains("example-999", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregates_any_declared_dimension_count_from_generic_descriptors()
    {
        using var rubricFile = TemporaryRubric.WithAdditionalDimension();
        var rubric = EvaluationRubric.Load(rubricFile.Path);
        var reference = new Dictionary<string, IReadOnlyList<string>>(Reference(
            "medium",
            [],
            "report"), StringComparer.Ordinal)
        {
            ["follow-up-quality"] = ["complete"],
        };
        var prediction = Prediction("medium", [], "report") with
        {
            Dimensions =
            [
                .. Prediction("medium", [], "report").Dimensions,
                Valid("follow-up-quality", "partial"),
            ],
        };

        var scoring = rubric.Score(reference, prediction);

        Assert.Equal(4, rubric.Dimensions.Count);
        Assert.Equal(4, scoring.Dimensions.Count);
        Assert.Equal(
            ["decision", "follow-up-quality", "missing-information", "severity"],
            scoring.Dimensions.Select(item => item.DimensionId));
        Assert.Equal(0.75d, scoring.CaseScore);
    }

    [Fact]
    public void Closed_registry_rejects_an_unsupported_scorer_version()
    {
        using var rubricFile = TemporaryRubric.WithScorerVersion(2);

        var exception = Assert.Throws<InvalidDataException>(
            () => EvaluationRubric.Load(rubricFile.Path));

        Assert.Contains("unsupported scorer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compiled_evaluation_contracts_expose_only_generic_dimension_members()
    {
        var contractTypes = new[]
        {
            typeof(EvaluationCase),
            typeof(EvaluationPrediction),
            typeof(EvaluationDimensionPrediction),
            typeof(EvaluationCaseScoring),
            typeof(EvaluationDimensionScoring),
            typeof(EvaluationDimensionDescriptor),
        };
        var memberNames = contractTypes
            .SelectMany(type => type.GetProperties())
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("Severity", memberNames);
        Assert.DoesNotContain("MissingInformation", memberNames);
        Assert.DoesNotContain("ExpectedDecision", memberNames);
        Assert.Contains("Dimensions", memberNames);
        Assert.Contains("DimensionId", memberNames);

        var evaluationSymbols = typeof(EvaluationRubric).Assembly
            .GetTypes()
            .Where(type => type.Namespace == "Hive.Evaluation.Tooling.Evaluation")
            .SelectMany(type => new[] { type.Name }.Concat(type
                .GetMembers(
                    BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Select(member => member.Name)))
            .Select(value => string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant())
            .ToArray();
        foreach (var forbidden in new[] { "bugtriage", "severity", "missinginformation" })
        {
            Assert.DoesNotContain(
                evaluationSymbols,
                symbol => symbol.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    private static EvaluationDimensionScoring Dimension(
        EvaluationCaseScoring scoring,
        string id) => scoring.Dimensions.Single(item => item.DimensionId == id);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Reference(
        string severity,
        IReadOnlyList<string> missingInformation,
        string decision) => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["severity"] = [severity],
            ["missing-information"] = missingInformation,
            ["decision"] = [decision],
        };

    private static EvaluationPrediction Prediction(
        string severity,
        IReadOnlyList<string> missingInformation,
        string decision) => new(
        1,
        1,
        [
            Valid("severity", severity),
            new EvaluationDimensionPrediction(
                "missing-information",
                EvaluationDimensionStatuses.Valid,
                missingInformation),
            Valid("decision", decision),
        ]);

    private static EvaluationDimensionPrediction Valid(string id, string label) =>
        new(id, EvaluationDimensionStatuses.Valid, [label]);

    private static EvaluationDimensionPrediction Missing(string id) =>
        new(id, EvaluationDimensionStatuses.Missing, []);

    public static IEnumerable<object[]> CanonicalDiagnosticCases()
    {
        yield return
        [
            "No evaluation envelope was emitted.",
            EvaluationDimensionStatuses.Missing,
            "envelope-missing",
        ];
        yield return
        [
            "hive-evaluation-v1:{} hive-evaluation-v1:{}",
            EvaluationDimensionStatuses.Invalid,
            "envelope-duplicated",
        ];
        yield return
        [
            "hive-evaluation-v1:not-json",
            EvaluationDimensionStatuses.Invalid,
            "payload-not-json",
        ];
        yield return
        [
            "hive-evaluation-v1:{\"dimensions\":[]}",
            EvaluationDimensionStatuses.Invalid,
            "unexpected-shape",
        ];
        yield return
        [
            "hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"rejected\"],\"missing-information\":[]}}",
            EvaluationDimensionStatuses.Invalid,
            "unknown-label",
        ];
        yield return
        [
            "hive-evaluation-v1:{\"dimensions\":{\"severity\":[],\"missing-information\":[]}}",
            EvaluationDimensionStatuses.Invalid,
            "cardinality-violation",
        ];
    }

    private sealed class TemporaryRubric : IDisposable
    {
        private TemporaryRubric(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryRubric WithAdditionalDimension()
        {
            var root = JsonNode.Parse(File.ReadAllText(RubricFile))!.AsObject();
            var dimensions = root["dimensions"]!.AsArray();
            foreach (var dimension in dimensions)
            {
                dimension!["weight"] = 0.25m;
            }

            dimensions.Add(new JsonObject
            {
                ["id"] = "follow-up-quality",
                ["weight"] = 0.25m,
                ["source"] = "evaluation-envelope",
                ["value_kind"] = "single-label",
                ["scorer"] = "exact-match",
                ["scorer_version"] = 1,
                ["allowed_labels"] = new JsonArray("partial", "complete"),
                ["match_score"] = 1d,
                ["mismatch_score"] = 0d,
            });
            return Write(root.ToJsonString());
        }

        public static TemporaryRubric WithScorerVersion(int version)
        {
            var root = JsonNode.Parse(File.ReadAllText(RubricFile))!.AsObject();
            root["dimensions"]!.AsArray()[0]!["scorer_version"] = version;
            return Write(root.ToJsonString());
        }

        private static TemporaryRubric Write(string content)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"hive-evaluation-rubric-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, content);
            return new TemporaryRubric(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

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

            throw new InvalidOperationException("Could not locate repository root.");
        }
    }
}
