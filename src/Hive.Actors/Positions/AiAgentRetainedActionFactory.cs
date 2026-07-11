using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

internal static class AiAgentRetainedActionFactory
{
    public static RetainAction Create(AiAgentActionGateResult result, DateTimeOffset retainedAt)
    {
        ArgumentNullException.ThrowIfNull(result);
        var retention = result.Retention
            ?? throw new ArgumentException("A retained gate result must carry a retention intent.", nameof(result));

        var material = RetainedActionFingerprintFactory.Create(retention.Candidate, result.Facts);
        var id = RetainedActionId.From(DeterministicGuid.FromName(
            $"retained-action|{retention.OrganizationId.Value}|{retention.PositionId.Value}|{retention.CorrelationId}|{material.Fingerprint.Value}"));
        var policies = retention.GovernanceMessages
            .OfType<ApprovalRequest>()
            .Select(request => request.Policy);

        return new RetainAction(new PersistedRetainedAction(
            id,
            material.Fingerprint,
            retention.Candidate.Kind == ActionDomainActionKind.Tool
                ? RetainedActionKind.Tool
                : RetainedActionKind.OrganizationalMessage,
            retention.Candidate.Selector,
            material.CanonicalPayload,
            material.CanonicalFacts,
            retention.CorrelationId,
            retention.OrganizationId,
            retention.PositionId,
            retention.ThreadId,
            retention.SourceMessageId,
            retention.DirectiveId,
            retention.ParentDirectiveId,
            retention.Code,
            retainedAt,
            policies,
            retention.GovernanceMessages));
    }

}
