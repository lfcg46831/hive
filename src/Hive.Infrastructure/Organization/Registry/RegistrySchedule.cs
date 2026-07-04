using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.Registry;

public sealed record RegistrySchedule(
    PositionId PositionId,
    string ScheduleId,
    bool IsActive,
    string Cron,
    string Priority,
    bool IsCritical,
    string CatchUp,
    string Instruction);
