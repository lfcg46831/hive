using Hive.Domain.Governance;
using Hive.Infrastructure.Governance;

namespace Hive.Tests;

public sealed class ActionDomainCatalogParserTests
{
    private const string FilePath = "config/organizations/acme-delivery/action-domains.yaml";

    private const string FullCatalog = """
        version: 1
        defaults:
          unmatched_action: escalate
        domains:
          - key: delivery.bug-triage
            description: Classificar, priorizar e atribuir bugs internamente
            gate: decide
          - key: delivery.release-prod
            description: Promover codigo ou configuracao para producao
            gate: human-approval
            match:
              - action: tool
                tool: http
                method: POST
                url: "https://ci.acme.pt/pipelines/*/promote"
                amount_eur: 100.50
          - key: comms.external-official
            description: Comunicacao oficial para fora da organizacao
            gate: escalate
            match:
              - action: organizational-message
                message_type: report
                recipient: external
        """;

    private static ActionDomainCatalogParser Parser => new();

    [Fact]
    public void Full_catalog_parses_into_the_typed_model()
    {
        var result = Parser.Parse(FullCatalog, FilePath);

        Assert.True(result.IsSuccess, string.Join("\n", result.Errors.Select(error => error.ToString())));
        Assert.Empty(result.Errors);

        var catalog = result.Catalog!;
        Assert.Equal(1, catalog.Version);
        Assert.Equal(ActionDomainGate.Escalate, catalog.Defaults.UnmatchedAction);

        Assert.Collection(
            catalog.Domains,
            domain =>
            {
                Assert.Equal("delivery.bug-triage", domain.Key.Value);
                Assert.Equal("Classificar, priorizar e atribuir bugs internamente", domain.Description);
                Assert.Equal(ActionDomainGate.Decide, domain.Gate);
                Assert.Empty(domain.Match);
            },
            domain =>
            {
                Assert.Equal("delivery.release-prod", domain.Key.Value);
                Assert.Equal(ActionDomainGate.HumanApproval, domain.Gate);

                var predicate = Assert.Single(domain.Match);
                Assert.Equal(ActionDomainActionKind.Tool, predicate.Action);
                Assert.Equal("http", predicate.Attributes["tool"]);
                Assert.Equal("POST", predicate.Attributes["method"]);
                Assert.Equal("https://ci.acme.pt/pipelines/*/promote", predicate.Attributes["url"]);
                Assert.Equal(100.50m, predicate.Attributes["amount_eur"]);
            },
            domain =>
            {
                Assert.Equal("comms.external-official", domain.Key.Value);
                Assert.Equal(ActionDomainGate.Escalate, domain.Gate);

                var predicate = Assert.Single(domain.Match);
                Assert.Equal(ActionDomainActionKind.OrganizationalMessage, predicate.Action);
                Assert.Equal("report", predicate.Attributes["message_type"]);
                Assert.Equal("external", predicate.Attributes["recipient"]);
            });
    }

