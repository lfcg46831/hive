using Hive.Domain.Organization.Configuration.Validation;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Organization.Configuration;

namespace Hive.Tests;

public sealed class ExampleOrganizationConfigurationTests
{
    private const string OrganizationId = "acme-delivery";

    [Fact]
    public void Example_organization_parses_and_satisfies_the_minimal_F0_contract()
    {
        var result = new OrganizationConfigurationParser().ParseFile(OrganizationFile);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        var configuration = result.Configuration!;

        Assert.Equal(OrganizationId, configuration.Organization.Id.Value);
        Assert.Equal("raiz", configuration.Organization.RootUnit.Value);
        Assert.Equal(2, configuration.Units.Count);
        Assert.Equal(3, configuration.Positions.Count);
        Assert.Equal(3, configuration.Prompts.Count);

        Assert.True(OrganizationConfigurationUniquenessValidator.Validate(configuration).IsValid);
        Assert.True(OrganizationConfigurationCrossReferenceValidator.Validate(configuration).IsValid);
        Assert.True(OrganizationConfigurationStructuralValidator.Validate(configuration).IsValid);

        var deliveryLead = configuration.Positions.Single(position => position.Id.Value == "delivery-lead");
        Assert.Equal("ceo", deliveryLead.ReportsTo!.Value);
        Assert.NotNull(deliveryLead.Occupant.Ai);
        Assert.NotNull(deliveryLead.Occupant.Authority);
        Assert.Single(deliveryLead.Occupant.Schedule);

        var bugTriage = configuration.Positions.Single(position => position.Id.Value == "bug-triage");
        Assert.Equal("delivery-lead", bugTriage.ReportsTo!.Value);
        Assert.Equal("engenharia", bugTriage.Unit.Value);
        Assert.Equal("Bug Triage", bugTriage.Name);
        Assert.Equal(OccupantType.AiAgent, bugTriage.Occupant.Type);
        Assert.Equal("triage-v1", bugTriage.Occupant.IdentityPromptRef);
        Assert.NotNull(bugTriage.Occupant.Ai);
        Assert.Equal("openai", bugTriage.Occupant.Ai.Provider);
        Assert.Equal("gpt-5-mini", bugTriage.Occupant.Ai.Model);
        Assert.Empty(bugTriage.Occupant.Ai.Fallback);
        Assert.All(configuration.Positions, position =>
        {
            Assert.Equal("openai", position.Occupant.Ai!.Provider);
            Assert.Equal("gpt-5-mini", position.Occupant.Ai.Model);
            Assert.Empty(position.Occupant.Ai.Fallback);
        });
        Assert.NotNull(bugTriage.Occupant.Authority);
        Assert.Equal(
            ["delivery.bug-triage"],
            bugTriage.Occupant.Authority.CanDecide.Select(key => key.Value));
        Assert.Empty(bugTriage.Occupant.Schedule);
    }

