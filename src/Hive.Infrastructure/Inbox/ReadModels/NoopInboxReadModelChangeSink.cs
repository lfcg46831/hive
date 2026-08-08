namespace Hive.Infrastructure.Inbox.ReadModels;

internal sealed class NoopInboxReadModelChangeSink : IInboxReadModelChangeSink
{
    public static readonly NoopInboxReadModelChangeSink Instance = new();

    private NoopInboxReadModelChangeSink()
    {
    }

    public ValueTask ProjectionChangedAsync(
        InboxProjectionChange change,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask InteractionChangedAsync(
        InboxInteractionMutation mutation,
        InboxInteractionState state,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
