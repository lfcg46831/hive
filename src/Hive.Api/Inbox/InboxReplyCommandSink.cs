using Hive.Actors.Sharding;
using Hive.Api.Directives;
using Hive.Domain.Identity;
using Hive.Domain.Positions;

namespace Hive.Api.Inbox;

internal interface IInboxReplyCommandSink
{
    bool IsAvailable { get; }

    ValueTask<OccupantReplyEmissionResult> EmitAsync(
        PositionEntityId sourcePosition,
        EmitOccupantReply command,
        CancellationToken cancellationToken);
}

internal sealed class ShardedInboxReplyCommandSink(IPositionCommandRequester requester)
    : IInboxReplyCommandSink
{
    public bool IsAvailable => true;

    public async ValueTask<OccupantReplyEmissionResult> EmitAsync(
        PositionEntityId sourcePosition,
        EmitOccupantReply command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePosition);
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            return await requester.RequestAsync<OccupantReplyEmissionResult>(
                    PositionEnvelope.For(sourcePosition, command),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InboxReplyEmissionUnavailableException(exception);
        }
    }
}

internal sealed class InboxReplyEmissionUnavailableException(Exception innerException)
    : Exception("Human inbox reply emission is unavailable.", innerException);

internal sealed class UnavailableInboxReplyCommandSink : IInboxReplyCommandSink
{
    public static UnavailableInboxReplyCommandSink Instance { get; } = new();

    public bool IsAvailable => false;

    public ValueTask<OccupantReplyEmissionResult> EmitAsync(
        PositionEntityId sourcePosition,
        EmitOccupantReply command,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<OccupantReplyEmissionResult>(
            new InvalidOperationException("Human inbox reply emission is unavailable."));
}
