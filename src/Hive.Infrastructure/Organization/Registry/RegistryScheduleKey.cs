using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.Registry;

public sealed record RegistryScheduleKey(PositionId PositionId, string ScheduleId)
{
    public override string ToString() => $"{PositionId.Value}/{ScheduleId}";
}
