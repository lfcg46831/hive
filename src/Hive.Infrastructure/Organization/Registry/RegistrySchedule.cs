using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.Registry;

public sealed record RegistrySchedule(
    PositionId PositionId,
    string ScheduleId,
    string Cron,
    string Instruction);
