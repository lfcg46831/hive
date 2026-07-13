using System.Reflection;
using System.Text.Json;
using Hive.Domain.Evaluation;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Evaluation;
using Hive.Infrastructure.Evaluation.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class EvaluationResultProjectionTests
{
    [Fact]
    public void Generic_evaluation_schema_migrations_are_embedded_in_infrastructure()
    {
        var assembly = typeof(PostgreSqlEvaluationProjectionMigrator).Assembly;
        var evaluationSymbols = new[]
            {
                typeof(IEvaluationResultProjector).Assembly,
                assembly,
            }
            .Distinct()
            .SelectMany(item => item.GetTypes())
            .Where(type => type.Namespace?.Contains(".Evaluation", StringComparison.Ordinal) is true)
            .SelectMany(type => new[] { type.Name }.Concat(type
                .GetMembers(
                    BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Select(member => member.Name)))
            .Select(NormalizeArchitectureName)
            .ToArray();
        foreach (var forbidden in new[] { "bugtriage", "severity", "missinginformation" })
        {
            Assert.DoesNotContain(
                evaluationSymbols,
                symbol => symbol.Contains(forbidden, StringComparison.Ordinal));
        }

        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(
                ".Evaluation.PostgreSql.Migrations.",
                StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(resources, name => name.EndsWith(
            ".001_create_result_projections.sql",
            StringComparison.Ordinal));
        Assert.Contains(resources, name => name.EndsWith(
            ".002_replace_legacy_result_projection.sql",
            StringComparison.Ordinal));
        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            Assert.DoesNotContain("bug-triage", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bug_triage", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("severity", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("missing_information", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Bootstrap_activates_projector_only_for_an_enabled_evaluation_profile()
    {
        var normalBuilder = Host.CreateApplicationBuilder();
        normalBuilder.AddHiveBootstrap();
        await using var normalServices = normalBuilder.Services.BuildServiceProvider();

        var evaluationBuilder = Host.CreateApplicationBuilder();
        evaluationBuilder.Configuration.AddInMemoryCollection(ProfileConfiguration(RubricFile));
        evaluationBuilder.AddHiveBootstrap();
        await using var evaluationServices = evaluationBuilder.Services.BuildServiceProvider();

        Assert.IsType<NoopEvaluationResultProjector>(
            normalServices.GetRequiredService<IEvaluationResultProjector>());
        Assert.IsType<PostgreSqlEvaluationResultProjector>(
            evaluationServices.GetRequiredService<IEvaluationResultProjector>());
    }

    [Fact]
    public void Bootstrap_fails_fast_when_an_enabled_profile_rubric_is_unavailable()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            $"missing-evaluation-rubric-{Guid.NewGuid():N}.json");
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(ProfileConfiguration(missing));
        builder.AddHiveBootstrap();
        using var services = builder.Services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IEvaluationResultProjector>());

        Assert.Contains("profile configuration is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_projects_declared_dimensions_and_ignores_undeclared_properties()
    {
        var projection = EvaluationProjectionParser.Parse(
            "Summary.\n" +
            "hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"high\"],\"missing-information\":[\"run-log\",\"environment\",\"run-log\"],\"not-declared\":[\"private\"]}}",
            "report",
            Rubric());
        var dimensions = projection.Dimensions.ToDictionary(
            dimension => dimension.DimensionId,
            StringComparer.Ordinal);

        Assert.Equal(EvaluationProjectionParser.ContractVersion, projection.ContractVersion);
        Assert.Equal(EvaluationDimensionStatus.Valid, dimensions["severity"].Status);
        Assert.Equal(["high"], dimensions["severity"].Labels);
        Assert.Equal(EvaluationDimensionStatus.Valid, dimensions["missing-information"].Status);
        Assert.Equal(["environment", "run-log"], dimensions["missing-information"].Labels);
        Assert.Equal(EvaluationDimensionStatus.Valid, dimensions["decision"].Status);
        Assert.Equal(["report"], dimensions["decision"].Labels);
        Assert.DoesNotContain("not-declared", dimensions.Keys);
    }

    [Fact]
    public void Parser_validates_each_dimension_independently_without_persisting_rejected_values()
    {
        var projection = EvaluationProjectionParser.Parse(
            "hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"high\",\"critical\"],\"missing-information\":[\"correlation-metadata\"]}}",
            "escalation",
            Rubric());
        var dimensions = projection.Dimensions.ToDictionary(
            dimension => dimension.DimensionId,
            StringComparer.Ordinal);

        Assert.Equal(EvaluationDimensionStatus.Invalid, dimensions["severity"].Status);
        Assert.Empty(dimensions["severity"].Labels);
        Assert.Equal(EvaluationDimensionStatus.Valid, dimensions["missing-information"].Status);
        Assert.Equal(["correlation-metadata"], dimensions["missing-information"].Labels);
        Assert.Equal(["escalation"], dimensions["decision"].Labels);
    }

    [Fact]
    public void Parser_distinguishes_missing_malformed_duplicated_and_partial_envelopes()
    {
        var rubric = Rubric();
        var missing = ById(EvaluationProjectionParser.Parse("No envelope", "report", rubric));
        var malformed = ById(EvaluationProjectionParser.Parse(
            "hive-evaluation-v1:{not-json}",
            "report",
            rubric));
        var duplicated = ById(EvaluationProjectionParser.Parse(
            "hive-evaluation-v1:{\"dimensions\":{}}\n" +
            "hive-evaluation-v1:{\"dimensions\":{}}",
            "report",
            rubric));
        var partialDuplicate = ById(EvaluationProjectionParser.Parse(
            "hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"low\"],\"severity\":[\"high\"],\"missing-information\":[]}}",
            "report",
            rubric));

        Assert.All(
            missing.Values.Where(dimension => dimension.DimensionId != "decision"),
            dimension => Assert.Equal(EvaluationDimensionStatus.Missing, dimension.Status));
        Assert.All(
            malformed.Values.Where(dimension => dimension.DimensionId != "decision"),
            dimension => Assert.Equal(EvaluationDimensionStatus.Invalid, dimension.Status));
        Assert.All(
            duplicated.Values.Where(dimension => dimension.DimensionId != "decision"),
            dimension => Assert.Equal(EvaluationDimensionStatus.Invalid, dimension.Status));
        Assert.Equal(EvaluationDimensionStatus.Invalid, partialDuplicate["severity"].Status);
        Assert.Equal(EvaluationDimensionStatus.Valid, partialDuplicate["missing-information"].Status);
        Assert.All(
            [missing, malformed, duplicated, partialDuplicate],
            result => Assert.Equal(EvaluationDimensionStatus.Valid, result["decision"].Status));
    }

    [Fact]
    public void Parser_treats_rubric_labels_as_opaque_exact_tokens()
    {
        using var rubric = TemporaryRubric.WithReplacement(
            "\"correlation-metadata\"",
            "\"correlation_metadata\"");
        var contract = EvaluationRubricContract.Load(rubric.Path, 1);
        var exact = ById(EvaluationProjectionParser.Parse(
            "hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"high\"],\"missing-information\":[\"correlation_metadata\"]}}",
            "report",
            contract));
        var alias = ById(EvaluationProjectionParser.Parse(
            "hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"high\"],\"missing-information\":[\"correlation-metadata\"]}}",
            "report",
            contract));

        Assert.Equal(EvaluationDimensionStatus.Valid, exact["missing-information"].Status);
        Assert.Equal(["correlation_metadata"], exact["missing-information"].Labels);
        Assert.Equal(EvaluationDimensionStatus.Invalid, alias["missing-information"].Status);
        Assert.Empty(alias["missing-information"].Labels);
    }

    [Fact]
    public void Parser_projects_the_second_role_fixture_without_role_specific_branches()
    {
        var rubric = EvaluationRubricContract.Load(FollowUpRubricFile, 1);
        var dimensions = ById(EvaluationProjectionParser.Parse(
            "Coordination summary.\n" +
            "hive-evaluation-v1:{\"dimensions\":{\"response-window\":[\"same-day\"],\"pending-signals\":[\"owner_ack\",\"schedule_slot\",\"owner_ack\"]}}",
            "escalation",
            rubric));

        Assert.Equal(EvaluationDimensionStatus.Valid, dimensions["response-window"].Status);
        Assert.Equal(["same-day"], dimensions["response-window"].Labels);
        Assert.Equal(EvaluationDimensionStatus.Valid, dimensions["pending-signals"].Status);
        Assert.Equal(["owner_ack", "schedule_slot"], dimensions["pending-signals"].Labels);
        Assert.Equal(EvaluationDimensionStatus.Valid, dimensions["coordination-route"].Status);
        Assert.Equal(["request-support"], dimensions["coordination-route"].Labels);
        Assert.DoesNotContain("severity", dimensions.Keys);
        Assert.DoesNotContain("missing-information", dimensions.Keys);
    }

    [Fact]
    public async Task Ai_actor_projects_canonical_result_before_audit_redaction()
    {
        const string privateText = "Customer-private diagnostic detail.";
        var projector = new RecordingEvaluationResultProjector();
        var scenario = AiDirectiveIntegrationScenario.Create(options => options.Text = $$$"""
            {
              "schema_version": 1,
              "intent": "Report",
              "acting_under": "bug.triage",
              "report": {
                "kind": "Done",
                "body": "{{{privateText}}}\nhive-evaluation-v1:{\"dimensions\":{\"severity\":[\"high\"],\"missing-information\":[\"run-log\"]}}"
              },
              "escalation": null,
              "directive": null
            }
            """);
        await using var fixture = await AiDirectiveIntegrationFixture.StartAsync(
            scenario,
            evaluationResultProjector: projector);

        var run = await fixture.ProcessDirectiveAsync();

        var projected = Assert.IsType<Report>(Assert.Single(projector.Messages));
        Assert.Contains(privateText, projected.Body, StringComparison.Ordinal);
        Assert.Contains(EvaluationInstruction.EnvelopeMarker, projected.Body, StringComparison.Ordinal);
        Assert.Contains(
            run.Audit.Redactions,
            redaction => redaction.Path == "resultMessage.report.body");
        Assert.DoesNotContain(
            privateText,
            JsonSerializer.Serialize(run.Audit),
            StringComparison.Ordinal);
        Assert.Equal(fixture.Directive.DirectiveId, Assert.Single(projector.DirectiveIds));
    }

    private static Dictionary<string, EvaluationDimensionProjection> ById(
        EvaluationProjection projection) => projection.Dimensions.ToDictionary(
            dimension => dimension.DimensionId,
            StringComparer.Ordinal);

    private static string NormalizeArchitectureName(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static EvaluationRubricContract Rubric() =>
        EvaluationRubricContract.Load(RubricFile, 1);

    private static Dictionary<string, string?> ProfileConfiguration(string rubricPath) =>
        new(StringComparer.Ordinal)
        {
            ["Hive:Evaluation:Profiles:bug-triage:Enabled"] = "true",
            ["Hive:Evaluation:Profiles:bug-triage:OrganizationId"] = "acme-delivery",
            ["Hive:Evaluation:Profiles:bug-triage:PositionId"] = "bug-triage",
            ["Hive:Evaluation:Profiles:bug-triage:RubricPath"] = rubricPath,
            ["Hive:Evaluation:Profiles:bug-triage:RubricVersion"] = "1",
            ["ConnectionStrings:PostgreSql"] = "Host=localhost;Database=hive",
        };

    private sealed class RecordingEvaluationResultProjector : IEvaluationResultProjector
    {
        public List<DirectiveId> DirectiveIds { get; } = [];

        public List<OrgMessage> Messages { get; } = [];

        public ValueTask ProjectAsync(
            DirectiveId directiveId,
            OrgMessage resultMessage,
            CancellationToken cancellationToken = default)
        {
            DirectiveIds.Add(directiveId);
            Messages.Add(resultMessage);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryRubric : IDisposable
    {
        private TemporaryRubric(string path) => Path = path;

        public string Path { get; }

        public static TemporaryRubric WithReplacement(string original, string replacement)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"hive-generic-evaluation-rubric-{Guid.NewGuid():N}.json");
            File.WriteAllText(
                path,
                File.ReadAllText(RubricFile).Replace(original, replacement, StringComparison.Ordinal));
            return new TemporaryRubric(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
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

    private static string FollowUpRubricFile => Path.Combine(
        RepositoryRoot,
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "evaluation",
        "follow-up-coordination-rubric.v1.json");

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
}
