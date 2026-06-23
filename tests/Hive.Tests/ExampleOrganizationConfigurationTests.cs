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
