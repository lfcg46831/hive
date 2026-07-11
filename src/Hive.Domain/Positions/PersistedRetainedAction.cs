using System.Collections.Immutable;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

public enum RetainedActionKind
{
    Tool = 1,
    OrganizationalMessage = 2,
}

public enum RetainedActionState
{
    Retained = 1,
}

public sealed record PersistedRetainedAction
{
    public PersistedRetainedAction(
        RetainedActionId id,
        ActionFingerprint fingerprint,
        RetainedActionKind kind,
        string selector,
        string canonicalPayload,
        string canonicalFacts,
        string correlationId,
        OrganizationId organizationId,
        PositionId positionId,
        ThreadId threadId,
        MessageId sourceMessageId,
        DirectiveId directiveId,
        DirectiveId? parentDirectiveId,
        string code,
        DateTimeOffset retainedAt,
        IEnumerable<ApprovalPolicyRef>? approvalPolicies = null,
        IEnumerable<OrgMessage>? governanceMessages = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Id = id ?? throw new ArgumentNullException(nameof(id));
        Fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        Kind = kind;
        Selector = CommandText.RequireContent(selector, nameof(selector));
        CanonicalPayload = CommandText.RequireContent(canonicalPayload, nameof(canonicalPayload));
        CanonicalFacts = CommandText.RequireContent(canonicalFacts, nameof(canonicalFacts));
        CorrelationId = CommandText.RequireContent(correlationId, nameof(correlationId));
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        PositionId = positionId ?? throw new ArgumentNullException(nameof(positionId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        SourceMessageId = sourceMessageId ?? throw new ArgumentNullException(nameof(sourceMessageId));
        DirectiveId = directiveId ?? throw new ArgumentNullException(nameof(directiveId));
        ParentDirectiveId = parentDirectiveId;
        Code = CommandText.RequireContent(code, nameof(code));
        RetainedAt = retainedAt;
        State = RetainedActionState.Retained;
        ApprovalPolicies = (approvalPolicies ?? [])
            .Select(policy => policy ?? throw new ArgumentException("Approval policies cannot contain null.", nameof(approvalPolicies)))
            .Distinct()
            .OrderBy(policy => policy.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        GovernanceMessages = ToMessages(governanceMessages);
    }

    public RetainedActionId Id { get; }
    public ActionFingerprint Fingerprint { get; }
    public RetainedActionKind Kind { get; }
    public string Selector { get; }
    public string CanonicalPayload { get; }
    public string CanonicalFacts { get; }
    public string CorrelationId { get; }
    public OrganizationId OrganizationId { get; }
    public PositionId PositionId { get; }
    public ThreadId ThreadId { get; }
    public MessageId SourceMessageId { get; }
    public DirectiveId DirectiveId { get; }
    public DirectiveId? ParentDirectiveId { get; }
    public string Code { get; }
    public DateTimeOffset RetainedAt { get; }
    public RetainedActionState State { get; }
    public ImmutableArray<ApprovalPolicyRef> ApprovalPolicies { get; }
    public ImmutableArray<OrgMessage> GovernanceMessages { get; }

    private static ImmutableArray<OrgMessage> ToMessages(IEnumerable<OrgMessage>? messages)
    {
        if (messages is null)
        {
            return ImmutableArray<OrgMessage>.Empty;
        }

        var result = messages.ToImmutableArray();
        if (result.Any(message => message is null))
        {
            throw new ArgumentException("Governance messages cannot contain null.", nameof(messages));
        }

        return result;
    }
}
