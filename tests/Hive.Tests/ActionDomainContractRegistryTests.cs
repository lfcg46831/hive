using Hive.Domain.Governance;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Governance;
using Hive.Infrastructure.Organization.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests;

public sealed class ActionDomainContractRegistryTests
{
    [Fact]
    public void Default_DI_composition_combines_platform_and_connector_sources()
    {
        var services = new ServiceCollection();
        services.AddHiveActionDomainContracts();
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IActionDomainContractRegistry>();

        Assert.Contains(
            registry.ActionContracts,
            contract => contract.Action == ActionDomainActionKind.OrganizationalMessage
                        && contract.SelectorValue == nameof(Report));
        Assert.Contains(
            registry.ActionContracts,
            contract => contract.Action == ActionDomainActionKind.Tool
                        && contract.SelectorValue == "jira");
        Assert.Contains(
            registry.ActionExtractors,
            extractor => extractor.Action == ActionDomainActionKind.Tool
                         && extractor.SelectorValue == "email.send");
    }

    [Fact]
    public void Organization_binding_uses_only_the_injected_contract_registry()
    {
        var configurationResult = new OrganizationConfigurationParser().Parse(
            """
            organization:
              id: isolated-org
              root_unit: root
              owner:
                type: human
                ref: owner@example.test
            units:
              - id: root
                parent: null
                leadership: lead
            positions:
              - id: lead
                unit: root
                reports_to: null
                occupant:
                  type: ai-agent
            """,
            "isolated-org/organization.yaml");
        Assert.True(configurationResult.IsSuccess, string.Join("; ", configurationResult.Errors));
        var registry = new ActionDomainContractRegistry(
        [
            new Source(
                [ActionDomainActionContract.ForOrganizationalMessage(nameof(Directive))],
                []),
        ]);

        var binding = OrganizationActionDomainBinding.Create(
            configurationResult.Configuration!,
            registry);

        var contract = Assert.Single(binding.ActionContracts);
        Assert.Equal(nameof(Directive), contract.SelectorValue);
        Assert.Empty(binding.ActionExtractors);
    }

    [Fact]
    public void Registry_rejects_duplicate_contracts_before_any_organization_is_imported()
    {
        var contract = ActionDomainActionContract.ForTool("duplicate.tool");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ActionDomainContractRegistry(
            [
                new Source([contract], []),
                new Source([contract], []),
            ]));

        Assert.Contains("registered more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_rejects_missing_extractor_for_a_derived_contract()
    {
        var contract = ActionDomainActionContract.ForTool(
            "derived.tool",
            [ActionAttributeDefinition.Derived("classification", ActionAttributeValueKind.String)]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ActionDomainContractRegistry([new Source([contract], [])]));

        Assert.Contains("invalid extractor registration", exception.Message, StringComparison.Ordinal);
    }

    private sealed record Source(
        IReadOnlyList<ActionDomainActionContract> ActionContracts,
        IReadOnlyList<ActionAttributeExtractorRegistration> ActionExtractors)
        : IActionDomainContractSource;
}
