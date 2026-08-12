using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

internal sealed record OccupantResponseReminderPlan(
    int Index,
    OccupantReminderId ReminderId,
    MessageId TriggerMessageId,
    DateTimeOffset ScheduledForUtc);

internal sealed record OccupantResponsePolicyPlan(
    IReadOnlyList<OccupantResponseReminderPlan> Reminders,
    DateTimeOffset TimeoutAtUtc)
{
    public static OccupantResponsePolicyPlan Create(
        PositionEntityId entityId,
        OrgMessage message,
        DateTimeOffset dispatchedAtUtc,
        OccupantResponsePolicyRuntimeConfiguration policy)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(policy);
        RequireUtc(dispatchedAtUtc, nameof(dispatchedAtUtc));

        var reminders = new List<OccupantResponseReminderPlan>(policy.ReminderMaxCount);
        for (var index = 1; index <= policy.ReminderMaxCount; index++)
        {
            var elapsed = TimeSpan.FromTicks(checked(policy.ReminderInterval.Ticks * index));
            var scheduledFor = AddPolicyDuration(
                dispatchedAtUtc,
                elapsed,
                message.Priority == Priority.Critical,
                policy);
            var identity = $"{entityId.Value}\n{message.Id.Value:D}\n{index.ToString(CultureInfo.InvariantCulture)}\n{scheduledFor.UtcTicks.ToString(CultureInfo.InvariantCulture)}";
            reminders.Add(new OccupantResponseReminderPlan(
                index,
                OccupantReminderId.From(DeterministicGuid("hive:occupant-response:reminder", identity)),
                MessageId.From(DeterministicGuid("hive:occupant-response:event-trigger", identity)),
                scheduledFor));
        }

        var timeoutAt = AddPolicyDuration(
            dispatchedAtUtc,
            policy.Timeout,
            message.Priority == Priority.Critical,
            policy);
        return new OccupantResponsePolicyPlan(reminders, timeoutAt);
    }

    public static bool IsEligible(OrgMessage message) =>
        message is Hive.Domain.Messaging.Directive or PeerRequest or Escalation or ApprovalRequest;

    public static MessageId TimeoutEscalationId(
        PositionEntityId entityId,
        MessageId sourceMessageId,
        DateTimeOffset timeoutAtUtc)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        ArgumentNullException.ThrowIfNull(sourceMessageId);
        RequireUtc(timeoutAtUtc, nameof(timeoutAtUtc));
        var identity = $"{entityId.Value}\n{sourceMessageId.Value:D}\n{timeoutAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)}";
        return MessageId.From(DeterministicGuid(
            "hive:occupant-response:timeout-escalation",
            identity));
    }

    private static DateTimeOffset AddPolicyDuration(
        DateTimeOffset startUtc,
        TimeSpan duration,
        bool critical,
        OccupantResponsePolicyRuntimeConfiguration policy) =>
        critical
            ? startUtc.Add(duration)
            : AddWorkingDuration(startUtc, duration, policy);

    private static DateTimeOffset AddWorkingDuration(
        DateTimeOffset startUtc,
        TimeSpan duration,
        OccupantResponsePolicyRuntimeConfiguration policy)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId);
        var remaining = duration;
        var currentUtc = startUtc;

        while (remaining > TimeSpan.Zero)
        {
            var local = TimeZoneInfo.ConvertTime(currentUtc, timeZone);
            var date = DateOnly.FromDateTime(local.DateTime);
            var time = TimeOnly.FromDateTime(local.DateTime);
            if (time < policy.WorkingHoursStart)
            {
                currentUtc = LocalToUtc(date, policy.WorkingHoursStart, timeZone);
            }
            else if (time >= policy.WorkingHoursEnd)
            {
                currentUtc = LocalToUtc(date.AddDays(1), policy.WorkingHoursStart, timeZone);
            }

            local = TimeZoneInfo.ConvertTime(currentUtc, timeZone);
            date = DateOnly.FromDateTime(local.DateTime);
            var endUtc = LocalToUtc(date, policy.WorkingHoursEnd, timeZone);
            var available = endUtc - currentUtc;
            if (available <= TimeSpan.Zero)
            {
                currentUtc = LocalToUtc(date.AddDays(1), policy.WorkingHoursStart, timeZone);
                continue;
            }

            if (remaining <= available)
            {
                return currentUtc.Add(remaining);
            }

            remaining -= available;
            currentUtc = LocalToUtc(date.AddDays(1), policy.WorkingHoursStart, timeZone);
        }

        return currentUtc;
    }

    private static DateTimeOffset LocalToUtc(
        DateOnly date,
        TimeOnly time,
        TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }

    private static Guid DeterministicGuid(string namespaceName, string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(namespaceName + "\n" + identity));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Occupant response policy timestamps must be expressed as UTC offsets.",
                parameterName);
        }
    }
}

