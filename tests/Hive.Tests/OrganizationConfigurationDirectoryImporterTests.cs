using Hive.Domain.Identity;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Governance;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests;

public sealed class OrganizationConfigurationDirectoryImporterTests
{
    private const string ValidOrganizationYaml = """
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
            name: Engenharia/Delivery
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
            occupant:
              type: ai-agent
              identity_prompt_ref: engineer-v1
        """;

    [Fact]
    public async Task Directory_importer_materializes_each_declared_organization()
    {
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationDirectoryImporter(
            new OrganizationConfigurationParser(),
            new OrganizationConfigurationImporter(registry),
            ContractRegistry());

        var results = await importer.ImportAsync(
            Path.Combine(RepositoryRoot, "config", "organizations"));

        var result = Assert.Single(results);
        Assert.Equal(OrganizationImportStatus.Applied, result.Status);
        Assert.Equal("acme-delivery", result.Snapshot!.OrganizationId.Value);
        Assert.Equal(2, result.Snapshot.Units.Count);
        Assert.Equal(3, result.Snapshot.Positions.Count);
        Assert.Single(result.Snapshot.Schedules);
        Assert.Contains(
            result.Snapshot.ActionDomainCatalog.Value.Domains,
            domain => domain.Key.Value == "delivery.bug-triage");
    }

    [Fact]
    public async Task Directory_importer_rejects_prompt_path_that_does_not_exist_before_writing()
    {
        var root = CreateOrganizationTree(
            Mutate(ValidOrganizationYaml, "prompts/engineer-v1.md", "prompts/missing.md"),
            ["prompts/ceo-v1.md"]);
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationDirectoryImporter(
            new OrganizationConfigurationParser(),
            new OrganizationConfigurationImporter(registry),
            ContractRegistry());
        var expectedPath = Path.GetFullPath(
            Path.Combine(root, "acme-delivery", "prompts", "missing.md"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => importer.ImportAsync(root));

        Assert.Contains("prompts[1].path", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedPath, exception.Message, StringComparison.Ordinal);
        Assert.False(registry.TryGetSnapshot(OrganizationId.From("acme-delivery"), out _));
    }

    [Fact]
    public async Task Directory_importer_rejects_prompt_path_that_escapes_organization_tree_before_writing()
    {
        var root = CreateOrganizationTree(
            Mutate(ValidOrganizationYaml, "prompts/engineer-v1.md", "../engineer-v1.md"),
            ["prompts/ceo-v1.md"]);
        await File.WriteAllTextAsync(
            Path.Combine(root, "engineer-v1.md"),
            "outside organization tree");
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationDirectoryImporter(
            new OrganizationConfigurationParser(),
            new OrganizationConfigurationImporter(registry),
            ContractRegistry());
        var expectedPath = Path.GetFullPath(
            Path.Combine(root, "engineer-v1.md"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => importer.ImportAsync(root));

        Assert.Contains("prompts[1].path", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedPath, exception.Message, StringComparison.Ordinal);
        Assert.False(registry.TryGetSnapshot(OrganizationId.From("acme-delivery"), out _));
    }

    [Fact]
    public async Task Directory_importer_rejects_absolute_prompt_path_even_when_it_points_inside_the_organization_tree()
    {
        var root = CreateOrganizationTree(
            ValidOrganizationYaml,
            ["prompts/ceo-v1.md", "prompts/engineer-v1.md"]);
        var absolutePromptPath = Path.GetFullPath(
                Path.Combine(root, "acme-delivery", "prompts", "engineer-v1.md"))
            .Replace(Path.DirectorySeparatorChar, '/');
        File.WriteAllText(
            Path.Combine(root, "acme-delivery", "organization.yaml"),
            Mutate(ValidOrganizationYaml, "prompts/engineer-v1.md", absolutePromptPath));
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationDirectoryImporter(
            new OrganizationConfigurationParser(),
            new OrganizationConfigurationImporter(registry),
            ContractRegistry());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => importer.ImportAsync(root));

        Assert.Contains("prompts[1].path", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            absolutePromptPath.Replace('/', Path.DirectorySeparatorChar),
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(registry.TryGetSnapshot(OrganizationId.From("acme-delivery"), out _));
    }

    [Fact]
    public async Task Directory_importer_rejects_missing_action_domain_catalog_before_writing()
    {
        var root = CreateOrganizationTree(
            ValidOrganizationYaml,
            ["prompts/ceo-v1.md", "prompts/engineer-v1.md"]);
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationDirectoryImporter(
            new OrganizationConfigurationParser(),
            new OrganizationConfigurationImporter(registry),
            ContractRegistry());

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => importer.ImportAsync(root));

        Assert.Contains("action-domains.yaml", exception.Message, StringComparison.Ordinal);
        Assert.False(registry.TryGetSnapshot(OrganizationId.From("acme-delivery"), out _));
    }

    private static string CreateOrganizationTree(string organizationYaml, IReadOnlyList<string> promptFiles)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hive-org-import-{Guid.NewGuid():N}");
        var organizationDirectory = Path.Combine(root, "acme-delivery");
        Directory.CreateDirectory(organizationDirectory);
        File.WriteAllText(Path.Combine(organizationDirectory, "organization.yaml"), organizationYaml);

        foreach (var promptFile in promptFiles)
        {
            var path = Path.Combine(
                organizationDirectory,
                promptFile.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"# {Path.GetFileNameWithoutExtension(path)}");
        }

        return root;
    }

    private static IActionDomainContractRegistry ContractRegistry()
    {
        var services = new ServiceCollection();
        services.AddHiveActionDomainContracts();
        return services.BuildServiceProvider().GetRequiredService<IActionDomainContractRegistry>();
    }

    private static string Mutate(string yaml, string token, string replacement)
    {
        var first = yaml.IndexOf(token, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Token '{token}' was not found in the base document.");
        Assert.Equal(
            -1,
            yaml.IndexOf(token, first + token.Length, StringComparison.Ordinal));
        return yaml.Remove(first, token.Length).Insert(first, replacement);
    }

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
