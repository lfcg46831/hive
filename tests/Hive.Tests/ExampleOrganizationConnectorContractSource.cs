using Hive.Domain.Governance;
using Hive.Infrastructure.Governance;

namespace Hive.Tests;

/// <summary>
/// Generic test double for connector contracts required by the tracked example organization.
/// The concrete plugin project independently proves these declarations against its real source.
/// </summary>
internal sealed class ExampleOrganizationConnectorContractSource : IActionDomainContractSource
{
    public static ExampleOrganizationConnectorContractSource Instance { get; } = new();

    public IReadOnlyList<ActionDomainActionContract> ActionContracts { get; } =
    [
        ActionDomainActionContract.ForTool(
            "issues.comment",
            [
                Derived("operation_type", "comment"),
                Derived("visibility", "external"),
            ]),
        ActionDomainActionContract.ForTool(
            "issues.update-state",
            [
                Derived("operation_type", "state-update"),
                Derived("visibility", "external"),
                ActionAttributeDefinition.Direct(
                    "state",
                    ActionAttributeValueKind.String,
                    [
                        ActionAttributeValue.FromString("open"),
                        ActionAttributeValue.FromString("closed"),
                    ]),
            ]),
    ];

    public IReadOnlyList<ActionAttributeExtractorRegistration> ActionExtractors { get; } =
    [
        ActionAttributeExtractorRegistration.ForTool(
            "issues.comment",
            new ExampleOrganizationConnectorExtractor("issues.comment", "comment")),
        ActionAttributeExtractorRegistration.ForTool(
            "issues.update-state",
            new ExampleOrganizationConnectorExtractor("issues.update-state", "state-update")),
    ];

    private static ActionAttributeDefinition Derived(string name, string value) =>
        ActionAttributeDefinition.Derived(
            name,
            ActionAttributeValueKind.String,
            [ActionAttributeValue.FromString(value)]);

    private sealed class ExampleOrganizationConnectorExtractor : IActionAttributeExtractor
    {
        private readonly string _selector;
        private readonly string _operationType;

        public ExampleOrganizationConnectorExtractor(string selector, string operationType)
        {
            _selector = selector;
            _operationType = operationType;
        }

        public ActionAttributeExtractorOutput Extract(ActionAttributeExtractionRequest request) =>
            request.Action == ActionDomainActionKind.Tool
            && string.Equals(request.SelectorValue, _selector, StringComparison.Ordinal)
                ? ActionAttributeExtractorOutput.Success(
                    new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
                    {
                        ["operation_type"] = ActionAttributeValue.FromString(_operationType),
                        ["visibility"] = ActionAttributeValue.FromString("external"),
                    })
                : ActionAttributeExtractorOutput.Failure(
                    ActionAttributeExtractorFailureReason.InvalidInput);
    }
}
