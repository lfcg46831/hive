using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

/// <summary>
/// Public approval context and the principal-specific capability to decide it.
/// </summary>
public sealed record InboxApprovalMetadata
{
    public InboxApprovalMetadata(
        Guid requestId,
        string action,
        string policyRef,
        InboxApprovalState state,
        bool canDecide,
        Guid? decisionMessageId = null,
        DateTimeOffset? decidedAtUtc = null)
    {
        RequestId = InboxContractGuards.MessageIdentifier(requestId, nameof(requestId));
        Action = InboxContractGuards.DisplayText(action, nameof(action));
        PolicyRef = InboxContractGuards.Identifier(policyRef, nameof(policyRef));
        State = InboxContractGuards.DefinedEnum(state, nameof(state));
        DecisionMessageId = InboxContractGuards.OptionalMessageIdentifier(
            decisionMessageId,
            nameof(decisionMessageId));
        DecidedAtUtc = InboxContractGuards.OptionalUtcTimestamp(
            decidedAtUtc,
            nameof(decidedAtUtc));

        var isDecided = State is InboxApprovalState.Approved or InboxApprovalState.Rejected;
        if (isDecided != (DecisionMessageId is not null && DecidedAtUtc is not null))
        {
            throw new ArgumentException(
                "Approved or rejected metadata requires both a decision message and decision timestamp; other states cannot contain them.",
                nameof(decisionMessageId));
        }

        if (canDecide && State != InboxApprovalState.Pending)
        {
            throw new ArgumentException(
                "Only a pending approval can expose the capability to decide.",
                nameof(canDecide));
        }

        CanDecide = canDecide;
    }

    [JsonPropertyName("request_id")]
    public Guid RequestId { get; }

    [JsonPropertyName("action")]
    public string Action { get; }

    [JsonPropertyName("policy_ref")]
    public string PolicyRef { get; }

    [JsonPropertyName("state")]
    public InboxApprovalState State { get; }

    [JsonPropertyName("can_decide")]
    public bool CanDecide { get; }

    [JsonPropertyName("decision_message_id")]
    public Guid? DecisionMessageId { get; }

    [JsonPropertyName("decided_at_utc")]
    public DateTimeOffset? DecidedAtUtc { get; }
}
