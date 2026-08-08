namespace Hive.Infrastructure.Inbox.ReadModels;

/// <summary>
/// Receives best-effort notifications after durable inbox changes commit. Consumers must not let
/// notification failures fail the owning durable write; REST snapshots remain authoritative.
/// </summary>
public interface IInboxReadModelChangeSink
{
    ValueTask ProjectionChangedAsync(
        InboxProjectionChange change,
        CancellationToken cancellationToken = default);

    ValueTask InteractionChangedAsync(
        InboxInteractionMutation mutation,
        InboxInteractionState state,
        CancellationToken cancellationToken = default);
}
