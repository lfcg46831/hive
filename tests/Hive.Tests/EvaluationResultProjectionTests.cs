using System.Text.Json;
using Hive.Domain.Evaluation;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Evaluation;
using Hive.Infrastructure.Evaluation.PostgreSql;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class EvaluationResultProjectionTests
{
    [Fact]
    public void Evaluation_schema_migration_is_embedded_in_infrastructure()
    {
        Assert.Contains(
            typeof(PostgreSqlEvaluationProjectionMigrator).Assembly.GetManifestResourceNames(),
            name => name.EndsWith(
                ".Evaluation.PostgreSql.Migrations.001_create_result_projections.sql",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bootstrap_activates_projector_only_when_evaluation_profile_is_enabled()
    {
        var normalBuilder = Host.CreateApplicationBuilder();
        normalBuilder.AddHiveBootstrap();
        await using var normalServices = normalBuilder.Services.BuildServiceProvider();

        var evaluationBuilder = Host.CreateApplicationBuilder();
        evaluationBuilder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Hive:EvaluationProjection:Enabled"] = "true",
                ["Hive:EvaluationProjection:RubricPath"] = RubricFile,
                ["ConnectionStrings:PostgreSql"] = "Host=localhost;Database=hive",
            });
        evaluationBuilder.AddHiveBootstrap();
        await using var evaluationServices = evaluationBuilder.Services.BuildServiceProvider();

        Assert.IsType<NoopEvaluationResultProjector>(
            normalServices.GetRequiredService<IEvaluationResultProjector>());
        Assert.IsType<PostgreSqlEvaluationResultProjector>(
            evaluationServices.GetRequiredService<IEvaluationResultProjector>());
    }

    [Fact]
    public void Bootstrap_fails_fast_when_enabled_rubric_is_unavailable()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Hive:EvaluationProjection:Enabled"] = "true",
                ["Hive:EvaluationProjection:RubricPath"] = Path.Combine(
                    Path.GetTempPath(),
                    $"missing-evaluation-rubric-{Guid.NewGuid():N}.json"),
                ["ConnectionStrings:PostgreSql"] = "Host=localhost;Database=hive",
            });
        builder.AddHiveBootstrap();
        using var services = builder.Services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IEvaluationResultProjector>());

        Assert.Contains("rubric configuration is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_accepts_empty_and_multiple_missing_information_labels_canonically()
    {
        var vocabulary = BugTriageEvaluationVocabulary.Load(RubricFile);
        var empty = BugTriageEvaluationLabelParser.Parse(
            "hive-evaluation-v1:{\"severity\":\"low\",\"missing_information\":[]}",
            vocabulary);
        var multiple = BugTriageEvaluationLabelParser.Parse(
            "Summary.\n" +
            "hive-evaluation-v1:{\"severity\":\"critical\",\"missing_information\":[\"run-log\",\"environment\",\"run-log\"]}",
            vocabulary);

        Assert.NotNull(empty);
        Assert.Equal("low", empty.Severity);
        Assert.Empty(empty.MissingInformation!);
        Assert.NotNull(multiple);
        Assert.Equal("critical", multiple.Severity);
        Assert.Equal(["environment", "run-log"], multiple.MissingInformation);
    }

    [Fact]
    public void Parser_preserves_partial_validity_but_rejects_ambiguous_blocks()
    {
        var vocabulary = BugTriageEvaluationVocabulary.Load(RubricFile);
        var partial = BugTriageEvaluationLabelParser.Parse(
            "hive-evaluation-v1:{\"severity\":\"\",\"missing_information\":[\"correlation-metadata\"]}",
            vocabulary);
        var ambiguous = BugTriageEvaluationLabelParser.Parse(
            "hive-evaluation-v1:{\"severity\":\"low\",\"missing_information\":[]}\n" +
            "hive-evaluation-v1:{\"severity\":\"high\",\"missing_information\":[]}",
            vocabulary);
        var duplicateField = BugTriageEvaluationLabelParser.Parse(
            "hive-evaluation-v1:{\"severity\":\"low\",\"severity\":\"high\",\"missing_information\":[]}",
            vocabulary);

        Assert.NotNull(partial);
        Assert.Null(partial.Severity);
        Assert.Equal(["correlation-metadata"], partial.MissingInformation);
        Assert.Null(ambiguous);
        Assert.Null(duplicateField);
    }

    [Fact]
    public void Parser_rejects_snake_case_unknown_and_partially_invalid_lists_per_dimension()
    {
        var vocabulary = BugTriageEvaluationVocabulary.Load(RubricFile);
        var snakeCase = BugTriageEvaluationLabelParser.Parse(
            "hive-evaluation-v1:{\"severity\":\"high\",\"missing_information\":[\"correlation_metadata\"]}",
            vocabulary);
        var unknown = BugTriageEvaluationLabelParser.Parse(
            "hive-evaluation-v1:{\"severity\":\"medium\",\"missing_information\":[\"unknown-label\"]}",
            vocabulary);
        var partiallyInvalid = BugTriageEvaluationLabelParser.Parse(
            "hive-evaluation-v1:{\"severity\":\"critical\",\"missing_information\":[\"environment\",\"unknown-label\"]}",
            vocabulary);

        Assert.Equal("high", snakeCase!.Severity);
        Assert.Null(snakeCase.MissingInformation);
        Assert.Equal("medium", unknown!.Severity);
        Assert.Null(unknown.MissingInformation);
        Assert.Equal("critical", partiallyInvalid!.Severity);
        Assert.Null(partiallyInvalid.MissingInformation);
    }

    [Fact]
    public async Task Ai_actor_projects_canonical_result_before_audit_redaction()
    {
        const string privateText = "Customer-private diagnostic detail.";
        var projector = new RecordingEvaluationResultProjector();
        var scenario = AiDirectiveIntegrationScenario.Create(options => options.Text = $$"""
            {
              "schema_version": 1,
              "intent": "Report",
              "acting_under": "bug.triage",
              "report": {
                "kind": "Done",
                "body": "{{privateText}}\nhive-evaluation-v1:{\"severity\":\"high\",\"missing_information\":[\"logs\"]}"
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
        Assert.Contains(BugTriageEvaluationLabelParser.Marker, projected.Body, StringComparison.Ordinal);
        Assert.Contains(
            run.Audit.Redactions,
            redaction => redaction.Path == "resultMessage.report.body");
        Assert.DoesNotContain(
            privateText,
            JsonSerializer.Serialize(run.Audit),
            StringComparison.Ordinal);
        Assert.Equal(fixture.Directive.DirectiveId, Assert.Single(projector.DirectiveIds));
    }

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
}
