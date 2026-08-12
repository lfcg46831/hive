using Hive.Domain.Identity;
using Hive.Domain.OccupantChannels;

namespace Hive.Actors.Positions;

internal sealed record HumanOccupantReminderDelivery(
    MessageId SourceMessageId,
    OccupantReminderId ReminderId,
    OccupantChannelDeliveryContext Context);

internal sealed record PositionOccupantReminderDeliveryReported(
    MessageId SourceMessageId,
    OccupantReminderId ReminderId,
    MessageId TriggerMessageId,
    OccupantChannelBindingId? BindingId,
    OccupantChannelDeliveryResult Result);
