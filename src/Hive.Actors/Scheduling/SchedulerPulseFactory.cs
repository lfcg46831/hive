using System.Security.Cryptography;
using System.Text;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Scheduling;
using Hive.Infrastructure.Scheduling;

namespace Hive.Actors.Scheduling;

internal static class SchedulerPulseFactory
{
    private const int PulseSchemaVersion = 1;
    private const string MessageNamespace = "hive:scheduler:pulse:message";
    private const string ThreadNamespace = "hive:scheduler:pulse:thread";

    public static Pulse Build(
        SchedulerScheduleMaterialization materialization,
        DateTimeOffset firedAtUtc,
        PulseIdempotencyKey idempotencyKey)
    {
        var delivery = BuildDeliveryRecord(materialization, firedAtUtc, idempotencyKey);

        return new Pulse(
            delivery.MessageId,
            materialization.Key.Organization,
            new SystemEndpointRef(SystemEndpointKind.Scheduler),
            new PositionEndpointRef(materialization.Key.Position),
            delivery.ThreadId,
            materialization.Definition.Priority,
            PulseSchemaVersion,
            firedAtUtc,
            deadline: null,
            materialization.Definition.Id.Value,
            materialization.Definition.Payload);
    }

    public static SchedulerPulseDeliveryRecord BuildDeliveryRecord(
        SchedulerScheduleMaterialization materialization,
        DateTimeOffset occurredAtUtc,
        PulseIdempotencyKey idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        ArgumentNullException.ThrowIfNull(idempotencyKey);

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Scheduler pulse timestamps must be expressed as UTC offsets.",
                nameof(occurredAtUtc));
        }

        if (materialization.Key.Organization != idempotencyKey.Organization
            || materialization.Key.Position != idempotencyKey.Position
            || materialization.Key.Schedule != idempotencyKey.Schedule)
        {
            throw new ArgumentException(
                "Pulse idempotency key must match the scheduler materialization identity.",
                nameof(idempotencyKey));
        }

        return new SchedulerPulseDeliveryRecord(
            idempotencyKey,
            MessageId.From(DeterministicGuid(MessageNamespace, idempotencyKey.Value)),
            ThreadId.From(DeterministicGuid(ThreadNamespace, idempotencyKey.Value)),
            occurredAtUtc);
    }

    private static Guid DeterministicGuid(string namespaceName, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(namespaceName + "\n" + value);
        var hash = SHA256.HashData(bytes);
        return new Guid(hash.AsSpan(0, 16));
    }
}