internal interface IOccupantResponseScheduler
{
    void Schedule(IActorContext context, IActorRef receiver, object command, TimeSpan delay);
}

internal sealed class AkkaOccupantResponseScheduler : IOccupantResponseScheduler
{
    public static AkkaOccupantResponseScheduler Instance { get; } = new();

    private AkkaOccupantResponseScheduler()
    {
    }

    public void Schedule(IActorContext context, IActorRef receiver, object command, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(command);
        context.System.Scheduler.ScheduleTellOnce(
            delay < TimeSpan.Zero ? TimeSpan.Zero : delay,
            receiver,
            command,
            receiver);
    }
}

internal interface IOccupantResponseEscalationTargetResolver
{
    ValueTask<EndpointRef?> ResolveAsync(
        PositionEntityId entityId,
        CancellationToken cancellationToken = default);
}

internal sealed class OrganizationRelationsOccupantResponseEscalationTargetResolver(
    IOrganizationRelations relations) : IOccupantResponseEscalationTargetResolver
{
    public async ValueTask<EndpointRef?> ResolveAsync(
        PositionEntityId entityId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        var superior = await relations.GetDirectSuperiorAsync(
                entityId.Organization,
                entityId.Position,
                cancellationToken)
            .ConfigureAwait(false);
        return superior is not null
            ? new PositionEndpointRef(superior)
            : await relations.GetOrganizationOwnerAsync(entityId.Organization, cancellationToken)
                .ConfigureAwait(false);
    }
}

internal interface IOccupantResponseKillSwitch
{
    void Request(ActorSystem system, OccupantResponseKillSwitchRequest request);
}

internal sealed record OccupantResponseKillSwitchRequest(
    PositionEntityId EntityId,
    MessageId SourceMessageId,
    ThreadId ThreadId,
    DateTimeOffset OccurredAtUtc);

internal sealed class EventStreamOccupantResponseKillSwitch : IOccupantResponseKillSwitch
{
    public static EventStreamOccupantResponseKillSwitch Instance { get; } = new();

    private EventStreamOccupantResponseKillSwitch()
    {
    }

    public void Request(ActorSystem system, OccupantResponseKillSwitchRequest request)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);
        system.EventStream.Publish(request);
    }
}

internal sealed record InitializeOccupantResponsePolicy(MessageId MessageId);

internal sealed record TriggerOccupantResponseReminder(
    MessageId MessageId,
    OccupantReminderId ReminderId,
    MessageId TriggerMessageId,
    int Index);

internal sealed record TriggerOccupantResponseTimeout(MessageId MessageId);

internal sealed record OccupantResponseTimeoutTargetResolved(
    MessageId MessageId,
    DateTimeOffset ScheduledForUtc,
    EndpointRef? Target);

internal sealed record OccupantResponseTimeoutTargetResolutionFailed(
    MessageId MessageId,
    Exception Cause);

internal sealed record OccupantResponseTimeoutValidationCompleted(
    MessageId SourceMessageId,
    DateTimeOffset ScheduledForUtc,
    Escalation Escalation,
    ValidationResult Validation);

internal sealed record OccupantResponseTimeoutValidationFailed(
    MessageId SourceMessageId,
    Exception Cause);