    [Fact]
    public void Example_prompt_catalog_resolves_to_non_empty_files_inside_its_directory()
    {
        var result = new OrganizationConfigurationParser().ParseFile(OrganizationFile);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        foreach (var prompt in result.Configuration!.Prompts)
        {
            var path = Path.GetFullPath(Path.Combine(OrganizationDirectory, prompt.Path));
            Assert.StartsWith(OrganizationDirectory + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
            Assert.True(File.Exists(path), $"Prompt '{prompt.Id}' was not found at '{path}'.");
            Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(path)));
        }
    }

    [Theory]
    [InlineData("triage-v1.md")]
    [InlineData("triage-v2.md")]
    public void Example_triage_identities_contain_only_business_authored_content(string fileName)
    {
        var prompt = File.ReadAllText(Path.Combine(
            OrganizationDirectory,
            "prompts",
            fileName));

        Assert.Contains("## Role", prompt, StringComparison.Ordinal);
        Assert.Contains("## Responsibilities", prompt, StringComparison.Ordinal);
        Assert.Contains("## Outcomes and quality criteria", prompt, StringComparison.Ordinal);
        Assert.Contains("## Functional boundaries", prompt, StringComparison.Ordinal);
        Assert.Contains("Assess the reported severity and user impact", prompt, StringComparison.Ordinal);
        Assert.Contains("Assumptions and uncertainty", prompt, StringComparison.Ordinal);

        var forbiddenContractTerms = new[]
        {
            "`Report`",
            "`Escalation`",
            "`Directive`",
            "Directive.Context",
            "acting_under",
            "schema_version",
            "report.body",
            "escalation.context",
            "hive-evaluation-v1",
            "missing_information",
            "HIVE message",
            "DTO",
            "API contract",
            "routing",
            "tool",
        };

        Assert.All(
            forbiddenContractTerms,
            term => Assert.DoesNotContain(term, prompt, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Example_triage_v2_makes_the_business_decision_boundary_explicit()
    {
        var prompt = File.ReadAllText(Path.Combine(
            OrganizationDirectory,
            "prompts",
            "triage-v2.md"));

        Assert.Contains("## Decision procedure", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "Does the available evidence support the severity assessment?",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Does the available evidence support a safe, actionable next step?",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "only when both answers are yes",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "If either answer is no",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Missing facts are non-blocking only when the available evidence still supports both conclusions.",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_recovery_experiment_versions_only_the_triage_prompt_and_luna_model()
    {
        var source = new OrganizationConfigurationParser().ParseFile(OrganizationFile);
        var experiment = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(
                RepositoryRoot,
                "config",
                "experiments",
                "gpt-5.6-luna-prompt-recovery",
                "organization.yaml"));

        Assert.True(source.IsSuccess, string.Join(Environment.NewLine, source.Errors));
        Assert.True(experiment.IsSuccess, string.Join(Environment.NewLine, experiment.Errors));

        var sourceTriage = source.Configuration!.Positions.Single(position => position.Id.Value == "bug-triage");
        var experimentTriage = experiment.Configuration!.Positions.Single(position => position.Id.Value == "bug-triage");

        Assert.Equal("triage-v1", sourceTriage.Occupant.IdentityPromptRef);
        Assert.Equal("gpt-5-mini", sourceTriage.Occupant.Ai!.Model);
        Assert.Equal("triage-v2", experimentTriage.Occupant.IdentityPromptRef);
        Assert.Equal("gpt-5.6-luna", experimentTriage.Occupant.Ai!.Model);
        Assert.Contains(
            experiment.Configuration.Prompts,
            prompt => prompt.Id == "triage-v2" && prompt.Path == "prompts/triage-v2.md");
        Assert.DoesNotContain(
            experiment.Configuration.Prompts,
            prompt => prompt.Id == "triage-v1");
    }

    [Fact]
    public void Gpt_5_4_mini_experiment_preserves_triage_v2_and_changes_only_the_model_profile()
    {
        var t14 = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(
                RepositoryRoot,
                "config",
                "experiments",
                "gpt-5.6-luna-prompt-recovery",
                "organization.yaml"));
        var t15 = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(
                RepositoryRoot,
                "config",
                "experiments",
                "gpt-5.4-mini-prompt-recovery",
                "organization.yaml"));

        Assert.True(t14.IsSuccess, string.Join(Environment.NewLine, t14.Errors));
        Assert.True(t15.IsSuccess, string.Join(Environment.NewLine, t15.Errors));

        var t14Triage = t14.Configuration!.Positions.Single(position => position.Id.Value == "bug-triage");
        var t15Triage = t15.Configuration!.Positions.Single(position => position.Id.Value == "bug-triage");

        Assert.Equal("triage-v2", t14Triage.Occupant.IdentityPromptRef);
        Assert.Equal("triage-v2", t15Triage.Occupant.IdentityPromptRef);
        Assert.Equal("gpt-5.6-luna", t14Triage.Occupant.Ai!.Model);
        Assert.Equal("gpt-5.4-mini-2026-03-17", t15Triage.Occupant.Ai!.Model);
        Assert.Equal(t14Triage.Occupant.Ai.MaxTokens, t15Triage.Occupant.Ai.MaxTokens);
        Assert.Equal(t14Triage.Occupant.Ai.Timeout, t15Triage.Occupant.Ai.Timeout);
        Assert.Equal(
            t14Triage.Occupant.Authority!.CanDecide.Select(key => key.Value),
            t15Triage.Occupant.Authority!.CanDecide.Select(key => key.Value));
        Assert.Equal(
            t14Triage.Occupant.Authority.Overrides.Select(item => item.Key.Value),
            t15Triage.Occupant.Authority.Overrides.Select(item => item.Key.Value));
    }

    [Fact]
    public void Gpt_5_6_terra_experiment_preserves_t15_inputs_and_changes_only_the_model_profile()
    {
        var t15 = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(
                RepositoryRoot,
                "config",
                "experiments",
                "gpt-5.4-mini-prompt-recovery",
                "organization.yaml"));
        var t16 = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(
                RepositoryRoot,
                "config",
                "experiments",
                "gpt-5.6-terra",
                "organization.yaml"));

        Assert.True(t15.IsSuccess, string.Join(Environment.NewLine, t15.Errors));
        Assert.True(t16.IsSuccess, string.Join(Environment.NewLine, t16.Errors));

        var t15Triage = t15.Configuration!.Positions.Single(position => position.Id.Value == "bug-triage");
        var t16Triage = t16.Configuration!.Positions.Single(position => position.Id.Value == "bug-triage");

        Assert.Equal("triage-v2", t16Triage.Occupant.IdentityPromptRef);
        Assert.Equal("gpt-5.4-mini-2026-03-17", t15Triage.Occupant.Ai!.Model);
        Assert.Equal("gpt-5.6-terra", t16Triage.Occupant.Ai!.Model);
        Assert.Equal(t15Triage.Occupant.Ai.MaxTokens, t16Triage.Occupant.Ai.MaxTokens);
        Assert.Equal(t15Triage.Occupant.Ai.Timeout, t16Triage.Occupant.Ai.Timeout);
        Assert.Equal(
            t15Triage.Occupant.Authority!.CanDecide.Select(key => key.Value),
            t16Triage.Occupant.Authority!.CanDecide.Select(key => key.Value));
        Assert.Equal(
            t15Triage.Occupant.Authority.Overrides.Select(item => item.Key.Value),
            t16Triage.Occupant.Authority.Overrides.Select(item => item.Key.Value));
    }

    [Fact]
    public void Example_bug_directive_context_is_normalized_demo_content_not_contract()
    {
        var context = File.ReadAllText(Path.Combine(
            OrganizationDirectory,
            "examples",
            "bug-triage-directive-context.md"));

        Assert.Contains("normalized F0 demo example", context, StringComparison.Ordinal);
        Assert.Contains("not a required input schema", context, StringComparison.Ordinal);
        Assert.Contains("title:", context, StringComparison.Ordinal);
        Assert.Contains("description:", context, StringComparison.Ordinal);
        Assert.Contains("reported_severity:", context, StringComparison.Ordinal);
        Assert.Contains("origin:", context, StringComparison.Ordinal);
        Assert.Contains("reproduction_steps:", context, StringComparison.Ordinal);
        Assert.Contains("environment:", context, StringComparison.Ordinal);
        Assert.Contains("textual_attachments:", context, StringComparison.Ordinal);
        Assert.Contains("correlation_metadata:", context, StringComparison.Ordinal);
        Assert.DoesNotContain("class Bug", context, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BugDto", context, StringComparison.OrdinalIgnoreCase);
    }

    private static string OrganizationFile => Path.Combine(OrganizationDirectory, "organization.yaml");

    private static string OrganizationDirectory =>
        Path.Combine(RepositoryRoot, "config", "organizations", OrganizationId);

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