    [Fact]
    public void Missing_required_fields_are_reported_with_paths()
    {
        const string yaml = """
            defaults: {}
            domains:
              - description: Missing key and gate
            """;

        var result = Parser.Parse(yaml, FilePath);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.FieldPath == "version");
        Assert.Contains(result.Errors, error => error.FieldPath == "defaults.unmatched_action");
        Assert.Contains(result.Errors, error => error.FieldPath == "domains[0].key");
        Assert.Contains(result.Errors, error => error.FieldPath == "domains[0].gate");
    }

    [Fact]
    public void Unknown_enum_values_are_reported()
    {
        const string yaml = """
            version: 1
            defaults:
              unmatched_action: allow
            domains:
              - key: delivery.release-prod
                description: Promover para producao
                gate: ask-human
                match:
                  - action: connector
                    tool: http
            """;

        var result = Parser.Parse(yaml, FilePath);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.FieldPath == "defaults.unmatched_action" && error.Message.Contains("decide"));
        Assert.Contains(result.Errors, error => error.FieldPath == "domains[0].gate" && error.Message.Contains("human-approval"));
        Assert.Contains(result.Errors, error => error.FieldPath == "domains[0].match[0].action" && error.Message.Contains("tool"));
    }

    [Fact]
    public void Wrong_shapes_and_bad_scalars_are_reported_with_position()
    {
        const string yaml = """
            version: one
            defaults: escalate
            domains:
              - key: delivery.release prod
                description:
                  - not
                  - scalar
                gate: decide
                match:
                  action: tool
            """;

        var result = Parser.Parse(yaml, FilePath);

        Assert.False(result.IsSuccess);
        var versionError = Assert.Single(result.Errors, error => error.FieldPath == "version");
        Assert.Contains("integer", versionError.Message);
        Assert.NotNull(versionError.Line);
        Assert.NotNull(versionError.Column);

        Assert.Contains(result.Errors, error => error.FieldPath == "defaults");
        Assert.Contains(result.Errors, error => error.FieldPath == "domains[0].key");
        Assert.Contains(result.Errors, error => error.FieldPath == "domains[0].description");
        Assert.Contains(result.Errors, error => error.FieldPath == "domains[0].match");
    }

    [Fact]
    public void Invalid_text_scalars_are_reported_on_the_exact_field_path()
    {
        const string yaml = """
            version: 1
            defaults:
              unmatched_action: escalate
            domains:
              - key: delivery.bug-triage
                description: ""
                gate: decide
              - key: comms.external-official
                description: Comunicacao oficial para fora da organizacao
                gate: escalate
                match:
                  - action: tool
                    bad key: email
                    recipient: ""
            """;

        var result = Parser.Parse(yaml, FilePath);

        Assert.False(result.IsSuccess);

        var descriptionError = Assert.Single(result.Errors, error => error.FieldPath == "domains[0].description");
        Assert.Contains("empty", descriptionError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(descriptionError.Line);
        Assert.NotNull(descriptionError.Column);

        var keyError = Assert.Single(result.Errors, error => error.FieldPath == "domains[1].match[0].bad key");
        Assert.Contains("whitespace", keyError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(keyError.Line);
        Assert.NotNull(keyError.Column);

        var valueError = Assert.Single(result.Errors, error => error.FieldPath == "domains[1].match[0].recipient");
        Assert.Contains("empty", valueError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(valueError.Line);
        Assert.NotNull(valueError.Column);
    }

    [Fact]
    public void Malformed_yaml_reports_root_error()
    {
        const string yaml = "version: [unterminated\n";

        var result = Parser.Parse(yaml, FilePath);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("$", error.FieldPath);
        Assert.Equal(FilePath, error.FilePath);
        Assert.Contains("invalid YAML", error.Message);
        Assert.NotNull(error.Line);
    }

    [Fact]
    public void Empty_document_is_reported()
    {
        var result = Parser.Parse("\n", FilePath);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("$", error.FieldPath);
        Assert.Contains("empty", error.Message);
    }

    [Fact]
    public void ParseFile_reads_and_parses_catalog_on_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hive-action-domains-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, FullCatalog);
        try
        {
            var result = Parser.ParseFile(path);

            Assert.True(result.IsSuccess, string.Join("\n", result.Errors.Select(error => error.ToString())));
            Assert.Equal("delivery.bug-triage", result.Catalog!.Domains[0].Key.Value);
            Assert.All(result.Errors, error => Assert.Equal(path, error.FilePath));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Errors_render_with_file_path_and_position()
    {
        var error = new ActionDomainCatalogParseError(FilePath, "domains[0].key", "required field 'key' is missing.", 5, 7);

        Assert.Equal("config/organizations/acme-delivery/action-domains.yaml(5,7): domains[0].key: required field 'key' is missing.", error.ToString());
    }

    [Fact]
    public void Parse_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => Parser.Parse(null!, FilePath));
        Assert.Throws<ArgumentNullException>(() => Parser.Parse("version: 1", null!));
    }
}
