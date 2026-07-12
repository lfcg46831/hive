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
    Authorized = 2,
    Consumed = 3,
    Denied = 4,
    Expired = 5,
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
        : this(
            id,
            fingerprint,
            kind,
            selector,
            canonicalPayload,
            canonicalFacts,
            correlationId,
            organizationId,
            positionId,
            threadId,
            sourceMessageId,
            directiveId,
            parentDirectiveId,
            code,
            retainedAt,
            approvalPolicies,
            governanceMessages,
            RetainedActionState.Retained,
            authorizationGrant: null,
            authorizationDenial: null,
            stateChangedAt: null,
            reEscalationCode: null)
    {
    }

    private PersistedRetainedAction(
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
        IEnumerable<ApprovalPolicyRef>? approvalPolicies,
        IEnumerable<OrgMessage>? governanceMessages,
        RetainedActionState state,
        AuthorizationGrant? authorizationGrant,
        AuthorizationDenial? authorizationDenial,
        DateTimeOffset? stateChangedAt,
        string? reEscalationCode)
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
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        State = state;
        ApprovalPolicies = (approvalPolicies ?? [])
            .Select(policy => policy ?? throw new ArgumentException("Approval policies cannot contain null.", nameof(approvalPolicies)))
            .Distinct()
            .OrderBy(policy => policy.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        GovernanceMessages = ToMessages(governanceMessages);
        AuthorizationGrant = authorizationGrant;
        AuthorizationDenial = authorizationDenial;
        StateChangedAt = stateChangedAt;
        ReEscalationCode = reEscalationCode;
        ValidateLifecycle();
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
    public AuthorizationGrant? AuthorizationGrant { get; }
    public AuthorizationDenial? AuthorizationDenial { get; }
    public DateTimeOffset? StateChangedAt { get; }
    public string? ReEscalationCode { get; }
    public AuthorizationGrant? ActiveGrant =>
        State == RetainedActionState.Authorized ? AuthorizationGrant : null;

    public PersistedRetainedAction Authorize(AuthorizationGrant grant, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return Copy(RetainedActionState.Authorized, grant, null, occurredAt, null);
    }

    public PersistedRetainedAction Deny(AuthorizationDenial denial, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(denial);
        return Copy(RetainedActionState.Denied, null, denial, occurredAt, null);
    }

    public PersistedRetainedAction Consume(DateTimeOffset occurredAt) =>
        Copy(RetainedActionState.Consumed, AuthorizationGrant, null, occurredAt, null);

    public PersistedRetainedAction Expire(DateTimeOffset occurredAt, string reEscalationCode) =>
        Copy(
            RetainedActionState.Expired,
            AuthorizationGrant,
            null,
            occurredAt,
            CommandText.RequireContent(reEscalationCode, nameof(reEscalationCode)));

    public PersistedRetainedAction ReturnToRetained(DateTimeOffset occurredAt, string reEscalationCode) =>
        Copy(
            RetainedActionState.Retained,
            AuthorizationGrant,
            null,
            occurredAt,
            CommandText.RequireContent(reEscalationCode, nameof(reEscalationCode)));

    public static PersistedRetainedAction Restore(
        PersistedRetainedAction retained,
        RetainedActionState state,
        AuthorizationGrant? authorizationGrant,
        AuthorizationDenial? authorizationDenial,
        DateTimeOffset? stateChangedAt,
        string? reEscalationCode)
    {
        ArgumentNullException.ThrowIfNull(retained);
        return retained.Copy(
            state,
            authorizationGrant,
            authorizationDenial,
            stateChangedAt,
            reEscalationCode);
    }

    private PersistedRetainedAction Copy(
        RetainedActionState state,
        AuthorizationGrant? authorizationGrant,
        AuthorizationDenial? authorizationDenial,
        DateTimeOffset? stateChangedAt,
        string? reEscalationCode) =>
        new(
            Id,
            Fingerprint,
            Kind,
            Selector,
            CanonicalPayload,
            CanonicalFacts,
            CorrelationId,
            OrganizationId,
            PositionId,
            ThreadId,
            SourceMessageId,
            DirectiveId,
            ParentDirectiveId,
            Code,
            RetainedAt,
            ApprovalPolicies,
            GovernanceMessages,
            state,
            authorizationGrant,
            authorizationDenial,
            stateChangedAt,
            reEscalationCode);

    private void ValidateLifecycle()
    {
        if (AuthorizationGrant is not null
            && (AuthorizationGrant.RetainedActionId != Id
                || AuthorizationGrant.OrganizationId != OrganizationId
                || AuthorizationGrant.Thread != ThreadId
                || AuthorizationGrant.To is not PositionEndpointRef destination
                || destination.PositionId != PositionId))
        {
            throw new ArgumentException("Authorization grant does not target the retained action.");
        }

        if (AuthorizationDenial is not null
            && (AuthorizationDenial.RetainedActionId != Id
                || AuthorizationDenial.OrganizationId != OrganizationId
                || AuthorizationDenial.Thread != ThreadId
                || AuthorizationDenial.To is not PositionEndpointRef denialDestination
                || denialDestination.PositionId != PositionId))
        {
            throw new ArgumentException("Authorization denial does not target the retained action.");
        }

        var valid = State switch
        {
            RetainedActionState.Retained => AuthorizationDenial is null
                && ((AuthorizationGrant is null && StateChangedAt is null && ReEscalationCode is null)
                    || (AuthorizationGrant is not null && StateChangedAt is not null && ReEscalationCode is not null)),
            RetainedActionState.Authorized or RetainedActionState.Consumed =>
                AuthorizationGrant is not null
                && AuthorizationDenial is null
                && StateChangedAt is not null
                && ReEscalationCode is null,
            RetainedActionState.Denied =>
                AuthorizationGrant is null
                && AuthorizationDenial is not null
                && StateChangedAt is not null
                && ReEscalationCode is null,
            RetainedActionState.Expired =>
                AuthorizationGrant is not null
                && AuthorizationDenial is null
                && StateChangedAt is not null
                && ReEscalationCode is not null,
            _ => false,
        };

        if (!valid)
        {
            throw new ArgumentException("Retained action lifecycle fields are inconsistent with its state.");
        }
    }

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
