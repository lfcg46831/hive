using Hive.Domain.Identity;

namespace Hive.Domain.Auditing;

public sealed record JourneyAuditIdempotencyKey
{
    private JourneyAuditIdempotencyKey(string value)
    {
        Value = value;
        AuditEventId = DeterministicGuid.FromName(value);
    }

    public string Value { get; }

    public Guid AuditEventId { get; }

    public static JourneyAuditIdempotencyKey From(
        JourneyAuditStage stage,
        JourneyAuditOutcome outcome,
        OrganizationId organizationId,
        ThreadId threadId,
        MessageId messageId,
        DirectiveId? directiveId = null,
        PositionId? positionId = null,
        string? discriminator = null)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(messageId);

        var normalizedStage = RequireDefined(stage, nameof(stage));
        var normalizedOutcome = RequireDefined(outcome, nameof(outcome));
        var normalizedDiscriminator = OptionalText(discriminator, nameof(discriminator));

        return new JourneyAuditIdempotencyKey(string.Join(
            "|",
            "journey-audit:v1",
            $"stage={normalizedStage}",
            $"outcome={normalizedOutcome}",
            $"organization={organizationId.Value}",
            $"thread={threadId.Value:N}",
            $"directive={directiveId?.Value.ToString("N") ?? "-"}",
            $"message={messageId.Value:N}",
            $"position={positionId?.Value ?? "-"}",
            $"discriminator={normalizedDiscriminator ?? "-"}"));
    }

    private static TEnum RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"Unknown {typeof(TEnum).Name}.");
        }

        return value;
    }

    private static string? OptionalText(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Value cannot contain leading or trailing whitespace.", parameterName);
        }

        return value;
    }
}
