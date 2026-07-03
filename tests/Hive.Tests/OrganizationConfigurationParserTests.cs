using Hive.Domain.Governance;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Organization.Configuration;

namespace Hive.Tests;

/// <summary>
/// Tests for the organization YAML parser (US-F0-05-T04): that a well-formed §4.8 + §6.2 document
/// parses into the typed model of US-F0-05-T03 preserving the null edges and optional sections, that
/// malformed input yields readable errors carrying the file path and — where locatable — the field
/// path and source position, that every parse-level problem surfaces in one aggregated pass, and
/// that semantic rules (uniqueness, cross-references, structure) are deliberately left to
/// US-F0-05-T05–T07 so a well-typed but semantically broken document still parses.
/// </summary>
public sealed class OrganizationConfigurationParserTests
{
    private const string FilePath = "config/organizations/acme.yaml";

    private const string FullDocument = """
        organization:
          id: acme-delivery
          name: ACME Engenharia/Delivery
          root_unit: raiz
          owner:
            type: human
            ref: owner@acme.pt
        prompts:
          - id: ceo-v1
            path: prompts/ceo-v1.md
          - id: engineer-v1
            path: prompts/engineer-v1.md
        units:
          - id: raiz
            name: ACME
            parent: null
            leadership: ceo
          - id: engenharia
            name: Engenharia
            parent: raiz
            leadership: delivery-lead
        positions:
          - id: ceo
            name: CEO
            unit: raiz
            reports_to: null
            occupant:
              type: ai-agent
              identity_prompt_ref: ceo-v1
          - id: delivery-lead
            name: Delivery Lead
            unit: engenharia
            reports_to: ceo
            timezone: Europe/Lisbon
            occupant:
              type: ai-agent
              identity_prompt_ref: engineer-v1
              ai:
                provider: anthropic
                model: claude-sonnet-4-6
                temperature: 0.7
                max_tokens: 4096
                max_iterations: 4
                timeout: PT30S
                processing: interactive
                batch_window: null
                fallback:
                  - provider: openai
                    model: gpt-4.1
                budget:
                  reactive_max_eur_per_day: 5.00
                  proactive_max_eur_per_day: 1.00
                  total_max_eur_per_day: 6.00
                  max_calls_per_hour: 60
              schedule:
                - id: relatorio-diario
                  cron: "0 55 17 * * MON-FRI"
                  instruction: "Compilar e enviar relatorio diario ao superior"
              subscriptions:
                - event: directive-deadline-approaching
                  within: PT4H
              working_hours:
                start: "09:00"
                end: "18:00"
              tools:
                - connector: http
                  scope: ["https://api.empresa.pt/*"]
              authority:
                can_decide: ["delivery.bug-triage"]
                overrides:
                  - key: comms.external-official
                    gate: human-approval
                    approver: ceo
        """;

    private static OrganizationConfigurationParser Parser => new();

    [Fact]
    public void Full_document_parses_into_the_typed_model()
    {
        var result = Parser.Parse(FullDocument, FilePath);

        Assert.True(result.IsSuccess, string.Join("\n", result.Errors.Select(error => error.ToString())));
        Assert.Empty(result.Errors);

        var config = result.Configuration!;
        Assert.Equal("acme-delivery", config.Organization.Id.Value);
        Assert.Equal("ACME Engenharia/Delivery", config.Organization.Name);
        Assert.Equal("raiz", config.Organization.RootUnit.Value);
        Assert.Equal(OwnerType.Human, config.Organization.Owner.Type);
        Assert.Equal("owner@acme.pt", config.Organization.Owner.Ref);

        Assert.Collection(
            config.Prompts,
            prompt =>
            {
                Assert.Equal("ceo-v1", prompt.Id);
                Assert.Equal("prompts/ceo-v1.md", prompt.Path);
            },
            prompt =>
            {
                Assert.Equal("engineer-v1", prompt.Id);
                Assert.Equal("prompts/engineer-v1.md", prompt.Path);
            });

        Assert.Equal(2, config.Units.Count);
        Assert.Equal(2, config.Positions.Count);
    }

    [Fact]
    public void Root_edges_are_null_and_non_root_edges_reference_their_parent_and_superior()
    {
        var config = Parser.Parse(FullDocument, FilePath).Configuration!;

        var root = config.Units.Single(unit => unit.Id.Value == "raiz");
        Assert.Null(root.Parent);
        Assert.Equal("ceo", root.Leadership.Value);

        var engineering = config.Units.Single(unit => unit.Id.Value == "engenharia");
        Assert.Equal("raiz", engineering.Parent!.Value);

        var ceo = config.Positions.Single(position => position.Id.Value == "ceo");
        Assert.Null(ceo.ReportsTo);

        var lead = config.Positions.Single(position => position.Id.Value == "delivery-lead");
        Assert.Equal("ceo", lead.ReportsTo!.Value);
        Assert.Equal("engenharia", lead.Unit.Value);
        Assert.Equal("Europe/Lisbon", lead.Timezone);
    }

