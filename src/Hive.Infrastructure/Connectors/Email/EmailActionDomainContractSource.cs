using Hive.Domain.Governance;
using Hive.Infrastructure.Governance;

namespace Hive.Infrastructure.Connectors.Email;

internal sealed class EmailActionDomainContractSource : IActionDomainContractSource
{
    public IReadOnlyList<ActionDomainActionContract> ActionContracts { get; } =
    [
        ActionDomainActionContract.ForTool(
            "email.send",
            [
                ActionAttributeDefinition.Derived(
                    "recipient_scope",
                    ActionAttributeValueKind.String,
                    [
                        ActionAttributeValue.FromString("internal"),
                        ActionAttributeValue.FromString("external"),
                    ]),
            ]),
    ];

    public IReadOnlyList<ActionAttributeExtractorRegistration> ActionExtractors { get; } =
    [
        ActionAttributeExtractorRegistration.ForTool(
            "email.send",
            ConservativeEmailRecipientScopeExtractor.Instance),
    ];

    private sealed class ConservativeEmailRecipientScopeExtractor : IActionAttributeExtractor
    {
        public static ConservativeEmailRecipientScopeExtractor Instance { get; } = new();

        // Until the email connector owns a validated address-book snapshot, every recipient is
        // classified as external. This can retain an internal email but cannot bypass governance.
        public ActionAttributeExtractorOutput Extract(ActionAttributeExtractionRequest request) =>
            ActionAttributeExtractorOutput.Success(
                new Dictionary<string, ActionAttributeValue>(StringComparer.Ordinal)
                {
                    ["recipient_scope"] = ActionAttributeValue.FromString("external"),
                });
    }
}
