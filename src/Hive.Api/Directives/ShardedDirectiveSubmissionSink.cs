using Hive.Actors.Sharding;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Api.Directives;

internal sealed class ShardedDirectiveSubmissionSink : IDirectiveSubmissionSink
{
    private readonly DirectiveRoutingValidator _routingValidator;
    private readonly IPositionCommandDispatcher _dispatcher;

    public ShardedDirectiveSubmissionSink(
        DirectiveRoutingValidator routingValidator,
        IPositionCommandDispatcher dispatcher)
    {
        _routingValidator = routingValidator
            ?? throw new ArgumentNullException(nameof(routingValidator));
        _dispatcher = dispatcher
            ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async ValueTask<DirectiveSubmissionResult> SubmitAsync(
        Directive directive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directive);
        cancellationToken.ThrowIfCancellationRequested();

        var validation = await _routingValidator
            .ValidateAsync(directive, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return DirectiveSubmissionResult.Rejected(
                directive,
                RoutingRejection.Create(
                    RoutingValidationContext.ForMessage(directive),
                    validation));
        }

        var target = (PositionEndpointRef)directive.To;
        var envelope = PositionEnvelope.For(
            PositionEntityId.From(directive.OrganizationId, target.PositionId),
            new AcceptMessage(directive));

        await _dispatcher
            .DispatchAsync(envelope, cancellationToken)
            .ConfigureAwait(false);

        return DirectiveSubmissionResult.Accepted(directive);
    }
}