    [Fact]
    public void Ai_occupant_interior_is_captured_in_full()
    {
        var config = Parser.Parse(FullDocument, FilePath).Configuration!;
        var occupant = config.Positions.Single(position => position.Id.Value == "delivery-lead").Occupant;

        Assert.Equal(OccupantType.AiAgent, occupant.Type);
        Assert.Equal("engineer-v1", occupant.IdentityPromptRef);

        var ai = occupant.Ai!;
        Assert.Equal("anthropic", ai.Provider);
        Assert.Equal("claude-sonnet-4-6", ai.Model);
        Assert.Equal(0.7, ai.Temperature);
        Assert.Equal(4096, ai.MaxTokens);
        Assert.Equal(4, ai.MaxIterations);
        Assert.Equal("PT30S", ai.Timeout);
        Assert.Equal("interactive", ai.Processing);
        Assert.Null(ai.BatchWindow);

        Assert.Collection(
            ai.Fallback,
            fallback =>
            {
                Assert.Equal("openai", fallback.Provider);
                Assert.Equal("gpt-4.1", fallback.Model);
            });

        var budget = ai.Budget!;
        Assert.Equal(5.00m, budget.ReactiveMaxEurPerDay);
        Assert.Equal(1.00m, budget.ProactiveMaxEurPerDay);
        Assert.Equal(6.00m, budget.TotalMaxEurPerDay);
        Assert.Equal(60, budget.MaxCallsPerHour);

        Assert.Collection(
            occupant.Schedule,
            entry =>
            {
                Assert.Equal("relatorio-diario", entry.Id);
                Assert.Equal("0 55 17 * * MON-FRI", entry.Cron);
                Assert.Equal("Compilar e enviar relatorio diario ao superior", entry.Instruction);
            });

        Assert.Collection(
            occupant.Subscriptions,
            subscription =>
            {
                Assert.Equal("directive-deadline-approaching", subscription.Event);
                Assert.Equal("PT4H", subscription.Within);
            });

        var workingHours = occupant.WorkingHours!;
        Assert.Equal("09:00", workingHours.Start);
        Assert.Equal("18:00", workingHours.End);

        Assert.Collection(
            occupant.Tools,
            tool =>
            {
                Assert.Equal("http", tool.Connector);
                Assert.Equal(new[] { "https://api.empresa.pt/*" }, tool.Scope);
            });

        var authority = occupant.Authority!;
        Assert.Equal(new[] { "delivery.bug-triage" }, authority.CanDecide.Select(key => key.Value));
        var authorityOverride = Assert.Single(authority.Overrides);
        Assert.Equal("comms.external-official", authorityOverride.Key.Value);
        Assert.Equal(ActionDomainGate.HumanApproval, authorityOverride.Gate);
        Assert.Equal("ceo", authorityOverride.Approver);
    }

    [Fact]
    public void Minimal_document_parses_with_empty_optional_blocks()
    {
        const string yaml = """
            organization:
              id: acme
              root_unit: raiz
              owner:
                type: group
                ref: owners@acme.pt
            """;

        var result = Parser.Parse(yaml, FilePath);

        Assert.True(result.IsSuccess);
        var config = result.Configuration!;
        Assert.Null(config.Organization.Name);
        Assert.Empty(config.Prompts);
        Assert.Empty(config.Units);
        Assert.Empty(config.Positions);
    }

    [Fact]
    public void Human_occupant_omits_the_ai_block()
    {
        const string yaml = """
            organization:
              id: acme
              root_unit: raiz
              owner:
                type: human
                ref: owner@acme.pt
            positions:
              - id: cfo
                unit: raiz
                reports_to: ceo
                occupant:
                  type: human
            """;

        var occupant = Parser.Parse(yaml, FilePath).Configuration!.Positions.Single().Occupant;

        Assert.Equal(OccupantType.Human, occupant.Type);
        Assert.Null(occupant.Ai);
        Assert.Null(occupant.Authority);
        Assert.Empty(occupant.Schedule);
        Assert.Empty(occupant.Tools);
    }

