using Hive.Domain.Governance;

namespace Hive.Domain.Connectors;

/// <summary>
/// One outbound connector action bound to the existing provider-neutral authority contract and,
/// exactly when derived attributes are declared, its deterministic extractor.
/// </summary>
public sealed record ConnectorOutboundAction
{
    public ConnectorOutboundAction(
        ActionDomainActionContract actionContract,
        ActionAttributeExtractorRegistration? extractor = null)
    {
        ActionContract = actionContract ?? throw new ArgumentNullException(nameof(actionContract));
        if (actionContract.Action != ActionDomainActionKind.Tool)
        {
            throw new ArgumentException(
                "A connector outbound action must reference a tool action contract.",
                nameof(actionContract));
        }

        if (actionContract.HasDerivedAttributes != (extractor is not null))
        {
            throw new ArgumentException(
                "A connector action must declare exactly one extractor when its contract has derived attributes.",
                nameof(extractor));
        }

        if (extractor is not null
            && (extractor.Action != actionContract.Action
                || !string.Equals(
                    extractor.SelectorValue,
                    actionContract.SelectorValue,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Connector action extractor must target the referenced action contract.",
                nameof(extractor));
        }

        Extractor = extractor;
    }

    public string Name => ActionContract.SelectorValue;

    public ActionDomainActionContract ActionContract { get; }

    public ActionAttributeExtractorRegistration? Extractor { get; }
}
