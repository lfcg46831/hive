using Hive.Infrastructure.Inbox.ReadModels;

namespace Hive.Api.Inbox;

public interface IInboxInteractionCommandSink
{
    bool IsAvailable { get; }

    ValueTask<InboxInteractionState> ApplyAsync(
        InboxInteractionMutation mutation,
        CancellationToken cancellationToken);
}

internal sealed class DurableInboxInteractionCommandSink(IInboxInteractionStore store) :
    IInboxInteractionCommandSink
{
    public bool IsAvailable => store.IsAvailable;

    public ValueTask<InboxInteractionState> ApplyAsync(
        InboxInteractionMutation mutation,
        CancellationToken cancellationToken) =>
        store.ApplyAsync(mutation, cancellationToken);
}

internal sealed class UnavailableInboxInteractionCommandSink : IInboxInteractionCommandSink
{
    public static UnavailableInboxInteractionCommandSink Instance { get; } = new();

    private UnavailableInboxInteractionCommandSink()
    {
    }

    public bool IsAvailable => false;

    public ValueTask<InboxInteractionState> ApplyAsync(
        InboxInteractionMutation mutation,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The inbox interaction store is unavailable.");
}
