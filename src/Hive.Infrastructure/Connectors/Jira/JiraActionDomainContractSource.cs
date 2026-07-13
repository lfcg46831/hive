using Hive.Domain.Governance;
using Hive.Infrastructure.Governance;

namespace Hive.Infrastructure.Connectors.Jira;

internal sealed class JiraActionDomainContractSource : IActionDomainContractSource
{
    public IReadOnlyList<ActionDomainActionContract> ActionContracts { get; } =
    [
        ActionDomainActionContract.ForTool("jira"),
    ];

    public IReadOnlyList<ActionAttributeExtractorRegistration> ActionExtractors { get; } = [];
}
