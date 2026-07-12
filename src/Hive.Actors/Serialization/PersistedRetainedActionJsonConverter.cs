using System.Text.Json;
using System.Text.Json.Serialization;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Actors.Serialization;

internal sealed class PersistedRetainedActionJsonConverter : JsonConverter<PersistedRetainedAction>
{
    public override PersistedRetainedAction Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var dto = JsonSerializer.Deserialize<Data>(ref reader, options)
            ?? throw new JsonException("Persisted retained action payload deserialized to null.");
        var retained = new PersistedRetainedAction(
            dto.Id!,
            dto.Fingerprint!,
            dto.Kind,
            dto.Selector!,
            dto.CanonicalPayload!,
            dto.CanonicalFacts!,
            dto.CorrelationId!,
            dto.OrganizationId!,
            dto.PositionId!,
            dto.ThreadId!,
            dto.SourceMessageId!,
            dto.DirectiveId!,
            dto.ParentDirectiveId,
            dto.Code!,
            dto.RetainedAt,
            dto.ApprovalPolicies,
            dto.GovernanceMessages);
        return PersistedRetainedAction.Restore(
            retained,
            dto.State ?? RetainedActionState.Retained,
            dto.AuthorizationGrant,
            dto.AuthorizationDenial,
            dto.StateChangedAt,
            dto.ReEscalationCode);
    }

    public override void Write(
        Utf8JsonWriter writer,
        PersistedRetainedAction value,
        JsonSerializerOptions options)
    {
        var dto = new Data
        {
            Id = value.Id,
            Fingerprint = value.Fingerprint,
            Kind = value.Kind,
            Selector = value.Selector,
            CanonicalPayload = value.CanonicalPayload,
            CanonicalFacts = value.CanonicalFacts,
            CorrelationId = value.CorrelationId,
            OrganizationId = value.OrganizationId,
            PositionId = value.PositionId,
            ThreadId = value.ThreadId,
            SourceMessageId = value.SourceMessageId,
            DirectiveId = value.DirectiveId,
            ParentDirectiveId = value.ParentDirectiveId,
            Code = value.Code,
            RetainedAt = value.RetainedAt,
            ApprovalPolicies = value.ApprovalPolicies.ToList(),
            GovernanceMessages = value.GovernanceMessages.ToList(),
            State = value.State == RetainedActionState.Retained
                && value.AuthorizationGrant is null
                ? null
                : value.State,
            AuthorizationGrant = value.AuthorizationGrant,
            AuthorizationDenial = value.AuthorizationDenial,
            StateChangedAt = value.StateChangedAt,
            ReEscalationCode = value.ReEscalationCode,
        };
        JsonSerializer.Serialize(writer, dto, options);
    }

    private sealed class Data
    {
        public RetainedActionId? Id { get; set; }
        public ActionFingerprint? Fingerprint { get; set; }
        public RetainedActionKind Kind { get; set; }
        public string? Selector { get; set; }
        public string? CanonicalPayload { get; set; }
        public string? CanonicalFacts { get; set; }
        public string? CorrelationId { get; set; }
        public OrganizationId? OrganizationId { get; set; }
        public PositionId? PositionId { get; set; }
        public ThreadId? ThreadId { get; set; }
        public MessageId? SourceMessageId { get; set; }
        public DirectiveId? DirectiveId { get; set; }
        public DirectiveId? ParentDirectiveId { get; set; }
        public string? Code { get; set; }
        public DateTimeOffset RetainedAt { get; set; }
        public List<ApprovalPolicyRef>? ApprovalPolicies { get; set; }
        public List<OrgMessage>? GovernanceMessages { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RetainedActionState? State { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AuthorizationGrant? AuthorizationGrant { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AuthorizationDenial? AuthorizationDenial { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? StateChangedAt { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ReEscalationCode { get; set; }
    }
}
