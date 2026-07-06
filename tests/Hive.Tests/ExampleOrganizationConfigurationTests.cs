using Hive.Domain.Organization.Configuration.Validation;
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
        Assert.Equal(2, configuration.Positions.Count);
        Assert.Equal(2, configuration.Prompts.Count);

        Assert.True(OrganizationConfigurationUniquenessValidator.Validate(configuration).IsValid);
        Assert.True(OrganizationConfigurationCrossReferenceValidator.Validate(configuration).IsValid);
        Assert.True(OrganizationConfigurationStructuralValidator.Validate(configuration).IsValid);

        var deliveryLead = configuration.Positions.Single(position => position.Id.Value == "delivery-lead");
        Assert.Equal("ceo", deliveryLead.ReportsTo!.Value);
        Assert.NotNull(deliveryLead.Occupant.Ai);
        Assert.NotNull(deliveryLead.Occupant.Authority);
        Assert.Single(deliveryLead.Occupant.Schedule);
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

    [Fact]
    public void Example_delivery_prompt_accepts_freeform_bug_context_and_lists_triage_facts()
    {
        var prompt = File.ReadAllText(Path.Combine(
            OrganizationDirectory,
            "prompts",
            "engineer-v1.md"));

        Assert.Contains("Example bug triage facts", prompt, StringComparison.Ordinal);
        Assert.Contains("Directive.Context", prompt, StringComparison.Ordinal);
        Assert.Contains("free-form or partial", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not require callers to use these exact field names", prompt, StringComparison.Ordinal);
        Assert.Contains("title", prompt, StringComparison.Ordinal);
        Assert.Contains("description", prompt, StringComparison.Ordinal);
        Assert.Contains("reported_severity", prompt, StringComparison.Ordinal);
        Assert.Contains("origin", prompt, StringComparison.Ordinal);
        Assert.Contains("reproduction_steps", prompt, StringComparison.Ordinal);
        Assert.Contains("environment", prompt, StringComparison.Ordinal);
        Assert.Contains("textual_attachments", prompt, StringComparison.Ordinal);
        Assert.Contains("correlation_metadata", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "Do not introduce a bug-specific HIVE message, DTO, route, or API contract.",
            prompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain("expected content convention", prompt, StringComparison.OrdinalIgnoreCase);
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