    [Fact]
    public void Malformed_yaml_reports_a_single_root_error_with_a_position()
    {
        const string yaml = "organization: [unterminated\n  id: acme\n";

        var result = Parser.Parse(yaml, FilePath);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Configuration);
        var error = Assert.Single(result.Errors);
        Assert.Equal("$", error.FieldPath);
        Assert.Equal(FilePath, error.FilePath);
        Assert.Contains("invalid YAML", error.Message);
        Assert.NotNull(error.Line);
    }

    [Fact]
    public void Missing_organization_block_is_reported()
    {
        const string yaml = """
            units:
              - id: raiz
                parent: null
                leadership: ceo
            """;

        var result = Parser.Parse(yaml, FilePath);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.FieldPath == "organization" && error.Message.Contains("missing"));
    }

    [Fact]
    public void Missing_required_fields_are_reported_with_their_paths()
    {
        const string yaml = """
            organization:
              name: ACME
              owner:
                type: human
            """;

        var result = Parser.Parse(yaml, FilePath);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.FieldPath == "organization.id");
        Assert.Contains(result.Errors, error => error.FieldPath == "organization.root_unit");
        Assert.Contains(result.Errors, error => error.FieldPath == "organization.owner.ref");
    }

    [Fact]
    public void Unknown_enum_values_are_reported()
    {
        const string yaml = """
            organization:
              id: acme
              root_unit: raiz
              owner:
                type: corporation
                ref: owner@acme.pt
            positions:
              - id: ceo
                unit: raiz
                reports_to: null
                occupant:
                  type: robot
            """;

        var result = Parser.Parse(yaml, FilePath);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.FieldPath == "organization.owner.type" && error.Message.Contains("human"));
        Assert.Contains(result.Errors, error => error.FieldPath == "positions[0].occupant.type" && error.Message.Contains("ai-agent"));
    }

    [Fact]
    public void Wrong_shape_and_bad_numbers_are_reported_with_position()
    {
        const string yaml = """
            organization:
              id: acme
              root_unit: raiz
              owner:
                type: human
                ref: owner@acme.pt
            positions:
              - id: ceo
                unit: raiz
                reports_to: null
                occupant:
                  type: ai-agent
                  ai:
                    provider: anthropic
                    model: claude-sonnet-4-6
                    max_tokens: lots
            """;

        var result = Parser.Parse(yaml, FilePath);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(
            result.Errors,
            candidate => candidate.FieldPath == "positions[0].occupant.ai.max_tokens");
        Assert.Contains("integer", error.Message);
        Assert.NotNull(error.Line);
        Assert.NotNull(error.Column);
    }

    [Fact]
    public void Non_mapping_root_is_reported()
    {
        var result = Parser.Parse("- just\n- a\n- list\n", FilePath);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("$", error.FieldPath);
        Assert.Contains("mapping", error.Message);
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
    public void Well_typed_but_semantically_broken_document_still_parses()
    {
        // Duplicate ids and a dangling reports_to are semantic problems owned by T05/T06, not the
        // parser: the document is well-shaped, so it must parse successfully here.
        const string yaml = """
            organization:
              id: acme
              root_unit: missing-unit
              owner:
                type: human
                ref: owner@acme.pt
            units:
              - id: raiz
                parent: null
                leadership: ceo
              - id: raiz
                parent: raiz
                leadership: ceo
            positions:
              - id: ceo
                unit: raiz
                reports_to: nonexistent
                occupant:
                  type: ai-agent
            """;

        var result = Parser.Parse(yaml, FilePath);

        Assert.True(result.IsSuccess, string.Join("\n", result.Errors.Select(error => error.ToString())));
        Assert.Equal(2, result.Configuration!.Units.Count);
    }

    [Fact]
    public void Deprecated_authority_lists_are_rejected()
    {
        const string yaml = """
            organization:
              id: acme
              root_unit: raiz
              owner:
                type: human
                ref: owner@acme.pt
            positions:
              - id: ceo
                unit: raiz
                reports_to: null
                occupant:
                  type: ai-agent
                  authority:
                    can_decide: ["org.quarterly-priorities"]
                    must_escalate: ["finance.commitments"]
                    requires_human_approval: ["org.structure-change"]
            """;

        var result = Parser.Parse(yaml, FilePath);

        Assert.False(result.IsSuccess);
        Assert.Contains(
            result.Errors,
            error => error.FieldPath == "positions[0].occupant.authority.must_escalate"
                && error.Message.Contains("no longer supported", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Errors,
            error => error.FieldPath == "positions[0].occupant.authority.requires_human_approval"
                && error.Message.Contains("no longer supported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Errors_render_with_file_path_and_position()
    {
        var error = new OrganizationConfigurationParseError(FilePath, "organization.id", "required field 'id' is missing.", 3, 5);

        Assert.Equal("config/organizations/acme.yaml(3,5): organization.id: required field 'id' is missing.", error.ToString());
    }

    [Fact]
    public void ParseFile_reads_and_parses_a_file_on_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hive-org-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, FullDocument);
        try
        {
            var result = Parser.ParseFile(path);

            Assert.True(result.IsSuccess, string.Join("\n", result.Errors.Select(error => error.ToString())));
            Assert.Equal("acme-delivery", result.Configuration!.Organization.Id.Value);
            Assert.All(result.Errors, error => Assert.Equal(path, error.FilePath));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => Parser.Parse(null!, FilePath));
        Assert.Throws<ArgumentNullException>(() => Parser.Parse("organization:", null!));
    }
}
