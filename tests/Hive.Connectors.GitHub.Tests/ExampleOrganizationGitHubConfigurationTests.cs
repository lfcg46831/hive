using Hive.Domain.Governance;
using Hive.Infrastructure.Governance;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Connectors.GitHub.Tests;

public sealed class ExampleOrganizationGitHubConfigurationTests
{
    private const string ExampleRepository = "acme/payments";
    private static readonly AuthorityKey TriageAuthority =
        AuthorityKey.From("delivery.bug-triage");
    private static readonly AuthorityKey CommentDomain =
        AuthorityKey.From("delivery.github-issue-comment");
    private static readonly AuthorityKey StateDomain =
        AuthorityKey.From("delivery.github-issue-state");

    [Fact]
    public async Task Example_configuration_imports_with_the_installed_GitHub_contracts()
    {
        var fixture = LoadExample();

        Assert.True(
            fixture.Validation.IsValid,
            string.Join(Environment.NewLine, fixture.Validation.Errors.Select(error =>
                $"{error.Path}: {error.Code}: {error.Message}")));

        var comment = fixture.Catalog.Domains.Single(domain => domain.Key == CommentDomain);
        Assert.Equal(ActionDomainGate.Decide, comment.Gate);
        AssertPredicate(
            Assert.Single(comment.Match),
            GitHubIssuesOutboundOperations.Comment,
            GitHubIssuesActionOperationTypes.Comment);

        var state = fixture.Catalog.Domains.Single(domain => domain.Key == StateDomain);
        Assert.Equal(ActionDomainGate.HumanApproval, state.Gate);
        Assert.Equal(2, state.Match.Count);
        Assert.Equal(
            ["closed", "open"],
            state.Match
                .Select(predicate => Assert.IsType<string>(predicate.Attributes["state"]))
                .OrderBy(value => value, StringComparer.Ordinal));
        Assert.All(state.Match, predicate => AssertPredicate(
            predicate,
            GitHubIssuesOutboundOperations.UpdateState,
            GitHubIssuesActionOperationTypes.StateUpdate,
            Assert.IsType<string>(predicate.Attributes[GitHubIssuesActionAttributeNames.State])));

        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationDirectoryImporter(
            new OrganizationConfigurationParser(),
            new OrganizationConfigurationImporter(registry),
            fixture.ContractRegistry);

        var results = await importer.ImportAsync(Path.Combine(
            RepositoryRoot,
            "config",
            "organizations"));

        var imported = Assert.Single(results);
        Assert.Equal(OrganizationImportStatus.Applied, imported.Status);
        Assert.Contains(
            imported.Snapshot!.ActionDomainCatalog.Value.Domains,
            domain => domain.Key == StateDomain);
    }

    [Fact]
    public void Example_triage_position_allowlists_only_comment_and_state_for_the_example_repository()
    {
        var triage = LoadExample().Triage;

        Assert.Equal(
            [GitHubIssuesOutboundOperations.Comment, GitHubIssuesOutboundOperations.UpdateState],
            triage.Occupant.Tools.Select(tool => tool.Connector));
        Assert.All(
            triage.Occupant.Tools,
            tool => Assert.Equal([ExampleRepository], tool.Scope));

        var authority = Assert.IsType<Hive.Domain.Organization.Configuration.AuthorityConfiguration>(
            triage.Occupant.Authority);
        Assert.Equal([TriageAuthority], authority.CanDecide);
        var stateOverride = Assert.Single(authority.Overrides);
        Assert.Equal(StateDomain, stateOverride.Key);
        Assert.Equal(ActionDomainGate.HumanApproval, stateOverride.Gate);
        Assert.Equal("delivery-lead", stateOverride.Approver);
    }

    [Fact]
    public void Example_comment_is_allowed_only_under_the_triage_authority()
    {
        var fixture = LoadExample();
        var facts = ExtractFacts(fixture.ContractSource, GitHubIssuesOutboundOperations.Comment);

        var declared = ActionGateResolver.Resolve(
            fixture.Catalog,
            fixture.Authority,
            facts,
            ActingUnderDeclaration.Declared(TriageAuthority));
        var missing = ActionGateResolver.Resolve(
            fixture.Catalog,
            fixture.Authority,
            facts,
            ActingUnderDeclaration.Missing());

        Assert.Equal(ActionGateOutcome.Allowed, declared.Outcome);
        Assert.Equal(TriageAuthority, declared.AllowedAuthorityKey);
        Assert.Equal(CommentDomain, Assert.Single(declared.Matches).Key);
        Assert.Equal(ActionGateOutcome.EscalationRequired, missing.Outcome);
        Assert.Equal(CommentDomain, Assert.Single(missing.Matches).Key);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("closed")]
    public void Example_state_changes_require_delivery_lead_approval(string state)
    {
        var fixture = LoadExample();
        var facts = ExtractFacts(
            fixture.ContractSource,
            GitHubIssuesOutboundOperations.UpdateState,
            state);

        var result = ActionGateResolver.Resolve(
            fixture.Catalog,
            fixture.Authority,
            facts,
            ActingUnderDeclaration.Declared(TriageAuthority));

        Assert.Equal(ActionGateOutcome.HumanApprovalRequired, result.Outcome);
        Assert.Equal(StateDomain, Assert.Single(result.Matches).Key);
        var approval = Assert.Single(result.RequiredApprovals);
        Assert.Equal("delivery-lead", approval.Approver);
        Assert.Equal([StateDomain], approval.AuthorityKeys);
    }

