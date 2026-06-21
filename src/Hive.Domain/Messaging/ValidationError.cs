namespace Hive.Domain.Messaging;

public sealed record ValidationError(
    string Code,
    string Path,
    RejectionReason Reason);
