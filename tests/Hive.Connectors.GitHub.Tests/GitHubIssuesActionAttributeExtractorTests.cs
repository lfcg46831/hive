using System.Collections.Immutable;
using Hive.Domain.Governance;

namespace Hive.Connectors.GitHub.Tests;

public sealed class GitHubIssuesActionAttributeExtractorTests
{
    public static TheoryData<string, string> Operations => new()
    {
        {
            GitHubIssuesOutboundOperations.Comment,
            GitHubIssuesActionOperationTypes.Comment
        },
        {
            GitHubIssuesOutboundOperations.UpdateState,
            GitHubIssuesActionOperationTypes.StateUpdate
        },
        {
            GitHubIssuesOutboundOperations.UpdateLabels,
            GitHubIssuesActionOperationTypes.LabelsUpdate
        },
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public void Registered_extractors_produce_total_deterministic_facts(
        string tool,
        string operationType)
    {
        var source = new GitHubIssuesActionDomainContractSource();
        var contract = Assert.Single(
            source.ActionContracts,
            candidate => candidate.SelectorValue == tool);
        var registration = Assert.Single(
            source.ActionExtractors,
            candidate => candidate.SelectorValue == tool);
        var request = Request(tool);

        var raw = registration.Extractor.Extract(request);
        var first = ActionAttributeExtractorRunner.Extract(contract, registration, request);
        var repeated = ActionAttributeExtractorRunner.Extract(contract, registration, request);

        Assert.IsAssignableFrom<IImmutableDictionary<string, ActionAttributeValue>>(
            raw.DerivedAttributes);
        Assert.True(first.IsSuccess);
        Assert.True(repeated.IsSuccess);
        Assert.Equal(first.Facts!.Action, repeated.Facts!.Action);
        Assert.Equal(first.Facts.SelectorValue, repeated.Facts.SelectorValue);
        Assert.Equal(
            first.Facts.Attributes.ToArray(),
            repeated.Facts.Attributes.ToArray());
        Assert.Equal(tool, first.Facts!.Attributes["tool"].CanonicalValue);
        Assert.Equal(
            operationType,
            first.Facts.Attributes[GitHubIssuesActionAttributeNames.OperationType].CanonicalValue);
        Assert.Equal(
            GitHubIssuesActionVisibilities.External,
            first.Facts.Attributes[GitHubIssuesActionAttributeNames.Visibility].CanonicalValue);
        Assert.Equal(
            tool == GitHubIssuesOutboundOperations.UpdateState ? 4 : 3,
            first.Facts.Attributes.Count);

        if (tool == GitHubIssuesOutboundOperations.UpdateState)
        {
            Assert.Equal(
                "closed",
                first.Facts.Attributes[GitHubIssuesActionAttributeNames.State].CanonicalValue);
        }
    }

    [Fact]
    public void Extractor_rejects_a_mismatched_selector_without_partial_facts()
    {
        var extractor = new GitHubIssuesActionAttributeExtractor(
            GitHubIssuesOutboundOperations.Comment,
            GitHubIssuesActionOperationTypes.Comment);

        var result = extractor.Extract(Request(GitHubIssuesOutboundOperations.UpdateLabels));

        Assert.False(result.IsSuccess);
        Assert.Empty(result.DerivedAttributes);
        Assert.Equal(ActionAttributeExtractorFailureReason.InvalidInput, result.FailureReason);
    }

    [Fact]
    public void Catalog_validation_accepts_declared_predicates_and_rejects_unknown_facts()
    {
        var source = new GitHubIssuesActionDomainContractSource();
        var binding = new ActionDomainCatalogBinding(
            actionContracts: source.ActionContracts,
            actionExtractors: source.ActionExtractors);
        var valid = Catalog(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["tool"] = GitHubIssuesOutboundOperations.UpdateState,
            [GitHubIssuesActionAttributeNames.OperationType] =
                GitHubIssuesActionOperationTypes.StateUpdate,
            [GitHubIssuesActionAttributeNames.Visibility] =
                GitHubIssuesActionVisibilities.External,
            [GitHubIssuesActionAttributeNames.State] = "closed",
        });
        var invalid = Catalog(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["tool"] = GitHubIssuesOutboundOperations.UpdateState,
            ["repository"] = "acme/payments",
            [GitHubIssuesActionAttributeNames.OperationType] = "delete",
            [GitHubIssuesActionAttributeNames.Visibility] = true,
        });

        var validResult = ActionDomainCatalogValidator.Validate(valid, binding);
        var invalidResult = ActionDomainCatalogValidator.Validate(invalid, binding);

        Assert.True(validResult.IsValid);
        Assert.Empty(validResult.Errors);
        Assert.Contains(
            invalidResult.Errors,
            error => error.Code == "predicate-attribute-not-declared"
                     && error.Path.EndsWith(".repository", StringComparison.Ordinal));
        Assert.Contains(
            invalidResult.Errors,
            error => error.Code == "predicate-attribute-value-not-allowed"
                     && error.Path.EndsWith(".operation_type", StringComparison.Ordinal));
        Assert.Contains(
            invalidResult.Errors,
            error => error.Code == "predicate-attribute-type-mismatch"
                     && error.Path.EndsWith(".visibility", StringComparison.Ordinal));
    }

    [Fact]
    public void Update_state_contract_rejects_values_outside_the_closed_direct_vocabulary()
    {
        var source = new GitHubIssuesActionDomainContractSource();
        var contract = Assert.Single(
            source.ActionContracts,
            candidate => candidate.SelectorValue == GitHubIssuesOutboundOperations.UpdateState);
        var registration = Assert.Single(
            source.ActionExtractors,
            candidate => candidate.SelectorValue == GitHubIssuesOutboundOperations.UpdateState);
        var request = new ActionAttributeExtractionRequest(
            ActionDomainActionKind.Tool,
            GitHubIssuesOutboundOperations.UpdateState,
            new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
            {
                [GitHubIssuesActionAttributeNames.State] =
                    ActionAttributeValue.FromString("merged"),
            });

        var result = ActionAttributeExtractorRunner.Extract(contract, registration, request);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Facts);
        Assert.Equal("direct-attribute-value-not-allowed", result.Failure!.Code);
        Assert.Equal(GitHubIssuesActionAttributeNames.State, result.Failure.Attribute);
    }

    private static ActionAttributeExtractionRequest Request(string tool) =>
        new(
            ActionDomainActionKind.Tool,
            tool,
            tool == GitHubIssuesOutboundOperations.UpdateState
                ? new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
                {
                    [GitHubIssuesActionAttributeNames.State] =
                        ActionAttributeValue.FromString("closed"),
                }
                : null);

    private static ActionDomainCatalog Catalog(IReadOnlyDictionary<string, object> attributes) =>
        new(
            version: 1,
            new ActionDomainCatalogDefaults(ActionDomainGate.Escalate),
            [
                new ActionDomain(
                    AuthorityKey.From("delivery.github-external"),
                    "Govern GitHub external effects.",
                    ActionDomainGate.HumanApproval,
                    [new ActionDomainMatchPredicate(ActionDomainActionKind.Tool, attributes)]),
            ]);
}