    private static ExampleFixture LoadExample()
    {
        var directory = Path.Combine(
            RepositoryRoot,
            "config",
            "organizations",
            "acme-delivery");
        var organizationResult = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(directory, "organization.yaml"));
        var catalogResult = new ActionDomainCatalogParser().ParseFile(
            Path.Combine(directory, "action-domains.yaml"));

        Assert.True(
            organizationResult.IsSuccess,
            string.Join(Environment.NewLine, organizationResult.Errors));
        Assert.True(
            catalogResult.IsSuccess,
            string.Join(Environment.NewLine, catalogResult.Errors));

        var contractSource = new GitHubIssuesActionDomainContractSource();
        var services = new ServiceCollection();
        services.AddHiveActionDomainContracts();
        services.AddSingleton<IActionDomainContractSource>(contractSource);
        using var provider = services.BuildServiceProvider();
        var contractRegistry = provider.GetRequiredService<IActionDomainContractRegistry>();
        var organization = organizationResult.Configuration!;
        var catalog = catalogResult.Catalog!;
        var validation = ActionDomainCatalogValidator.Validate(
            catalog,
            OrganizationActionDomainBinding.Create(organization, contractRegistry));
        var triage = organization.Positions.Single(position => position.Id.Value == "bug-triage");
        var authority = triage.Occupant.Authority!;

        return new ExampleFixture(
            catalog,
            triage,
            new ActionDomainAuthorityBinding(
                "positions[bug-triage].authority",
                authority.CanDecide,
                authority.Overrides.Select(item => new ActionDomainAuthorityOverride(
                    item.Key,
                    item.Gate,
                    item.Approver)).ToArray()),
            contractSource,
            contractRegistry,
            validation);
    }

    private static ActionFacts ExtractFacts(
        GitHubIssuesActionDomainContractSource source,
        string tool,
        string? state = null)
    {
        var contract = source.ActionContracts.Single(item => item.SelectorValue == tool);
        var extractor = source.ActionExtractors.Single(item => item.SelectorValue == tool);
        var directAttributes = state is null
            ? null
            : new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
            {
                [GitHubIssuesActionAttributeNames.State] =
                    ActionAttributeValue.FromString(state),
            };
        var result = ActionAttributeExtractorRunner.Extract(
            contract,
            extractor,
            new ActionAttributeExtractionRequest(
                ActionDomainActionKind.Tool,
                tool,
                directAttributes));

        Assert.True(result.IsSuccess, result.Failure?.Code);
        return result.Facts!;
    }

    private static void AssertPredicate(
        ActionDomainMatchPredicate predicate,
        string tool,
        string operationType,
        string? state = null)
    {
        Assert.Equal(ActionDomainActionKind.Tool, predicate.Action);
        var expected = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["tool"] = tool,
            [GitHubIssuesActionAttributeNames.OperationType] = operationType,
            [GitHubIssuesActionAttributeNames.Visibility] =
                GitHubIssuesActionVisibilities.External,
        };
        if (state is not null)
        {
            expected[GitHubIssuesActionAttributeNames.State] = state;
        }

        Assert.Equal(
            expected.OrderBy(item => item.Key, StringComparer.Ordinal),
            predicate.Attributes.OrderBy(item => item.Key, StringComparer.Ordinal));
    }

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate the Hive repository root.");
        }
    }

    private sealed record ExampleFixture(
        ActionDomainCatalog Catalog,
        Hive.Domain.Organization.Configuration.PositionConfiguration Triage,
        ActionDomainAuthorityBinding Authority,
        GitHubIssuesActionDomainContractSource ContractSource,
        IActionDomainContractRegistry ContractRegistry,
        ActionDomainCatalogValidationResult Validation);
}
