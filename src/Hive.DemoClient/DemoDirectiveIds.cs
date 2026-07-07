namespace Hive.DemoClient;

public sealed record DemoDirectiveIds(
    Guid MessageId,
    Guid ThreadId,
    Guid DirectiveId)
{
    public static DemoDirectiveIds New() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
}
