using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Infrastructure.Governance;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests;

public sealed class OrganizationActionGateRuntimeProviderTests
{
    [Fact]
    public async Task Runtime_provider_keeps_organization_catalogs_and_authority_bindings_isolated()
    {
        var registry = new InMemoryOrganizationRegistry();
        var contractRegistry = ContractRegistry();
        var importer = new OrganizationConfigurationImporter(registry);
        var firstConfiguration = Configuration("org-alpha", "alpha-lead", "alpha.triage");
        var secondConfiguration = Configuration("org-beta", "beta-lead", "beta.triage");

        var first = await importer.ImportAsync(
            firstConfiguration,
            Catalog("alpha.triage"),
            OrganizationActionDomainBinding.Create(firstConfiguration, contractRegistry));
        var second = await importer.ImportAsync(
            secondConfiguration,
            Catalog("beta.triage"),
            OrganizationActionDomainBinding.Create(secondConfiguration, contractRegistry));
        var provider = new RegistryOrganizationActionGateRuntimeProvider(registry, contractRegistry);

        var alpha = await provider.FindAsync(OrganizationId.From("org-alpha"));
        var beta = await provider.FindAsync(OrganizationId.From("org-beta"));

        Assert.Equal(OrganizationImportStatus.Applied, first.Status);
        Assert.Equal(OrganizationImportStatus.Applied, second.Status);
        Assert.Equal("alpha.triage", Assert.Single(alpha!.Catalog.Domains).Key.Value);
        Assert.Equal("beta.triage", Assert.Single(beta!.Catalog.Domains).Key.Value);
        Assert.Equal(
            "alpha.triage",
            Assert.Single(Assert.Single(alpha.Binding.Authorities).CanDecide).Value);
        Assert.Equal(
            "beta.triage",
            Assert.Single(Assert.Single(beta.Binding.Authorities).CanDecide).Value);
    }

    [Theory]
    [InlineData("Report")]
    [InlineData("Escalation")]
    [InlineData("Directive")]
    public void Platform_binding_registers_organizational_message_contracts(string selector)
    {
        var configuration = Configuration("org-messages", "message-lead", authorityKey: null);
        var catalog = new ActionDomainCatalog(
            1,
            new ActionDomainCatalogDefaults(ActionDomainGate.Escalate),
            [
                new ActionDomain(
                    AuthorityKey.From("messages.objective"),
                    "Objective organizational message.",
                    ActionDomainGate.Escalate,
                    [
                        new ActionDomainMatchPredicate(
                            ActionDomainActionKind.OrganizationalMessage,
                            new Dictionary<string, object> { ["message_type"] = selector }),
                    ]),
            ]);

        var validation = ActionDomainCatalogValidator.Validate(
            catalog,
            OrganizationActionDomainBinding.Create(configuration, ContractRegistry()));

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
    }

    [Fact]
    public async Task Import_rejects_catalog_action_without_a_registered_contract_atomically()
    {
        var registry = new InMemoryOrganizationRegistry();
        var configuration = Configuration("org-invalid", "invalid-lead", authorityKey: null);
        var catalog = new ActionDomainCatalog(
            1,
            new ActionDomainCatalogDefaults(ActionDomainGate.Escalate),
            [
                new ActionDomain(
                    AuthorityKey.From("unknown.action"),
                    "Unknown action.",
                    ActionDomainGate.Escalate,
                    [
                        new ActionDomainMatchPredicate(
                            ActionDomainActionKind.Tool,
                            new Dictionary<string, object> { ["tool"] = "unknown.execute" }),
                    ]),
            ]);

        var result = await new OrganizationConfigurationImporter(registry).ImportAsync(
            configuration,
            catalog,
            OrganizationActionDomainBinding.Create(configuration, ContractRegistry()));

        Assert.Equal(OrganizationImportStatus.Invalid, result.Status);
        Assert.Contains(result.ValidationErrors, error => error.Code == "action-contract-not-found");
        Assert.False(registry.TryGetSnapshot(configuration.Organization.Id, out _));
    }

    [Fact]
    public async Task Runtime_provider_returns_null_for_an_absent_organization()
    {
        var provider = new RegistryOrganizationActionGateRuntimeProvider(
            new InMemoryOrganizationRegistry(),
            ContractRegistry());

        var snapshot = await provider.FindAsync(OrganizationId.From("missing-org"));

        Assert.Null(snapshot);
    }

    [Fact]
    public void PostgreSql_catalog_json_round_trips_typed_predicate_scalars()
    {
        var catalog = new ActionDomainCatalog(
            1,
            new ActionDomainCatalogDefaults(ActionDomainGate.Escalate),
            [
                new ActionDomain(
                    AuthorityKey.From("delivery.release"),
                    "Release deployment.",
                    ActionDomainGate.HumanApproval,
                    [
                        new ActionDomainMatchPredicate(
                            ActionDomainActionKind.Tool,
                            new Dictionary<string, object>
                            {
                                ["tool"] = "jira",
                                ["critical"] = true,
                                ["attempt"] = 2L,
                            }),
                    ]),
            ]);

        var roundTrip = RegistryJson.DeserializeActionDomainCatalog(
            RegistryJson.Serialize(catalog));

        var predicate = Assert.Single(Assert.Single(roundTrip.Domains).Match);
        Assert.Equal("jira", predicate.Attributes["tool"]);
        Assert.Equal(true, predicate.Attributes["critical"]);
        Assert.Equal(2L, predicate.Attributes["attempt"]);
    }

    private static ActionDomainCatalog Catalog(string key) =>
        new(
            1,
            new ActionDomainCatalogDefaults(ActionDomainGate.Escalate),
            [new ActionDomain(AuthorityKey.From(key), "Trusted action.", ActionDomainGate.Decide, [])]);

    private static IActionDomainContractRegistry ContractRegistry()
    {
        var services = new ServiceCollection();
        services.AddHiveActionDomainContracts();
        return services.BuildServiceProvider().GetRequiredService<IActionDomainContractRegistry>();
    }

    private static Hive.Domain.Organization.Configuration.OrganizationConfiguration Configuration(
        string organizationId,
        string positionId,
        string? authorityKey)
    {
        var authority = authorityKey is null
            ? string.Empty
            : $"      authority:{Environment.NewLine}        can_decide: [\"{authorityKey}\"]{Environment.NewLine}";
        var yaml =
            $"organization:{Environment.NewLine}"
            + $"  id: {organizationId}{Environment.NewLine}"
            + $"  name: Test organization{Environment.NewLine}"
            + $"  root_unit: root{Environment.NewLine}"
            + $"  owner:{Environment.NewLine}"
            + $"    type: human{Environment.NewLine}"
            + $"    ref: owner@example.test{Environment.NewLine}"
            + $"units:{Environment.NewLine}"
            + $"  - id: root{Environment.NewLine}"
            + $"    name: Root{Environment.NewLine}"
            + $"    parent: null{Environment.NewLine}"
            + $"    leadership: {positionId}{Environment.NewLine}"
            + $"positions:{Environment.NewLine}"
            + $"  - id: {positionId}{Environment.NewLine}"
            + $"    name: Lead{Environment.NewLine}"
            + $"    unit: root{Environment.NewLine}"
            + $"    reports_to: null{Environment.NewLine}"
            + $"    occupant:{Environment.NewLine}"
            + $"      type: ai-agent{Environment.NewLine}"
            + authority;
        var parsed = new OrganizationConfigurationParser().Parse(yaml, $"{organizationId}/organization.yaml");
        Assert.True(parsed.IsSuccess, string.Join(Environment.NewLine, parsed.Errors));
        return parsed.Configuration!;
    }
}
