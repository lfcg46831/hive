using Hive.Domain.Governance;
using Hive.Domain.Messaging;

namespace Hive.Infrastructure.Governance;

internal sealed class PlatformMessageActionDomainContractSource : IActionDomainContractSource
{
    public IReadOnlyList<ActionDomainActionContract> ActionContracts { get; } =
    [
        ActionDomainActionContract.ForOrganizationalMessage(nameof(Report)),
        ActionDomainActionContract.ForOrganizationalMessage(nameof(Escalation)),
        ActionDomainActionContract.ForOrganizationalMessage(nameof(Directive)),
        ActionDomainActionContract.ForOrganizationalMessage(nameof(ApprovalRequest)),
        ActionDomainActionContract.ForOrganizationalMessage(nameof(ApprovalDecision)),
        AuthorizationGrantAuthority.ActionContract,
    ];

    public IReadOnlyList<ActionAttributeExtractorRegistration> ActionExtractors { get; } = [];
}
