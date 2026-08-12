using System.Security.Cryptography;
using System.Text;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Actors.Positions;

internal static class OccupantAbsenceEscalationIdentity
{
    public static MessageId For(PositionEntityId entityId, MessageId sourceMessageId)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        ArgumentNullException.ThrowIfNull(sourceMessageId);

        var input = $"hive:occupant-absence:escalation\n{entityId.Value}\n{sourceMessageId.Value:D}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return MessageId.From(new Guid(hash.AsSpan(0, 16)));
    }
}

internal sealed record InitializeOccupantAbsenceEscalation(MessageId MessageId);

internal sealed record OccupantAbsenceEscalationTargetResolved(
    MessageId MessageId,
    EndpointRef? Target);

internal sealed record OccupantAbsenceEscalationTargetResolutionFailed(
    MessageId MessageId,
    Exception Cause);

internal sealed record OccupantAbsenceEscalationValidationCompleted(
    MessageId SourceMessageId,
    Escalation Escalation,
    ValidationResult Validation);

internal sealed record OccupantAbsenceEscalationValidationFailed(
    MessageId SourceMessageId,
    Exception Cause);
